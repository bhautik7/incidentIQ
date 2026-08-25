using Confluent.Kafka;
using IncidentIQ.Contracts;

namespace IncidentIQ.Messaging;

/// <summary>
/// Business logic for one event type. Deliberately knows nothing about Kafka:
/// no offsets, no commits, no partitions. Everything mechanical lives in
/// <see cref="KafkaConsumerService{TPayload, THandler}"/>.
///
/// Handlers must be idempotent. Kafka delivers at least once, so a handler will
/// see duplicates after a rebalance, after a crash between the database commit
/// and the offset commit, and after a dead-letter replay.
/// </summary>
public interface IEventHandler<TPayload>
{
    Task HandleAsync(EventEnvelope<TPayload> envelope, EventContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Where a message came from and how many times it has been tried. Useful for
/// logging and for a handler that wants to behave differently on a last
/// attempt, but never required to make the handler correct.
/// </summary>
public sealed record EventContext(
    string Topic,
    int Partition,
    long Offset,
    string Key,
    DateTimeOffset BrokerTimestamp,
    int Attempt);

/// <summary>
/// Raised by a handler when a message can never succeed - malformed payload,
/// unknown schema version, a required field that is null.
///
/// The distinction matters more than any retry count: a transient failure is
/// retried, a permanent one goes straight to the dead-letter topic. Retrying a
/// permanent failure burns the partition forever.
/// </summary>
public sealed class PermanentEventException(string message, Exception? innerException = null)
    : Exception(message, innerException);
