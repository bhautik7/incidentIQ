using IncidentIQ.Contracts;
using IncidentIQ.Contracts.Payloads;
using IncidentIQ.Messaging;

namespace IncidentIQ.EventProcessor;

/// <summary>
/// Handles <c>logs.raw</c>.
///
/// This phase establishes the transport only: the handler validates the
/// envelope and logs what it received. Normalisation, fingerprinting, incident
/// correlation and the database writes arrive with the ingestion pipeline.
///
/// Two properties are already load-bearing and must survive that work:
/// the handler knows nothing about Kafka - no offsets, no commits - and it
/// raises <see cref="PermanentEventException"/> for anything a retry cannot fix,
/// so a malformed event is dead-lettered instead of blocking the partition.
/// </summary>
public sealed class LogReceivedHandler(ILogger<LogReceivedHandler> logger) : IEventHandler<LogReceived>
{
    private const int SupportedVersion = 1;

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

        logger.LogInformation(
            "Consumed {EventType} v{Version} from {Topic}[{Partition}]@{Offset} key={Key} attempt={Attempt} "
            + "eventId={EventId} correlationId={CorrelationId} tenantId={TenantId} "
            + "service={Service} environment={Environment} level={Level} logEventId={LogEventId}",
            envelope.EventType, envelope.EventVersion,
            context.Topic, context.Partition, context.Offset, context.Key, context.Attempt,
            envelope.EventId, envelope.CorrelationId, envelope.TenantId,
            envelope.Payload.Service, envelope.Payload.Environment, envelope.Payload.Level,
            envelope.Payload.LogEventId);

        return Task.CompletedTask;
    }
}
