using IncidentIQ.Api.Contracts;
using IncidentIQ.Domain.Entities;
using IncidentIQ.Domain.Enums;
using IncidentIQ.Persistence;
using IncidentIQ.Shared.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Environment = IncidentIQ.Domain.Entities.Environment;

namespace IncidentIQ.Api.Endpoints;

/// <summary>
/// Recording that something shipped.
///
/// This was the gap that made the product's best sentence unreachable. The
/// detection rule that correlates a new error with a release, the ranking that
/// scores a deployment minutes before an incident as the strongest evidence
/// available, and the panel on the incident page were all built and all
/// correct - and none of them could ever fire, because nothing in the system
/// wrote a <c>deployments</c> row. Measured: adding one row by hand moved an
/// incident from "the evidence does not identify a cause" at 20% confidence to
/// a named release and a mechanism at 35%, and a richer incident from 40% to
/// 60%.
///
/// Written straight to PostgreSQL rather than published to Kafka, which is a
/// departure from how log events arrive and is deliberate. Deployments are a
/// few rows a day, not tens of thousands a second, so the queue buys no
/// throughput; and the value of the row is entirely in it being *there* when
/// detection asks "what shipped just before this?" seconds later. A queue hop
/// adds a window in which the answer is wrong. The <c>deployments.created</c>
/// topic stays declared and unused rather than being published to with no
/// consumer, which is the same problem one layer along.
/// </summary>
public static class DeploymentEndpoints
{
    private const int MaxVersionLength = 100;
    private const int MaxCommitShaLength = 64;

    /// <summary>
    /// How far ahead of now a deployment may claim to have happened.
    ///
    /// Small, because a future deployment sorts to the top of every "most
    /// recent release" query and would be blamed for incidents that started
    /// before it. Some slack for clock skew between a CI runner and this
    /// service, and no more.
    /// </summary>
    private static readonly TimeSpan MaxClockSkewAhead = TimeSpan.FromMinutes(5);

    /// <summary>Matches the ingestion validator, so a backfill is bounded the same way.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    public static IEndpointRouteBuilder MapDeploymentEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/v1/deployments", RecordAsync)
            .WithName("RecordDeployment")
            .WithTags("deployments");

