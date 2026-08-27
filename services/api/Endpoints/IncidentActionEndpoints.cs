using IncidentIQ.Api.Contracts;
using IncidentIQ.Contracts;
using IncidentIQ.Contracts.Payloads;
using IncidentIQ.Domain.Enums;
using IncidentIQ.Incidents;
using IncidentIQ.Persistence;
using IncidentIQ.Shared.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IncidentIQ.Api.Endpoints;

/// <summary>
/// The write side of an incident: taking it, handing it on, closing it, adding
/// what you found, and asking for another look.
///
/// Every one of these has to be attributable to a person. There is no login
/// flow yet, so the actor comes from the API key, which may be bound to a user
/// row. A key without one is refused rather than defaulted to somebody - an
/// incident timeline that names the wrong person is worse than one that says
/// the action could not be performed.
///
/// The rules themselves live in <see cref="IncidentLifecycleService"/>, not
/// here. This file translates HTTP into a call and an exception into a status
/// code, and nothing else: the legality of Detected -> Resolved is a property
/// of the domain, and putting it behind an endpoint would leave the detector
/// free to disagree with the dashboard.
/// </summary>
public static class IncidentActionEndpoints
{
    private const int MaxNoteLength = 4000;

    public static IEndpointRouteBuilder MapIncidentActionEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/incidents/{id:guid}").WithTags("incident-actions");

        group.MapPost("/acknowledge", AcknowledgeAsync).WithName("AcknowledgeIncident");
        group.MapPost("/resolve", ResolveAsync).WithName("ResolveIncident");
        group.MapPost("/ignore", IgnoreAsync).WithName("IgnoreIncident");
        group.MapPost("/reopen", ReopenAsync).WithName("ReopenIncident");
        group.MapPost("/assign", AssignAsync).WithName("AssignIncident");
        group.MapPost("/notes", AddNoteAsync).WithName("AddIncidentNote");
        group.MapPost("/analyze", AnalyzeAsync).WithName("AnalyzeIncident");

        routes.MapGet("/api/v1/users", ListMembersAsync).WithName("ListOrganizationMembers").WithTags("team");

