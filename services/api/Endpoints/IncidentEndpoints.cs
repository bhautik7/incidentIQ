using System.Text.Json;
using IncidentIQ.Api.Contracts;
using IncidentIQ.Domain.Entities;
using IncidentIQ.Domain.Enums;
using IncidentIQ.Persistence;
using IncidentIQ.Shared.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IncidentIQ.Api.Endpoints;

/// <summary>
/// Read endpoints for the dashboard.
///
/// Tenant scoping is not done here. Authentication puts the organization on
/// the ambient tenant context, and EF's global query filters apply it to every
/// query in this file automatically. That is deliberate: a filter someone has
/// to remember to write is a filter someone will eventually forget, and the
/// failure mode of forgetting is a cross-tenant leak. Here the failure mode of
/// a missing tenant is an empty result.
/// </summary>
public static class IncidentEndpoints
{
    private const int MaxPageSize = 100;
    private const int MaxSamples = 20;

    public static IEndpointRouteBuilder MapIncidentEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1").WithTags("incidents");

        group.MapGet("/incidents", ListIncidentsAsync).WithName("ListIncidents");
        group.MapGet("/incidents/{id:guid}", GetIncidentAsync).WithName("GetIncident");
        group.MapGet("/stats", GetStatsAsync).WithName("GetStats");
        group.MapGet("/services", ListServicesAsync).WithName("ListServices");

