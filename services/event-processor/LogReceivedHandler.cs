using IncidentIQ.Contracts;
using IncidentIQ.Contracts.Payloads;
using IncidentIQ.Messaging;
using Prometheus;

namespace IncidentIQ.EventProcessor;

/// <summary>
/// Handles <c>logs.raw</c>.
///
/// This phase establishes the transport only: the handler validates the
/// envelope and counts what it received. Normalisation, fingerprinting,
/// incident correlation and the database writes arrive with the pipeline.
///
/// Two properties are already load-bearing and must survive that work:
/// the handler knows nothing about Kafka - no offsets, no commits - and it
/// raises <see cref="PermanentEventException"/> for anything a retry cannot fix,
/// so a malformed event is dead-lettered instead of blocking the partition.
/// </summary>
public sealed class LogReceivedHandler(ILogger<LogReceivedHandler> logger) : IEventHandler<LogReceived>
{
    private const int SupportedVersion = 1;

    /// <summary>
    /// Throughput is a metric, not a log line.
    ///
    /// Logging one line per event at Information sounds harmless until it is
    /// measured: at ingestion volume it rotated 30 MB of container logs in half
    /// a second, cost more than the work it described, and made the logs
    /// useless for anything else. Per-event detail stays at Debug, off by
    /// default; the count that people actually watch lives in Prometheus.
    /// </summary>
    private static readonly Counter EventsConsumed = Metrics.CreateCounter(
        "incidentiq_log_events_consumed_total",
        "Log events consumed from logs.raw.",
        new CounterConfiguration { LabelNames = ["service", "environment", "severity"] });

    public Task HandleAsync(
        EventEnvelope<LogReceived> envelope,
        EventContext context,
        CancellationToken cancellationToken)
    {
        // A version this build does not understand can never succeed by being
        // retried, so it is permanent by definition.
        if (envelope.EventVersion != SupportedVersion)
        {
            throw new PermanentEventException(
                $"Unsupported {envelope.EventType} version {envelope.EventVersion}; this build handles v{SupportedVersion}.");
        }

        if (envelope.TenantId == Guid.Empty)
        {
            throw new PermanentEventException("Event has no tenant; it cannot be attributed to an organization.");
        }

        var payload = envelope.Payload;

        EventsConsumed
            .WithLabels(payload.Service, payload.Environment, payload.Level)
            .Inc();

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(
                "Consumed {EventType} v{Version} from {Topic}[{Partition}]@{Offset} key={Key} attempt={Attempt} "
                + "eventId={EventId} correlationId={CorrelationId} tenantId={TenantId} "
                + "service={Service} environment={Environment} severity={Severity} logEventId={LogEventId}",
                envelope.EventType, envelope.EventVersion,
                context.Topic, context.Partition, context.Offset, context.Key, context.Attempt,
                envelope.EventId, envelope.CorrelationId, envelope.TenantId,
                payload.Service, payload.Environment, payload.Level, payload.LogEventId);
        }

        return Task.CompletedTask;
    }
}
