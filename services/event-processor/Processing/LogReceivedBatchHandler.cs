using System.Diagnostics;
using System.Text.Json;
using IncidentIQ.Contracts;
using IncidentIQ.Contracts.Payloads;
using IncidentIQ.EventProcessor.Normalization;
using IncidentIQ.Messaging;
using Microsoft.Extensions.Options;
using Prometheus;

namespace IncidentIQ.EventProcessor.Processing;

public sealed class ProcessingOptions
{
    public const string SectionName = "Processing";

    /// <summary>
    /// How long a processed-event record is kept. Must exceed the Kafka
    /// retention window: while a message can still be redelivered, we need the
    /// record that says we already handled it.
    /// </summary>
    public int ProcessedEventRetentionDays { get; set; } = 8;
}

/// <summary>
/// The logs.raw consumer: validate, normalise, fingerprint, enrich, persist,
/// and announce.
///
/// Everything here operates on the whole batch. The single most important
/// property is that it is safe to run twice on the same input - Kafka
/// guarantees at-least-once delivery, so this handler *will* see the same
/// events again after a rebalance, a crash, or a dead-letter replay.
/// </summary>
public sealed class LogReceivedBatchHandler(
    TopologyResolver topology,
    LogBatchWriter writer,
    IEventProducer producer,
    IOptions<ProcessingOptions> options,
    ILogger<LogReceivedBatchHandler> logger) : IEventBatchHandler<LogReceived>
{
    private const int SupportedVersion = 1;

    private static readonly Counter EventsProcessed = Metrics.CreateCounter(
        "incidentiq_log_events_processed_total",
        "Log events processed from logs.raw.",
        new CounterConfiguration { LabelNames = ["service", "environment", "severity"] });

    private static readonly Counter EventsDuplicate = Metrics.CreateCounter(
        "incidentiq_log_events_duplicate_total",
        "Log events skipped because they had already been processed.");

    private static readonly Counter PatternsTouched = Metrics.CreateCounter(
        "incidentiq_log_patterns_touched_total",
        "Log pattern rows created or updated.");

    private static readonly Histogram BatchDuration = Metrics.CreateHistogram(
        "incidentiq_log_batch_duration_seconds",
        "Time to process one logs.raw batch.");

    public async Task HandleBatchAsync(
        IReadOnlyList<EventBatchItem<LogReceived>> batch,
        CancellationToken cancellationToken)
    {
        using var timer = BatchDuration.NewTimer();
        var stopwatch = Stopwatch.StartNew();

        var prepared = new List<ProcessedLogEvent>(batch.Count);
        var normalized = new List<KeyedEvent<LogNormalized>>(batch.Count);

        foreach (var item in batch)
        {
            var envelope = item.Envelope;

            // ---- 2. Validate the schema ----
            // These are permanent failures by definition: no retry makes an
            // unknown version knowable or an absent tenant appear. Throwing
            // here would poison the batch, so the consumer's isolation pass
            // dead-letters exactly these messages and applies the rest.
            if (envelope.EventVersion != SupportedVersion)
            {
                throw new PermanentEventException(
                    $"Unsupported {envelope.EventType} version {envelope.EventVersion}; this build handles v{SupportedVersion}.");
            }

            var payload = envelope.Payload;

            if (envelope.TenantId == Guid.Empty)
            {
                throw new PermanentEventException("Event has no tenant; it cannot be attributed to an organization.");
            }

            if (string.IsNullOrWhiteSpace(payload.Service) || string.IsNullOrWhiteSpace(payload.Environment))
            {
                throw new PermanentEventException("Event is missing service or environment.");
            }

            if (!LogSeverity.TryNormalize(payload.Level, out var severity))
            {
                throw new PermanentEventException($"Unknown severity '{payload.Level}'.");
            }

            if (!await topology.OrganizationExistsAsync(envelope.TenantId, cancellationToken))
            {
                // An event for an organization that does not exist can never be
                // written - every foreign key would reject it - and no amount
                // of waiting will create it.
                throw new PermanentEventException($"Unknown organization {envelope.TenantId}.");
            }

            // ---- 3. Normalise ----
            var template = LogMessageNormalizer.Normalize(payload.Message);
            var frames = LogFingerprint.NormalizeStackFrames(payload.StackTrace);

            // ---- 4. Fingerprint ----
            var fingerprint = LogFingerprint.Compute(
                envelope.TenantId, payload.Environment, payload.Service,
                payload.ExceptionType, template, payload.StackTrace);

            var httpStatus = HttpStatusExtractor.Extract(payload.Properties, payload.Message);

            // ---- 5. Enrich: client names become database ids ----
            var serviceId = await topology.ResolveServiceIdAsync(envelope.TenantId, payload.Service, cancellationToken);
            var environmentId = await topology.ResolveEnvironmentIdAsync(envelope.TenantId, payload.Environment, cancellationToken);

            prepared.Add(new ProcessedLogEvent
            {
                LogEventId = payload.LogEventId,
                OrganizationId = envelope.TenantId,
                MonitoredServiceId = serviceId,
                EnvironmentId = environmentId,
                Fingerprint = fingerprint,
                Severity = severity,
                Message = payload.Message,
                NormalizedMessage = template,
                OccurredAt = payload.Timestamp,
                ReceivedAt = envelope.OccurredAt,
                ExceptionType = payload.ExceptionType,
                StackTrace = payload.StackTrace,
                TopStackFrames = string.IsNullOrEmpty(frames) ? null : frames,
                TraceId = payload.TraceId,
                SpanId = payload.SpanId,
                Host = payload.Host,
                PropertiesJson = payload.Properties is { Count: > 0 }
                    ? JsonSerializer.Serialize(payload.Properties)
                    : null,
                HttpStatusCode = httpStatus
            });

            normalized.Add(new KeyedEvent<LogNormalized>(
                PartitionKeys.ForService(envelope.TenantId, payload.Service),
                EventEnvelope<LogNormalized>.Create(
                    EventTypes.LogNormalized,
                    envelope.TenantId,
                    new LogNormalized
                    {
                        LogEventId = payload.LogEventId,
                        Service = payload.Service,
                        Environment = payload.Environment,
                        Level = severity,
                        Fingerprint = fingerprint,
                        MessageTemplate = template,
                        SampleMessage = payload.Message,
                        ExceptionType = payload.ExceptionType,
                        Timestamp = payload.Timestamp,
                        HttpStatusCode = httpStatus
                    },
                    // Carried through unchanged, so one id traces a log line
                    // from the HTTP request that accepted it to the incident it
                    // eventually opens.
                    envelope.CorrelationId)));

            EventsProcessed
                .WithLabels(payload.Service, payload.Environment, severity)
                .Inc();
        }

        // ---- 6. Persist, in a fixed number of statements ----
        var result = await writer.WriteAsync(
            ConsumerGroups.IncidentProcessor,
            prepared,
            TimeSpan.FromDays(options.Value.ProcessedEventRetentionDays),
            cancellationToken);

        EventsDuplicate.Inc(result.AlreadyProcessed);
        PatternsTouched.Inc(result.PatternsTouched);

        // ---- 7. Announce ----
        // Published after the commit, and published for every valid event -
        // including redeliveries. Publishing a duplicate costs a downstream
        // consumer one idempotent no-op, which it must handle anyway; *not*
        // publishing loses an event downstream permanently. The asymmetry
        // decides it.
        await producer.PublishBatchAsync(Topics.LogsNormalized, normalized, cancellationToken);

        stopwatch.Stop();

        logger.LogInformation(
            "Processed batch. events={Events} new={New} duplicates={Duplicates} patterns={Patterns} "
            + "samples={Samples} published={Published} durationMs={DurationMs}",
            result.Submitted, result.Submitted - result.AlreadyProcessed, result.AlreadyProcessed,
            result.PatternsTouched, result.SamplesInserted, normalized.Count,
            stopwatch.Elapsed.TotalMilliseconds);
    }
}