        return routes;
    }

    private static async Task<IResult> RecordAsync(
        [FromServices] IncidentIQDbContext db,
        [FromServices] TimeProvider timeProvider,
        HttpContext http,
        [FromBody] RecordDeploymentRequest? request,
        CancellationToken cancellationToken)
    {
        var tenant = http.GetTenantContext();

        if (tenant is null)
        {
            return Problem(http, StatusCodes.Status401Unauthorized, "Not authenticated",
                "The request carried no usable API key.");
        }

        var service = request?.Service?.Trim();
        var environment = request?.Environment?.Trim();
        var version = request?.Version?.Trim();

        if (string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(environment))
        {
            return Problem(http, StatusCodes.Status400BadRequest, "Service and environment are required",
                "A release is to one service in one environment; a deployment row that names neither "
                + "cannot be correlated with anything.");
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            return Problem(http, StatusCodes.Status400BadRequest, "Version is required",
                "The version is what the incident page names as the suspect. Without it the "
                + "correlation can only say that something shipped.");
        }

        if (version.Length > MaxVersionLength)
        {
            return Problem(http, StatusCodes.Status400BadRequest, "Version is too long",
                $"Versions are limited to {MaxVersionLength} characters; this one is {version.Length}.");
        }

        var status = DeploymentStatus.Succeeded;

        if (!string.IsNullOrWhiteSpace(request?.Status)
            && !Enum.TryParse(request.Status, ignoreCase: true, out status))
        {
            return Problem(http, StatusCodes.Status400BadRequest, "Unknown status",
                $"'{request.Status}' is not a deployment status. Expected one of: "
                + $"{string.Join(", ", Enum.GetNames<DeploymentStatus>())}.");
        }

        var now = timeProvider.GetUtcNow();
        var deployedAt = request?.DeployedAt ?? now;

        if (deployedAt > now + MaxClockSkewAhead)
        {
            return Problem(http, StatusCodes.Status400BadRequest, "Deployment is in the future",
                $"deployedAt is more than {MaxClockSkewAhead.TotalMinutes:N0} minutes ahead of this "
                + "server's clock. A future release outranks every real one in correlation queries.");
        }

        if (deployedAt < now - MaxAge)
        {
            return Problem(http, StatusCodes.Status400BadRequest, "Deployment is too old",
                $"deployedAt is more than {MaxAge.TotalDays:N0} days ago, which is outside every "
                + "window that would correlate it with an incident.");
        }

        // Created if unknown, exactly as an unrecognised service name in a log
        // batch is. CI often reports a release before the service has logged
        // anything, and refusing the first deployment of a new service would
        // make this useless precisely when the correlation is most wanted.
        var serviceId = await ResolveServiceAsync(db, tenant.TenantId, service, cancellationToken);
        var environmentId = await ResolveEnvironmentAsync(db, tenant.TenantId, environment, cancellationToken);

        var deployment = new Deployment
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = tenant.TenantId,
            MonitoredServiceId = serviceId,
            EnvironmentId = environmentId,
            Version = version,
            CommitSha = Truncate(request?.CommitSha?.Trim(), MaxCommitShaLength),
            DeployedBy = request?.DeployedBy?.Trim(),
            DeployedAt = deployedAt,
            Status = status,
            CreatedAt = now
        };

        db.Deployments.Add(deployment);
        await db.SaveChangesAsync(cancellationToken);

        // Incidents that began after this release and are still open. The
        // analysis would find this deployment on its own - its repository falls
        // back to a time window when the incident points at nothing - but an
        // incident opened in the seconds before CI got around to reporting the
        // release would otherwise carry a null suspect on its own page forever.
        var correlated = await db.Incidents
            .Where(i => i.MonitoredServiceId == serviceId
                        && i.EnvironmentId == environmentId
                        && i.SuspectedDeploymentId == null
                        && i.FirstSeenAt >= deployedAt
                        && i.FirstSeenAt <= deployedAt.Add(CorrelationWindow)
                        && (i.Status == IncidentStatus.Detected || i.Status == IncidentStatus.Investigating))
            .ToListAsync(cancellationToken);

        foreach (var incident in correlated)
        {
            incident.SuspectedDeploymentId = deployment.Id;
            incident.UpdatedAt = now;
        }

        if (correlated.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return Results.Created($"/api/v1/deployments/{deployment.Id}", new RecordDeploymentResult
        {
            DeploymentId = deployment.Id,
            Service = service,
            Environment = environment,
            Version = version,
            DeployedAt = deployedAt,
            CorrelatedIncidentIds = correlated.Select(i => i.Id).ToList()
        });
    }

    /// <summary>
    /// How long after a release an incident is still plausibly its fault.
    ///
    /// Matches the detector's own correlation window. Longer would attach a
    /// morning's release to an afternoon's unrelated outage, which is worse
    /// than attaching nothing: a wrong suspect is acted on, an absent one is
    /// investigated.
    /// </summary>
    private static readonly TimeSpan CorrelationWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Insert-if-absent then read.
    ///
    /// Two CI runners finishing a deploy of the same new service in the same
    /// second is unlikely and entirely possible; ON CONFLICT makes the loser
    /// read the winner's row instead of failing the release notification.
    /// </summary>
    private static async Task<Guid> ResolveServiceAsync(
        IncidentIQDbContext db, Guid organizationId, string key, CancellationToken cancellationToken)
    {
        var normalized = key.ToLowerInvariant();

        var existing = await db.MonitoredServices.AsNoTracking()
            .Where(s => s.Key == normalized)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is { } id)
        {
            return id;
        }

        var created = new MonitoredService
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            Key = normalized,
            DisplayName = normalized,
            IsActive = true
        };

        db.MonitoredServices.Add(created);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return created.Id;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            db.Entry(created).State = EntityState.Detached;

            return await db.MonitoredServices.AsNoTracking()
                .Where(s => s.Key == normalized)
                .Select(s => s.Id)
                .FirstAsync(cancellationToken);
        }
    }

    private static async Task<Guid> ResolveEnvironmentAsync(
        IncidentIQDbContext db, Guid organizationId, string key, CancellationToken cancellationToken)
    {
        var normalized = key.ToLowerInvariant();

        var existing = await db.Environments.AsNoTracking()
            .Where(e => e.Key == normalized)
            .Select(e => (Guid?)e.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is { } id)
        {
            return id;
        }

        var created = new Environment
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            Key = normalized,
            DisplayName = normalized,
            IsProduction = normalized is "production" or "prod"
        };

        db.Environments.Add(created);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return created.Id;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            db.Entry(created).State = EntityState.Detached;

            return await db.Environments.AsNoTracking()
                .Where(e => e.Key == normalized)
                .Select(e => e.Id)
                .FirstAsync(cancellationToken);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null
            : value.Length > maxLength ? value[..maxLength] : value;

    private static IResult Problem(HttpContext http, int statusCode, string title, string detail) =>
        Results.Problem(
            title: title,
            detail: detail,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?> { ["correlationId"] = http.GetCorrelationId() });
}
