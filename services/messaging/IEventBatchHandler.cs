using IncidentIQ.Contracts;

namespace IncidentIQ.Messaging;

/// <summary>
/// Handles many events at once.
///
/// Exists because the per-message interface forces one database round trip per
/// log line, and at ingestion volume that is the difference between keeping up
/// and falling permanently behind. A batch of 500 events sharing 3 fingerprints
/// becomes 3 upserts and one insert, not 500 of each.
///
/// The contract is all-or-nothing: either the whole batch is applied or none of
/// it is, so the consumer can commit one offset range with confidence. A
/// handler that partially succeeds and then throws will have its batch
/// redelivered, which is safe only because handlers are also idempotent.
/// </summary>
public interface IEventBatchHandler<TPayload>
{
    Task HandleBatchAsync(IReadOnlyList<EventBatchItem<TPayload>> batch, CancellationToken cancellationToken);
}

/// <summary>One event plus where it came from.</summary>
public sealed record EventBatchItem<TPayload>(EventEnvelope<TPayload> Envelope, EventContext Context);
