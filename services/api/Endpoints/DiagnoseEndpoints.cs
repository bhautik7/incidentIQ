using System.Text.Json;
using IncidentIQ.Api.Contracts;
using IncidentIQ.Contracts;
using IncidentIQ.Contracts.Payloads;
using IncidentIQ.Domain.Entities;
using IncidentIQ.Domain.Enums;
using IncidentIQ.Incidents;
using IncidentIQ.Persistence;
using IncidentIQ.Shared.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IncidentIQ.Api.Endpoints;

/// <summary>
/// Opens an incident for an uploaded log, because no rule will.
///
/// The detection rules are tuned for production traffic: a threshold of five in
/// five minutes, a rate far above a baseline, a burst of 5xx across a service.
/// A pasted log is a hundred lines that already happened - verified, a
/// realistic 106-line outage log produced zero incidents on first upload. So a
/// page whose entire promise is "paste this and I will tell you what is wrong"
/// cannot wait for a rule to fire, and this endpoint deliberately opens one for
/// the upload's dominant error pattern under
/// <see cref="DetectionRule.UserRequested"/>.
///
/// Everything after that is the existing machinery, untouched: the incident is
/// announced on incidents.detected, an analysis is requested through the
/// outbox, the Python worker embeds it, retrieves similar past incidents and
/// narrates it, and the user reads the answer on the incident detail page. The
/// rule name is what keeps this honest afterwards - an incident that says
/// UserRequested is not claiming anything was spiking.
///
/// It is *not* an insert-on-demand: an already-open incident for the same
/// fingerprint is returned rather than duplicated, which is the same
/// deduplication invariant the detector obeys and the same partial unique index
/// enforcing it.
/// </summary>
public static class DiagnoseEndpoints
{
    /// <summary>
    /// How far back "since" is allowed to reach.
    ///
    /// The client sends the instant its upload's oldest line lands on, which is
    /// as far back as that log is long - an eight-hour log replayed to end now
    /// starts eight hours ago, and a window that refused to look that far would
    /// simply never find the pattern that broke it. Matched to the raw
    /// retention window, since beyond it there are no lines to show anyway.
    ///
    /// Clamped rather than trusted, because this parameter chooses which
    /// pattern gets an incident: unbounded, a stale tab could open one for an
    /// error the user never uploaded.
    /// </summary>
    private static readonly TimeSpan MaxLookback = TimeSpan.FromHours(48);

    private static readonly TimeSpan DefaultLookback = TimeSpan.FromMinutes(15);

    /// <summary>Mirrors the detector's own correlation window.</summary>
    private static readonly TimeSpan DeploymentCorrelationWindow = TimeSpan.FromMinutes(30);

    private const int MaxTitleLength = 200;

    public static IEndpointRouteBuilder MapDiagnoseEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/v1/diagnose", DiagnoseAsync)
            .WithName("DiagnoseUpload")
            .WithTags("diagnose");

