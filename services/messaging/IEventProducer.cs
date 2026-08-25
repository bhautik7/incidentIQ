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

    /// <summary>
    /// Publishes many envelopes to one topic and awaits all acknowledgements.
    ///
    /// Not a convenience wrapper around a loop of <see cref="PublishAsync"/>:
    /// awaiting each message in turn would serialise a 500-event batch into 500
    /// broker round trips. Messages are enqueued first and awaited afterwards,
    /// so librdkafka can batch and compress them into a handful of requests.
    ///
    /// Enqueue order is preserved, which with idempotence enabled means
    /// per-partition ordering survives the parallelism.
    /// </summary>
    Task<IReadOnlyList<PublishResult>> PublishBatchAsync<TPayload>(
        string topic,
        IReadOnlyList<KeyedEvent<TPayload>> messages,
        CancellationToken cancellationToken = default);

    /// <summary>Blocks until every buffered message has been delivered. Called during shutdown.</summary>
    void Flush(TimeSpan timeout);
}

/// <summary>One envelope plus the key that decides its partition.</summary>
public readonly record struct KeyedEvent<TPayload>(string PartitionKey, EventEnvelope<TPayload> Envelope);

/// <summary>Where the broker actually put the message. Worth logging: it is the proof of delivery.</summary>
public readonly record struct PublishResult(string Topic, int Partition, long Offset);
