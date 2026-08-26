using IncidentIQ.Domain.Entities;

namespace IncidentIQ.Persistence;

/// <summary>
/// Everything the publisher needs to send one event, decided by the code that
/// raises it rather than by the publisher.
/// </summary>
public sealed record OutboxEnqueueRequest
{
    public required Guid OrganizationId { get; init; }
    public required string AggregateType { get; init; }
    public required Guid AggregateId { get; init; }
    public required string EventType { get; init; }
    public required string Topic { get; init; }
    public required string PartitionKey { get; init; }

    /// <summary>The complete serialised envelope, exactly as it should reach Kafka.</summary>
    public required string SerialisedEnvelope { get; init; }

    /// <summary>The envelope's own event id. Must match the one inside the serialised envelope.</summary>
    public required Guid EventId { get; init; }

    public required Guid CorrelationId { get; init; }
    public int EventVersion { get; init; } = 1;
    public DateTimeOffset? OccurredAt { get; init; }
    public string? Headers { get; init; }
}

/// <summary>
/// Adds an outbox row to the caller's unit of work.
///
/// Deliberately does not open a transaction, commit, or call SaveChanges. The
/// entire value of the pattern is that the outbox row and the domain change
/// commit *together*; a writer that committed on its own behalf would quietly
/// reintroduce the dual write it exists to prevent.
///
/// Usage is therefore always:
///
///   dbContext.Incidents.Add(incident);
///   outboxWriter.Enqueue(request);
///   await dbContext.SaveChangesAsync();   // one transaction, both rows
/// </summary>
public interface IOutboxWriter
{
    /// <summary>Stages the message. It is durable only once the caller commits.</summary>
    OutboxMessage Enqueue(OutboxEnqueueRequest request);
}

public sealed class OutboxWriter(IncidentIQDbContext dbContext) : IOutboxWriter
{
    public OutboxMessage Enqueue(OutboxEnqueueRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PartitionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SerialisedEnvelope);

        if (request.EventId == Guid.Empty)
        {
            // A publish retry has to be recognisable downstream as the same
            // event; without a stable id there is nothing to recognise it by.
            throw new ArgumentException("EventId is required: it is the downstream idempotency key.", nameof(request));
        }

        var message = new OutboxMessage
        {
            OrganizationId = request.OrganizationId,
            EventId = request.EventId,
            CorrelationId = request.CorrelationId,
            AggregateType = request.AggregateType,
            AggregateId = request.AggregateId,
            EventType = request.EventType,
            EventVersion = request.EventVersion,
            Topic = request.Topic,
            PartitionKey = request.PartitionKey,
            Payload = request.SerialisedEnvelope,
            Headers = request.Headers,
            OccurredAt = request.OccurredAt ?? DateTimeOffset.UtcNow,
            // Due immediately. The publisher only sets this when an attempt fails.
            NextAttemptAt = null
        };

        dbContext.OutboxMessages.Add(message);

        return message;
    }
}