        return routes;
    }

    private static Task<IResult> AcknowledgeAsync(
        [FromServices] IncidentLifecycleService lifecycle,
        [FromServices] IncidentIQDbContext db,
        HttpContext http, Guid id, CancellationToken cancellationToken) =>
        PerformAsync(db, http, id, (tenant, actor) =>
            lifecycle.StartInvestigatingAsync(tenant, id, actor, cancellationToken), cancellationToken);

    private static Task<IResult> ResolveAsync(
        [FromServices] IncidentLifecycleService lifecycle,
        [FromServices] IncidentIQDbContext db,
        HttpContext http, Guid id,
        [FromBody] ResolveIncidentRequest? request,
        CancellationToken cancellationToken) =>
        PerformAsync(db, http, id, (tenant, actor) =>
            lifecycle.ResolveAsync(tenant, id, actor, Trim(request?.ResolutionNotes), cancellationToken),
            cancellationToken);

    private static async Task<IResult> IgnoreAsync(
        [FromServices] IncidentLifecycleService lifecycle,
        [FromServices] IncidentIQDbContext db,
        HttpContext http, Guid id,
        [FromBody] ReasonRequest? request,
        CancellationToken cancellationToken)
    {
        var reason = Trim(request?.Reason);

        if (reason is null)
        {
            // Ignoring is the one transition with no evidence behind it, so the
            // reason is the only record of why. Required, not optional.
            return Problem(http, StatusCodes.Status400BadRequest, "A reason is required",
                "Ignoring an incident records a decision rather than a fix, so it must say why.");
        }

        return await PerformAsync(db, http, id, (tenant, actor) =>
            lifecycle.IgnoreAsync(tenant, id, actor, reason, cancellationToken), cancellationToken);
    }

    private static async Task<IResult> ReopenAsync(
        [FromServices] IncidentLifecycleService lifecycle,
        [FromServices] IncidentIQDbContext db,
        HttpContext http, Guid id,
        [FromBody] ReasonRequest? request,
        CancellationToken cancellationToken)
    {
        var reason = Trim(request?.Reason);

        if (reason is null)
        {
            return Problem(http, StatusCodes.Status400BadRequest, "A reason is required",
                "Reopening contradicts an earlier resolution, so it must say what was missed.");
        }

        return await PerformAsync(db, http, id, (tenant, actor) =>
            lifecycle.ReopenAsync(tenant, id, actor, reason, cancellationToken), cancellationToken);
    }

    private static async Task<IResult> AssignAsync(
        [FromServices] IncidentLifecycleService lifecycle,
        [FromServices] IncidentIQDbContext db,
        HttpContext http, Guid id,
        [FromBody] AssignIncidentRequest request,
        CancellationToken cancellationToken)
    {
        var tenant = http.GetTenantContext();

        // The assignee is looked up rather than trusted, which also keeps the
        // global query filter in play: a user id from another organization
        // simply does not resolve.
        var assignee = tenant is null
            ? null
            : await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == request.UserId, cancellationToken);

        if (assignee is null)
        {
            return Problem(http, StatusCodes.Status400BadRequest, "Unknown assignee",
                $"No user {request.UserId} exists in this organization.");
        }

        return await PerformAsync(db, http, id, (org, actor) =>
            lifecycle.AssignAsync(org, id, assignee.Id, actor, assignee.DisplayName, cancellationToken),
            cancellationToken);
    }

    private static async Task<IResult> AddNoteAsync(
        [FromServices] IncidentLifecycleService lifecycle,
        [FromServices] IncidentIQDbContext db,
        HttpContext http, Guid id,
        [FromBody] AddNoteRequest? request,
        CancellationToken cancellationToken)
    {
        var note = Trim(request?.Note);

        if (note is null)
        {
            return Problem(http, StatusCodes.Status400BadRequest, "The note is empty",
                "A note needs text. Nothing was recorded.");
        }

        if (note.Length > MaxNoteLength)
        {
            return Problem(http, StatusCodes.Status400BadRequest, "The note is too long",
                $"Notes are limited to {MaxNoteLength} characters; this one is {note.Length}.");
        }

        return await PerformAsync(db, http, id, (tenant, actor) =>
            lifecycle.AddNoteAsync(tenant, id, actor, note, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Queues a fresh AI analysis.
    ///
    /// Goes through the outbox rather than producing to Kafka directly, for the
    /// same reason the detector does: the request is committed with the same
    /// transaction that decided to make it, so a broker that is down delays the
    /// analysis instead of losing it.
    /// </summary>
    private static async Task<IResult> AnalyzeAsync(
        [FromServices] IncidentIQDbContext db,
        [FromServices] IOutboxWriter outbox,
        [FromServices] TimeProvider timeProvider,
        HttpContext http, Guid id, CancellationToken cancellationToken)
    {
        var tenant = http.GetTenantContext();

        if (tenant is null)
        {
            return Problem(http, StatusCodes.Status401Unauthorized, "Not authenticated",
                "The request carried no usable API key.");
        }

        var exists = await db.Incidents.AsNoTracking().AnyAsync(i => i.Id == id, cancellationToken);

        if (!exists)
        {
            return Problem(http, StatusCodes.Status404NotFound, "Incident not found",
                $"No incident {id} exists in this organization.");
        }

        // Versions are per incident and monotonic; the worker treats
        // (incidentId, analysisVersion) as its idempotency key, so a redelivered
        // request writes nothing.
        var currentVersion = await db.AiAnalyses.AsNoTracking()
            .Where(a => a.IncidentId == id)
            .MaxAsync(a => (int?)a.AnalysisVersion, cancellationToken) ?? 0;

        var version = currentVersion + 1;
        var now = timeProvider.GetUtcNow();
        var correlationId = http.GetCorrelationId();

        var envelope = EventEnvelope<IncidentAnalysisRequested>.Create(
            EventTypes.IncidentAnalysisRequested,
            tenant.TenantId,
            new IncidentAnalysisRequested
            {
                IncidentId = id,
                AnalysisVersion = version,
                Reason = "requested",
                RequestedAt = now
            },
            correlationId);

        outbox.Enqueue(new OutboxEnqueueRequest
        {
            OrganizationId = tenant.TenantId,
            AggregateType = "Incident",
            AggregateId = id,
            EventType = EventTypes.IncidentAnalysisRequested,
            Topic = Topics.IncidentsAnalysisRequested,
            // The same partition key every other event about this incident
            // uses, so its lifecycle stays ordered on one partition.
            PartitionKey = PartitionKeys.ForIncident(tenant.TenantId, id),
            SerialisedEnvelope = EventJson.Serialize(envelope),
            EventId = envelope.EventId,
            CorrelationId = envelope.CorrelationId,
            OccurredAt = now
        });

        await db.SaveChangesAsync(cancellationToken);

        // 202: the analysis has been asked for, not performed. It arrives on the
        // incident a few seconds later.
        return Results.Accepted(
            $"/api/v1/incidents/{id}",
            new AnalyzeIncidentResult { IncidentId = id, AnalysisVersion = version });
    }

    private static async Task<IResult> ListMembersAsync(
        [FromServices] IncidentIQDbContext db,
        CancellationToken cancellationToken)
    {
        var members = await db.Users.AsNoTracking()
            .OrderBy(u => u.DisplayName)
            .Select(u => new OrganizationMember
            {
                UserId = u.Id,
                DisplayName = u.DisplayName,
                Email = u.Email
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(members);
    }

    /// <summary>
    /// The shape every action shares: establish who is acting, confirm the
    /// incident is visible, run the transition, and translate a refusal.
    /// </summary>
    private static async Task<IResult> PerformAsync(
        IncidentIQDbContext db,
        HttpContext http,
        Guid id,
        Func<Guid, Guid, Task<Domain.Entities.Incident>> action,
        CancellationToken cancellationToken)
    {
        var tenant = http.GetTenantContext();

        if (tenant is null)
        {
            return Problem(http, StatusCodes.Status401Unauthorized, "Not authenticated",
                "The request carried no usable API key.");
        }

        if (tenant.ActorUserId is null)
        {
            // Deliberately 403 and not a silent default. Attributing a
            // resolution to whoever happens to be first in the users table
            // would put a name on the timeline that did not do the thing.
            return Problem(http, StatusCodes.Status403Forbidden, "This API key cannot act as a user",
                $"Key '{tenant.ApiKeyName}' is not bound to a user, so an action that must be attributed "
                + "to a person cannot be performed with it. Bind it to a user, or use a key that is.");
        }

        // Checked before the transition so "no such incident" is a 404 rather
        // than being folded into the 409 the domain raises for both.
        var exists = await db.Incidents.AsNoTracking().AnyAsync(i => i.Id == id, cancellationToken);

        if (!exists)
        {
            return Problem(http, StatusCodes.Status404NotFound, "Incident not found",
                $"No incident {id} exists in this organization.");
        }

        try
        {
            var incident = await action(tenant.TenantId, tenant.ActorUserId.Value);

            return Results.Ok(new IncidentActionResult
            {
                Id = incident.Id,
                Status = incident.Status.ToString()
            });
        }
        catch (InvalidIncidentTransitionException exception)
        {
            // 409, because the request was well formed and the incident simply
            // is not in a state where it applies - almost always two people
            // acting on the same stale screen. The domain's message names both
            // states, which is what the second person needs to read.
            return Problem(http, StatusCodes.Status409Conflict, "That action does not apply", exception.Message);
        }
    }

    /// <summary>The transitions legal from a status, mirroring the lifecycle service.</summary>
    public static IReadOnlyList<string> AvailableActionsFor(IncidentStatus status) => status switch
    {
        IncidentStatus.Detected => ["acknowledge", "assign", "resolve", "ignore", "notes", "analyze"],
        IncidentStatus.Investigating => ["assign", "resolve", "ignore", "notes", "analyze"],
        IncidentStatus.Resolved => ["reopen", "notes", "analyze"],
        IncidentStatus.Ignored => ["reopen", "notes", "analyze"],
        _ => ["notes"]
    };

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IResult Problem(HttpContext http, int statusCode, string title, string detail) =>
        Results.Problem(
            title: title,
            detail: detail,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?> { ["correlationId"] = http.GetCorrelationId() });
}