        return routes;
    }

    private static async Task<IResult> DiagnoseAsync(
        [FromServices] IncidentIQDbContext db,
        [FromServices] IOutboxWriter outbox,
        [FromServices] TimeProvider timeProvider,
        HttpContext http,
        [FromBody] DiagnoseRequest? request,
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

        if (string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(environment))
        {
            return Problem(http, StatusCodes.Status400BadRequest, "Service and environment are required",
                "A diagnosis is scoped to one service in one environment; without both, "
                + "there is no set of patterns to choose a dominant one from.");
        }

        var now = timeProvider.GetUtcNow();

        // Clamped on both sides: never into the future, never further back than
        // the lookback cap.
        var since = request?.Since is { } requested
            ? Clamp(requested, now - MaxLookback, now)
            : now - DefaultLookback;

        // Resolved through the same global query filter as everything else, so
        // another organization's service simply does not exist here.
        var topology = await db.MonitoredServices.AsNoTracking()
            .Where(s => s.Key == service)
            .Select(s => new { ServiceId = s.Id })
            .FirstOrDefaultAsync(cancellationToken);

        var environmentId = await db.Environments.AsNoTracking()
            .Where(e => e.Key == environment)
            .Select(e => (Guid?)e.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (topology is null || environmentId is null)
        {
            // Not a 404. Ingestion creates the service and environment rows as
            // it processes the first event, so "no such service" a second after
            // an upload means the pipeline has not caught up yet - which is
            // exactly what pending says, and the client is already polling.
            return Results.Ok(Pending(
                $"Nothing from {service} in {environment} has been processed yet."));
        }

        var serviceId = topology.ServiceId;

        // Errors only. A log full of Information lines has nothing to diagnose,
        // and an incident opened for one would be noise wearing the product's
        // most important badge.
        var errorLevels = new[] { LogEventLevel.Error, LogEventLevel.Fatal };

        var patterns = await db.LogPatterns.AsNoTracking()
            .Where(p => p.MonitoredServiceId == serviceId
                        && p.EnvironmentId == environmentId
                        && errorLevels.Contains(p.Level)
                        && p.LastSeenAt >= since)
            .Select(p => new
            {
                p.Id,
                p.Fingerprint,
                p.Level,
                p.ExceptionType,
                p.MessageTemplate,
                p.FirstSeenAt,
                p.LastSeenAt,
                p.IsMuted
            })
            .ToListAsync(cancellationToken);

        if (patterns.Count == 0)
        {
            return Results.Ok(Pending(
                $"No errors from {service} in {environment} have been processed yet."));
        }

        var patternIds = patterns.Select(p => p.Id).ToList();

        // "Dominant" is measured inside the window, not from
        // LogPattern.OccurrenceCount - that is a lifetime total, so a pattern
        // this service has produced a million times over six months would beat
        // the one that actually broke this morning every time.
        //
        // Counted from the raw lines rather than from the minute buckets, and
        // the difference is a race this endpoint would otherwise lose. The
        // buckets are written by the *detector*, a separate consumer group, and
        // the patterns by the processor - so in the seconds after an upload the
        // patterns exist and their buckets do not, every count comes back zero,
        // and the incident is opened for whichever pattern happened to be seen
        // last. The raw lines are written by the same batch as the pattern
        // itself, so if there is a pattern to rank there is something to rank
        // it by. Retention on that table is the 48 hours this window is capped
        // to, so nothing in range is missing.
        var counts = await db.RawLogEvents.AsNoTracking()
            .Where(e => e.LogPatternId != null
                        && patternIds.Contains(e.LogPatternId.Value)
                        && e.OccurredAt >= since)
            .GroupBy(e => e.LogPatternId!.Value)
            .Select(g => new { PatternId = g.Key, Count = g.LongCount() })
            .ToListAsync(cancellationToken);

        var countsByPattern = counts.ToDictionary(row => row.PatternId, row => row.Count);

        var candidates = patterns
            .Select(p => new PatternCandidate
            {
                Id = p.Id,
                Fingerprint = p.Fingerprint,
                Level = p.Level,
                ExceptionType = p.ExceptionType,
                MessageTemplate = p.MessageTemplate,
                FirstSeenAt = p.FirstSeenAt,
                LastSeenAt = p.LastSeenAt,
                IsMuted = p.IsMuted,
                WindowCount = countsByPattern.GetValueOrDefault(p.Id)
            })
            .ToList();

        // Muted patterns are excluded here for the same reason the detector
        // excludes them: somebody has already said this one is not worth an
        // incident, and asking from a different screen does not overrule that.
        var dominant = candidates
            .Where(c => !c.IsMuted)
            .OrderByDescending(c => c.WindowCount)
            .ThenByDescending(c => c.LastSeenAt)
            .FirstOrDefault();

        if (dominant is null)
        {
            return Results.Ok(new DiagnoseResult
            {
                Status = DiagnoseStatuses.Pending,
                PatternsFound = candidates.Count,
                Message = candidates.Count == 1
                    ? "The only error pattern in this upload is muted, so no incident was opened."
                    : $"All {candidates.Count} error patterns in this upload are muted, so no incident was opened."
            });
        }

        var dedupeKey = IncidentDedupeKeys.ForPattern(dominant.Fingerprint);

        // Already open? Send them there. Opening a second incident for the same
        // fingerprint is the thing this product exists to prevent, and a user
        // uploading the log of an outage that is already being investigated is
        // the normal case, not an edge one.
        var existing = await FindActiveAsync(db, dedupeKey, cancellationToken);

        if (existing is not null)
        {
            return Results.Ok(new DiagnoseResult
            {
                Status = DiagnoseStatuses.Existing,
                IncidentId = existing.Id,
                Fingerprint = dominant.Fingerprint,
                Title = existing.Title,
                OccurrenceCount = existing.OccurrenceCount,
                PatternsFound = candidates.Count,
                Message = "An incident for this error was already open; the upload was folded into it."
            });
        }

        var deployment = await db.Deployments.AsNoTracking()
            .Where(d => d.MonitoredServiceId == serviceId
                        && d.EnvironmentId == environmentId
                        && d.DeployedAt >= dominant.FirstSeenAt - DeploymentCorrelationWindow
                        && d.DeployedAt <= dominant.LastSeenAt)
            .OrderByDescending(d => d.DeployedAt)
            .Select(d => new { d.Id, d.Version, d.DeployedAt })
            .FirstOrDefaultAsync(cancellationToken);

        var incidentId = Guid.CreateVersion7();
        var title = BuildTitle(dominant);
        var correlationId = http.GetCorrelationId();

        db.Incidents.Add(new Incident
        {
            Id = incidentId,
            OrganizationId = tenant.TenantId,
            MonitoredServiceId = serviceId,
            EnvironmentId = environmentId.Value,
            LogPatternId = dominant.Id,
            DedupeKey = dedupeKey,
            DetectionRule = DetectionRule.UserRequested,
            Title = title,
            Status = IncidentStatus.Detected,
            Severity = SeverityFor(dominant),
            OccurrenceCount = dominant.WindowCount,
            FirstSeenAt = dominant.FirstSeenAt,
            LastSeenAt = dominant.LastSeenAt,
            SuspectedDeploymentId = deployment?.Id,
            CreatedAt = now,
            UpdatedAt = now
        });

        var detail = deployment is null
            ? $"Opened on request for an uploaded log. {dominant.WindowCount} occurrence(s) of the "
              + $"dominant error pattern, which crossed no detection threshold."
            : $"Opened on request for an uploaded log. {dominant.WindowCount} occurrence(s) of the "
              + $"dominant error pattern. Suspected deployment {deployment.Version}, "
              + $"{(dominant.FirstSeenAt - deployment.DeployedAt).TotalMinutes:N0} minute(s) earlier.";

        db.IncidentEvents.Add(new IncidentEvent
        {
            OrganizationId = tenant.TenantId,
            IncidentId = incidentId,
            Type = IncidentEventType.Created,
            OccurredAt = now,
            // System, not User, even though a person asked. The pipeline opened
            // it; nobody declared it. An actor here would put a name against a
            // judgement that was never made.
            ActorType = ActorType.System,
            Message = detail,
            Data = JsonSerializer.Serialize(new
            {
                rule = DetectionRule.UserRequested.ToString(),
                windowCount = dominant.WindowCount,
                patternsFound = candidates.Count,
                suspectedDeploymentVersion = deployment?.Version
            })
        });

        EnqueueDetected(outbox, tenant.TenantId, incidentId, dominant, title,
            SeverityFor(dominant), serviceId, environmentId.Value, correlationId, now);

        EnqueueAnalysisRequest(outbox, tenant.TenantId, incidentId, correlationId, now);

        try
        {
            // One transaction: the incident, its first timeline entry and both
            // outbox rows commit together, so there is no state in which the
            // incident exists and nothing was told about it.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // The detector opened one for the same fingerprint between our read
            // and our write. The index caught it; fold into theirs.
            db.ChangeTracker.Clear();

            var winner = await FindActiveAsync(db, dedupeKey, cancellationToken);

            return winner is null
                ? Problem(http, StatusCodes.Status409Conflict, "Could not open an incident",
                    "An incident for this error was opened and closed while this request was in flight. Try again.")
                : Results.Ok(new DiagnoseResult
                {
                    Status = DiagnoseStatuses.Existing,
                    IncidentId = winner.Id,
                    Fingerprint = dominant.Fingerprint,
                    Title = winner.Title,
                    OccurrenceCount = winner.OccurrenceCount,
                    PatternsFound = candidates.Count,
                    Message = "Detection opened an incident for this error while the upload was being read."
                });
        }

        return Results.Ok(new DiagnoseResult
        {
            Status = DiagnoseStatuses.Opened,
            IncidentId = incidentId,
            Fingerprint = dominant.Fingerprint,
            Title = title,
            OccurrenceCount = dominant.WindowCount,
            PatternsFound = candidates.Count,
            Message = candidates.Count > 1
                ? $"Opened an incident for the dominant of {candidates.Count} error patterns. "
                  + "The analysis covers that pattern only."
                : "Opened an incident for the error pattern in this upload."
        });
    }

    private sealed record ActiveIncident(Guid Id, string Title, long OccurrenceCount);

    private static async Task<ActiveIncident?> FindActiveAsync(
        IncidentIQDbContext db, string dedupeKey, CancellationToken cancellationToken) =>
        await db.Incidents.AsNoTracking()
            .Where(i => i.DedupeKey == dedupeKey
                        && (i.Status == IncidentStatus.Detected || i.Status == IncidentStatus.Investigating))
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new ActiveIncident(i.Id, i.Title, i.OccurrenceCount))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Severity from the pattern's own level, and no higher.
    ///
    /// The rules derive severity partly from how loudly something is firing.
    /// Nothing here is firing - the log already happened - so inferring Critical
    /// from a hundred pasted lines would be inventing urgency the evidence does
    /// not support.
    /// </summary>
    private static IncidentSeverity SeverityFor(PatternCandidate pattern) =>
        pattern.Level == LogEventLevel.Fatal ? IncidentSeverity.High : IncidentSeverity.Medium;

    private static string BuildTitle(PatternCandidate pattern)
    {
        var subject = string.IsNullOrWhiteSpace(pattern.ExceptionType)
            ? pattern.MessageTemplate
            : $"{ShortTypeName(pattern.ExceptionType)}: {pattern.MessageTemplate}";

        return subject.Length > MaxTitleLength ? subject[..MaxTitleLength] : subject;
    }

    private static string ShortTypeName(string exceptionType)
    {
        var lastDot = exceptionType.LastIndexOf('.');
        return lastDot >= 0 && lastDot < exceptionType.Length - 1 ? exceptionType[(lastDot + 1)..] : exceptionType;
    }

    private static void EnqueueDetected(
        IOutboxWriter outbox, Guid organizationId, Guid incidentId, PatternCandidate pattern,
        string title, IncidentSeverity severity, Guid serviceId, Guid environmentId,
        Guid correlationId, DateTimeOffset now)
    {
        var envelope = EventEnvelope<IncidentDetected>.Create(
            EventTypes.IncidentDetected,
            organizationId,
            new IncidentDetected
            {
                IncidentId = incidentId,
                LogPatternId = pattern.Id,
                Service = serviceId.ToString(),
                Environment = environmentId.ToString(),
                Title = title,
                Severity = severity.ToString(),
                FirstSeenAt = pattern.FirstSeenAt
            },
            correlationId);

        outbox.Enqueue(new OutboxEnqueueRequest
        {
            OrganizationId = organizationId,
            AggregateType = "Incident",
            AggregateId = incidentId,
            EventType = EventTypes.IncidentDetected,
            Topic = Topics.IncidentsDetected,
            PartitionKey = PartitionKeys.ForIncident(organizationId, incidentId),
            SerialisedEnvelope = EventJson.Serialize(envelope),
            EventId = envelope.EventId,
            CorrelationId = envelope.CorrelationId,
            OccurredAt = now
        });
    }

    private static void EnqueueAnalysisRequest(
        IOutboxWriter outbox, Guid organizationId, Guid incidentId,
        Guid correlationId, DateTimeOffset now)
    {
        // Version 1 unconditionally: the incident was created by this same
        // transaction, so it cannot already have an analysis.
        var envelope = EventEnvelope<IncidentAnalysisRequested>.Create(
            EventTypes.IncidentAnalysisRequested,
            organizationId,
            new IncidentAnalysisRequested
            {
                IncidentId = incidentId,
                AnalysisVersion = 1,
                Reason = "diagnose-upload",
                RequestedAt = now
            },
            correlationId);

        outbox.Enqueue(new OutboxEnqueueRequest
        {
            OrganizationId = organizationId,
            AggregateType = "Incident",
            AggregateId = incidentId,
            EventType = EventTypes.IncidentAnalysisRequested,
            Topic = Topics.IncidentsAnalysisRequested,
            PartitionKey = PartitionKeys.ForIncident(organizationId, incidentId),
            SerialisedEnvelope = EventJson.Serialize(envelope),
            EventId = envelope.EventId,
            CorrelationId = envelope.CorrelationId,
            OccurredAt = now
        });
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static DateTimeOffset Clamp(DateTimeOffset value, DateTimeOffset min, DateTimeOffset max) =>
        value < min ? min : value > max ? max : value;

    private static DiagnoseResult Pending(string message) => new()
    {
        Status = DiagnoseStatuses.Pending,
        Message = message
    };

    private static IResult Problem(HttpContext http, int statusCode, string title, string detail) =>
        Results.Problem(
            title: title,
            detail: detail,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?> { ["correlationId"] = http.GetCorrelationId() });

    private sealed class PatternCandidate
    {
        public required Guid Id { get; init; }
        public required string Fingerprint { get; init; }
        public required LogEventLevel Level { get; init; }
        public required string? ExceptionType { get; init; }
        public required string MessageTemplate { get; init; }
        public required DateTimeOffset FirstSeenAt { get; init; }
        public required DateTimeOffset LastSeenAt { get; init; }
        public required bool IsMuted { get; init; }
        public required long WindowCount { get; init; }
    }
}
