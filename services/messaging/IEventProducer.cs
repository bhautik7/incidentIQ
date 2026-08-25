using IncidentIQ.Contracts;

namespace IncidentIQ.Messaging;

/// <summary>
/// Publishes an envelope to a topic.
///
/// Deliberately narrow: callers choose a topic and a partition key and nothing
/// else. Kafka tuning belongs in configuration, not at every call site.
/// </summary>
public interface IEventProducer
{
    /// <summary>
    /// Awaits the broker's acknowledgement. With acks=all that means every
    /// in-sync replica has the message, so a caller that has awaited this can
    /// honestly tell a client the event is durable.
    /// </summary>
    Task<PublishResult> PublishAsync<TPayload>(
        string topic,
        string partitionKey,
        EventEnvelope<TPayload> envelope,
        CancellationToken cancellationToken = default);

    /// <summary>Blocks until every buffered message has been delivered. Called during shutdown.</summary>
    void Flush(TimeSpan timeout);
}

/// <summary>Where the broker actually put the message. Worth logging: it is the proof of delivery.</summary>
public readonly record struct PublishResult(string Topic, int Partition, long Offset);