        return routes;
    }

    private static async Task<IResult> ListIncidentsAsync(
        [FromServices] IncidentIQDbContext db,
        HttpContext http,
        [FromQuery] string? status,
        [FromQuery] string? severity,
        [FromQuery] string? service,
        [FromQuery] string? environment,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = db.Incidents.AsNoTracking();

        // "active" is the default view because it is the question the product
        // exists to answer: what is broken right now.
        if (string.IsNullOrWhiteSpace(status) || status.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(i => i.Status == IncidentStatus.Detected || i.Status == IncidentStatus.Investigating);
        }
        else if (!status.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            if (!Enum.TryParse<IncidentStatus>(status, ignoreCase: true, out var parsedStatus))
            {
                return Problem(http, StatusCodes.Status400BadRequest, "Unknown status",
                    $"'{status}' is not a status. Expected one of: active, all, {string.Join(", ", Enum.GetNames<IncidentStatus>())}.");
            }

            query = query.Where(i => i.Status == parsedStatus);
        }

        if (!string.IsNullOrWhiteSpace(severity))
        {
            if (!Enum.TryParse<IncidentSeverity>(severity, ignoreCase: true, out var parsedSeverity))
            {
                return Problem(http, StatusCodes.Status400BadRequest, "Unknown severity",
                    $"'{severity}' is not a severity. Expected one of: {string.Join(", ", Enum.GetNames<IncidentSeverity>())}.");
            }

            query = query.Where(i => i.Severity == parsedSeverity);
        }

        if (!string.IsNullOrWhiteSpace(service))
        {
            query = query.Where(i => i.MonitoredService.Key == service);
        }

        if (!string.IsNullOrWhiteSpace(environment))
        {
            query = query.Where(i => i.Environment.Key == environment);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(i => EF.Functions.ILike(i.Title, term));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        // Most recently active first. An incident that stopped an hour ago
        // matters less than one still firing, whatever their severities.
        var items = await query
            .OrderByDescending(i => i.LastSeenAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => new IncidentListItem
            {
                Id = i.Id,
                Title = i.Title,
                Status = i.Status.ToString(),
                Severity = i.Severity.ToString(),
                DetectionRule = i.DetectionRule.ToString(),
                Service = i.MonitoredService.Key,
                Environment = i.Environment.Key,
                OccurrenceCount = i.OccurrenceCount,
                FirstSeenAt = i.FirstSeenAt,
                LastSeenAt = i.LastSeenAt,
                SuspectedDeploymentVersion = i.SuspectedDeployment != null ? i.SuspectedDeployment.Version : null,
                HasAnalysis = i.Analyses.Any(a => a.Status == AiAnalysisStatus.Completed),
                AnalysisConfidence = i.Analyses
                    .Where(a => a.Status == AiAnalysisStatus.Completed)
                    .OrderByDescending(a => a.AnalysisVersion)
                    .Select(a => a.Confidence)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(new PagedResult<IncidentListItem>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        });
    }

    private static async Task<IResult> GetIncidentAsync(
        [FromServices] IncidentIQDbContext db,
        HttpContext http,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var incident = await db.Incidents
            .AsNoTracking()
            .Include(i => i.MonitoredService)
            .Include(i => i.Environment)
            .Include(i => i.LogPattern)
            .Include(i => i.SuspectedDeployment)
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

        if (incident is null)
        {
            // The query filter makes another tenant's incident invisible rather
            // than forbidden, so this is a 404 and not a 403 - which is also
            // the right answer, since confirming existence would leak it.
            return Problem(http, StatusCodes.Status404NotFound, "Incident not found",
                $"No incident {id} exists in this organization.");
        }

        var analysis = await db.AiAnalyses
            .AsNoTracking()
            .Where(a => a.IncidentId == id && a.Status == AiAnalysisStatus.Completed)
            .OrderByDescending(a => a.AnalysisVersion)
            .FirstOrDefaultAsync(cancellationToken);

        var timeline = await db.IncidentEvents
            .AsNoTracking()
            .Where(e => e.IncidentId == id)
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id)
            .Select(e => new IncidentTimelineEntry
            {
                Type = e.Type.ToString(),
                OccurredAt = e.OccurredAt,
                ActorType = e.ActorType.ToString(),
                Message = e.Message
            })
            .ToListAsync(cancellationToken);

        // Sampled lines are fetched by pattern, not by incident.
        //
        // log_events.incident_id looks like the obvious join, but it is never
        // populated: the processor writes samples while normalising, which
        // happens before detection has decided an incident exists at all.
        // The pattern is the link that is actually written, and it is the more
        // durable one anyway - samples belong to a pattern, and an incident is
        // one episode of that pattern.
        var samples = incident.LogPatternId is null
            ? []
            : await db.LogEvents
            .AsNoTracking()
            .Where(e => e.LogPatternId == incident.LogPatternId)
            .OrderByDescending(e => e.OccurredAt)
            .Take(MaxSamples)
            .Select(e => new IncidentSample
            {
                OccurredAt = e.OccurredAt,
                Level = e.Level.ToString(),
                Message = e.Message,
                Host = e.Host,
                TraceId = e.TraceId
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(new IncidentDetail
        {
            Incident = ToListItem(incident, analysis),
            Pattern = incident.LogPattern is null ? null : new IncidentPattern
            {
                Fingerprint = incident.LogPattern.Fingerprint,
                MessageTemplate = incident.LogPattern.MessageTemplate,
                SampleMessage = incident.LogPattern.SampleMessage,
                ExceptionType = incident.LogPattern.ExceptionType,
                HttpStatusCode = incident.LogPattern.HttpStatusCode,
                OccurrenceCount = incident.LogPattern.OccurrenceCount
            },
            Deployment = incident.SuspectedDeployment is null ? null : new IncidentDeployment
            {
                Version = incident.SuspectedDeployment.Version,
                DeployedAt = incident.SuspectedDeployment.DeployedAt,
                CommitSha = incident.SuspectedDeployment.CommitSha,
                DeployedBy = incident.SuspectedDeployment.DeployedBy,
                MinutesBeforeIncident = Math.Round(
                    (incident.FirstSeenAt - incident.SuspectedDeployment.DeployedAt).TotalMinutes, 1)
            },
            Analysis = analysis is null ? null : new IncidentAnalysis
            {
                ModelProvider = analysis.ModelProvider ?? "deterministic",
                ModelName = analysis.ModelName,
                Confidence = analysis.Confidence,
                Summary = analysis.Summary,
                ProbableCause = analysis.ProbableCause,
                SuggestedActions = ParseStringArray(analysis.SuggestedActions),
                SimilarIncidents = ParseSimilarIncidents(analysis.SimilarIncidents),
                CreatedAt = analysis.CreatedAt
            },
            Timeline = timeline,
            Samples = samples
        });
    }

    private static async Task<IResult> GetStatsAsync(
        [FromServices] IncidentIQDbContext db,
        [FromServices] TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        var since = timeProvider.GetUtcNow().AddHours(-24);

        // One round trip. Four counts as separate queries would be four times
        // the latency on a page that reloads every ten seconds.
        var counts = await db.Incidents
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Detected = g.Count(i => i.Status == IncidentStatus.Detected),
                Investigating = g.Count(i => i.Status == IncidentStatus.Investigating),
                ResolvedLast24Hours = g.Count(i => i.Status == IncidentStatus.Resolved && i.ResolvedAt >= since),
                Critical = g.Count(i =>
                    (i.Status == IncidentStatus.Detected || i.Status == IncidentStatus.Investigating)
                    && i.Severity == IncidentSeverity.Critical),
                TotalOccurrences = g
                    .Where(i => i.Status == IncidentStatus.Detected || i.Status == IncidentStatus.Investigating)
                    .Sum(i => (long?)i.OccurrenceCount) ?? 0
            })
            .FirstOrDefaultAsync(cancellationToken);

        return Results.Ok(new IncidentStats
        {
            Detected = counts?.Detected ?? 0,
            Investigating = counts?.Investigating ?? 0,
            ResolvedLast24Hours = counts?.ResolvedLast24Hours ?? 0,
            Critical = counts?.Critical ?? 0,
            TotalOccurrences = counts?.TotalOccurrences ?? 0
        });
    }

    private static async Task<IResult> ListServicesAsync(
        [FromServices] IncidentIQDbContext db,
        CancellationToken cancellationToken = default)
    {
        var services = await db.MonitoredServices
            .AsNoTracking()
            .Where(s => s.IsActive)
            .OrderBy(s => s.Key)
            .Select(s => new ServiceSummary
            {
                Key = s.Key,
                DisplayName = s.DisplayName,
                ActiveIncidents = db.Incidents.Count(i =>
                    i.MonitoredServiceId == s.Id
                    && (i.Status == IncidentStatus.Detected || i.Status == IncidentStatus.Investigating))
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(services);
    }

    private static IncidentListItem ToListItem(Incident incident, AiAnalysis? analysis) => new()
    {
        Id = incident.Id,
        Title = incident.Title,
        Status = incident.Status.ToString(),
        Severity = incident.Severity.ToString(),
        DetectionRule = incident.DetectionRule.ToString(),
        Service = incident.MonitoredService.Key,
        Environment = incident.Environment.Key,
        OccurrenceCount = incident.OccurrenceCount,
        FirstSeenAt = incident.FirstSeenAt,
        LastSeenAt = incident.LastSeenAt,
        SuspectedDeploymentVersion = incident.SuspectedDeployment?.Version,
        HasAnalysis = analysis is not null,
        AnalysisConfidence = analysis?.Confidence
    };

    /// <summary>
    /// Reads a jsonb column written by the Python worker.
    ///
    /// Tolerant by design: this is a cross-language boundary, and a malformed
    /// or absent value must degrade one section of one page rather than fail
    /// the whole request.
    /// </summary>
    private static IReadOnlyList<string> ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<SimilarIncident> ParseSimilarIncidents(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<SimilarIncident>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IResult Problem(HttpContext http, int statusCode, string title, string detail) =>
        Results.Problem(
            title: title,
            detail: detail,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?> { ["correlationId"] = http.GetCorrelationId() });
}
