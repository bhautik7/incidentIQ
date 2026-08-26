using System.Text.Json;
using IncidentIQ.Contracts;
using IncidentIQ.Contracts.Payloads;
using IncidentIQ.Domain.Enums;
using IncidentIQ.Messaging;
using IncidentIQ.Persistence;
using Microsoft.Extensions.Options;
using Prometheus;

namespace IncidentIQ.EventProcessor.Detection;

/// <summary>
/// Consumes logs.normalized, counts occurrences into minute buckets, evaluates
/// the detection rules, and opens incidents.
///
/// Runs as its own consumer group on the same topic the processor publishes,
/// so detection can be scaled, paused, replayed or rewritten without touching
/// ingestion.
///
/// Opening an incident writes three things - the incident row, its timeline
/// entry, and an outbox message - in one transaction. That is what stops the
/// system from ever holding an incident nothing was told about, or announcing
/// one that was rolled back.
/// </summary>
public sealed class IncidentDetector(
    IncidentIQDbContext dbContext,
    IncidentDetectionStore store,
    IOutboxWriter outbox,
    IOptions<DetectionOptions> options,
    TimeProvider timeProvider,
    ILogger<IncidentDetector> logger) : IEventBatchHandler<LogNormalized>
{
    private readonly DetectionOptions _options = options.Value;

    private static readonly Counter IncidentsOpened = Metrics.CreateCounter(
        "incidentiq_incidents_opened_total", "Incidents opened by a detection rule.",
        new CounterConfiguration { LabelNames = ["rule", "severity"] });

    private static readonly Counter IncidentsReopened = Metrics.CreateCounter(
        "incidentiq_incidents_reopened_total", "Resolved incidents reopened inside the cooldown window.");

    private static readonly Counter DuplicatesSuppressed = Metrics.CreateCounter(
        "incidentiq_incident_duplicates_suppressed_total",
        "Detections folded into an already-active incident instead of opening a new one.");

    public async Task HandleBatchAsync(
        IReadOnlyList<EventBatchItem<LogNormalized>> batch,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled || batch.Count == 0)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();

        // One tenant at a time: every query below is scoped to an organization,
        // and mixing them would mean either a wider query or a leak.
        foreach (var group in batch.GroupBy(item => item.Envelope.TenantId))
        {
            if (group.Key == Guid.Empty)
            {
                throw new PermanentEventException("Event has no tenant; it cannot be attributed to an organization.");
            }

            await ProcessTenantAsync(group.Key, [.. group], now, cancellationToken);
        }
    }

    private async Task ProcessTenantAsync(
        Guid organizationId,
        IReadOnlyList<EventBatchItem<LogNormalized>> items,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var fingerprints = items.Select(i => i.Envelope.Payload.Fingerprint).Distinct().ToArray();

        await dbContext.ExecuteInTransactionAsync(async () =>
        {
            var patterns = await store.GetPatternsAsync(organizationId, fingerprints, cancellationToken);

            // The processor commits the pattern before publishing this event, so
            // a miss means the two are genuinely out of step. Transient by
            // nature - the batch retries rather than dead-letters.
            var missing = fingerprints.Where(f => !patterns.ContainsKey(f)).ToArray();

            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    $"{missing.Length} fingerprint(s) have no log_patterns row yet; retrying.");
            }

            // ---- Count into minute buckets ----
            var buckets = items
                .GroupBy(i => (
                    Pattern: patterns[i.Envelope.Payload.Fingerprint].Id,
                    Bucket: TruncateToMinute(i.Envelope.Payload.Timestamp)))
                .Select(g => (g.Key.Pattern, g.Key.Bucket, (long)g.Count()))
                .ToList();

            await store.RecordOccurrencesAsync(organizationId, buckets, cancellationToken);

            // ---- Evaluate each pattern that appeared in this batch ----
            foreach (var patternGroup in items.GroupBy(i => i.Envelope.Payload.Fingerprint))
            {
                var pattern = patterns[patternGroup.Key];

                // Muted patterns still count - the numbers stay honest - but
                // never open an incident. That is the whole point of muting.
                if (pattern.IsMuted)
                {
                    continue;
                }

                await EvaluatePatternAsync(
                    organizationId, pattern, [.. patternGroup], now, cancellationToken);
            }
        }, cancellationToken);
    }

    private async Task EvaluatePatternAsync(
        Guid organizationId,
        PatternSnapshot pattern,
        IReadOnlyList<EventBatchItem<LogNormalized>> occurrences,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var windowStart = now.AddMinutes(-_options.WindowMinutes);
        var baselineStart = windowStart.AddMinutes(-_options.BaselineMinutes);

        var windowCount = await store.GetCountAsync(pattern.Id, windowStart, now.AddMinutes(1), cancellationToken);
        var baselineCount = await store.GetCountAsync(pattern.Id, baselineStart, windowStart, cancellationToken);

        var serverErrorCount = HttpStatusExtractorIsServerError(pattern.HttpStatusCode)
            ? await store.GetServerErrorCountAsync(
                organizationId, pattern.MonitoredServiceId, pattern.EnvironmentId, windowStart, cancellationToken)
            : 0;

        var deployment = await store.GetRecentDeploymentAsync(
            organizationId, pattern.MonitoredServiceId, pattern.EnvironmentId,
            now.AddMinutes(-_options.DeploymentCorrelationMinutes), now, cancellationToken);

        var verdict = DetectionRuleEngine.Evaluate(new DetectionInput
        {
            WindowCount = windowCount,
            BaselineCount = baselineCount,
            BaselineDuration = TimeSpan.FromMinutes(_options.BaselineMinutes),
            ServerErrorWindowCount = serverErrorCount,
            // "New" means the pattern itself first appeared inside this window,
            // not merely that this batch is the first we have seen of it.
            IsNewPattern = pattern.FirstSeenAt >= windowStart,
            TimeSinceDeployment = deployment is null ? null : now - deployment.DeployedAt
        }, _options);

        if (!verdict.ShouldOpen)
        {
            return;
        }

        var dedupeKey = verdict.Rule == DetectionRule.ServerErrorSpike
            ? IncidentDedupeKeys.ForServerErrors(pattern.MonitoredServiceId, pattern.EnvironmentId)
            : IncidentDedupeKeys.ForPattern(pattern.Fingerprint);

        var batchCount = occurrences.Count;
        var lastSeen = occurrences.Max(o => o.Envelope.Payload.Timestamp);
        var correlationId = occurrences[0].Envelope.CorrelationId;

        // ---- 1. Already active? Fold in and stop. ----
        var active = await store.TryUpdateActiveIncidentAsync(
            organizationId, dedupeKey, batchCount, lastSeen, verdict.Severity, cancellationToken);

        if (active is not null)
        {
            DuplicatesSuppressed.Inc();
            return;
        }

        // ---- 2. Resolved recently? Reopen rather than pile up. ----
        var reopenedId = await store.TryReopenAsync(
            organizationId, dedupeKey, now.AddMinutes(-_options.ReopenCooldownMinutes),
            batchCount, lastSeen, verdict.Severity, cancellationToken);

        if (reopenedId is { } reopened)
        {
            await store.AddIncidentEventAsync(
                organizationId, reopened, IncidentEventType.Reopened, now,
                $"Recurred within the {_options.ReopenCooldownMinutes}-minute cooldown. {verdict.Reason}",
                JsonSerializer.Serialize(new { rule = verdict.Rule.ToString(), windowCount }),
                cancellationToken);

            EnqueueDetected(organizationId, reopened, pattern, verdict, lastSeen, correlationId);
            await EnqueueAnalysisRequestAsync(
                organizationId, reopened, "reopened", lastSeen, correlationId, cancellationToken);

            IncidentsReopened.Inc();

            logger.LogInformation(
                "Reopened incident {IncidentId} for {Fingerprint}. {Reason}",
                reopened, Short(pattern.Fingerprint), verdict.Reason);

            return;
        }

        // ---- 3. Genuinely new. ----
        var incidentId = Guid.CreateVersion7();

        var inserted = await store.TryInsertIncidentAsync(new NewIncident
        {
            Id = incidentId,
            OrganizationId = organizationId,
            MonitoredServiceId = pattern.MonitoredServiceId,
            EnvironmentId = pattern.EnvironmentId,
            // A server-error spike belongs to no single pattern.
            LogPatternId = verdict.Rule == DetectionRule.ServerErrorSpike ? null : pattern.Id,
            DedupeKey = dedupeKey,
            Rule = verdict.Rule,
            Title = BuildTitle(pattern, verdict),
            Severity = verdict.Severity,
            OccurrenceCount = windowCount,
            FirstSeenAt = pattern.FirstSeenAt,
            LastSeenAt = lastSeen,
            SuspectedDeploymentId = deployment?.Id
        }, cancellationToken);

        if (inserted is null)
        {
            // Another replica won the race between our update and our insert.
            // The unique index caught it; fold into theirs instead.
            await store.TryUpdateActiveIncidentAsync(
                organizationId, dedupeKey, batchCount, lastSeen, verdict.Severity, cancellationToken);

            DuplicatesSuppressed.Inc();
            return;
        }

        var detail = deployment is null
            ? verdict.Reason
            : $"{verdict.Reason} Suspected deployment {deployment.Version}, "
              + $"{(now - deployment.DeployedAt).TotalMinutes:N0} minute(s) earlier.";

        await store.AddIncidentEventAsync(
            organizationId, incidentId, IncidentEventType.Created, now, detail,
            JsonSerializer.Serialize(new
            {
                rule = verdict.Rule.ToString(),
                windowCount,
                baselineCount,
                serverErrorCount,
                suspectedDeploymentVersion = deployment?.Version
            }),
            cancellationToken);

        EnqueueDetected(organizationId, incidentId, pattern, verdict, lastSeen, correlationId);
        await EnqueueAnalysisRequestAsync(
            organizationId, incidentId, "detected", lastSeen, correlationId, cancellationToken);

        IncidentsOpened.WithLabels(verdict.Rule.ToString(), verdict.Severity.ToString()).Inc();

        logger.LogInformation(
            "Opened incident {IncidentId} [{Severity}] by {Rule} for {Service}/{Environment}. {Reason}",
            incidentId, verdict.Severity, verdict.Rule, pattern.MonitoredServiceId, pattern.EnvironmentId, detail);
    }

    /// <summary>
    /// Stages the IncidentDetected event in the same transaction as the incident.
    ///
    /// Not published here. The outbox publisher sends it after the commit, so
    /// there is no window in which an incident exists that nothing was told
    /// about, and none in which an event describes an incident that rolled back.
    /// </summary>
    private void EnqueueDetected(
        Guid organizationId, Guid incidentId, PatternSnapshot pattern,
        DetectionVerdict verdict, DateTimeOffset lastSeen, Guid correlationId)
    {
        var envelope = EventEnvelope<IncidentDetected>.Create(
            EventTypes.IncidentDetected,
            organizationId,
            new IncidentDetected
            {
                IncidentId = incidentId,
                LogPatternId = pattern.Id,
                Service = pattern.MonitoredServiceId.ToString(),
                Environment = pattern.EnvironmentId.ToString(),
                Title = BuildTitle(pattern, verdict),
                Severity = verdict.Severity.ToString(),
                FirstSeenAt = pattern.FirstSeenAt
            },
            // Carried from the log line that triggered this, so one id traces
            // the whole path from HTTP request to incident.
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
            OccurredAt = lastSeen
        });
    }

    /// <summary>
    /// Asks the AI worker to analyse this incident.
    ///
    /// A separate event from IncidentDetected, and separate on purpose:
    /// analysis is also requested for incidents detected long ago - when a
    /// model is upgraded, or a failed analysis is retried. Overloading the
    /// detection event would make "re-analyse this" indistinguishable from
    /// "this just happened".
    ///
    /// Enqueued through the outbox in the same transaction as the incident, so
    /// an incident can never exist that nothing was asked to explain.
    /// </summary>
    private async Task EnqueueAnalysisRequestAsync(
        Guid organizationId, Guid incidentId, string reason,
        DateTimeOffset occurredAt, Guid correlationId, CancellationToken cancellationToken)
    {
        var version = await store.NextAnalysisVersionAsync(incidentId, cancellationToken);

        var envelope = EventEnvelope<IncidentAnalysisRequested>.Create(
            EventTypes.IncidentAnalysisRequested,
            organizationId,
            new IncidentAnalysisRequested
            {
                IncidentId = incidentId,
                AnalysisVersion = version,
                Reason = reason,
                RequestedAt = occurredAt
            },
            correlationId);

        outbox.Enqueue(new OutboxEnqueueRequest
        {
            OrganizationId = organizationId,
            AggregateType = "Incident",
            AggregateId = incidentId,
            EventType = EventTypes.IncidentAnalysisRequested,
            Topic = Topics.IncidentsAnalysisRequested,
            // Same key as every other event about this incident, so its
            // lifecycle stays ordered on one partition.
            PartitionKey = PartitionKeys.ForIncident(organizationId, incidentId),
            SerialisedEnvelope = EventJson.Serialize(envelope),
            EventId = envelope.EventId,
            CorrelationId = envelope.CorrelationId,
            OccurredAt = occurredAt
        });
    }

    private static string BuildTitle(PatternSnapshot pattern, DetectionVerdict verdict)
    {
        if (verdict.Rule == DetectionRule.ServerErrorSpike)
        {
            return "Server error spike";
        }

        var subject = string.IsNullOrWhiteSpace(pattern.ExceptionType)
            ? pattern.MessageTemplate
            : $"{ShortTypeName(pattern.ExceptionType)}: {pattern.MessageTemplate}";

        return subject.Length > 200 ? subject[..200] : subject;
    }

    private static string ShortTypeName(string exceptionType)
    {
        var lastDot = exceptionType.LastIndexOf('.');
        return lastDot >= 0 && lastDot < exceptionType.Length - 1 ? exceptionType[(lastDot + 1)..] : exceptionType;
    }

    private static bool HttpStatusExtractorIsServerError(int? status) => status is >= 500 and <= 599;

    /// <summary>Buckets are minute-aligned so concurrent writers land on the same row.</summary>
    private static DateTimeOffset TruncateToMinute(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, TimeSpan.Zero);

    private static string Short(string fingerprint) => fingerprint[..Math.Min(12, fingerprint.Length)];
}

/// <summary>
/// How "the same active problem" is identified, per rule shape.
///
/// Pattern rules key on the fingerprint. The server-error spike keys on the
/// service and environment, because it is about a service being broken rather
/// than about any one error.
/// </summary>
public static class IncidentDedupeKeys
{
    public static string ForPattern(string fingerprint) => $"fp:{fingerprint}";

    public static string ForServerErrors(Guid serviceId, Guid environmentId) =>
        $"svc5xx:{serviceId:D}:{environmentId:D}";
}
