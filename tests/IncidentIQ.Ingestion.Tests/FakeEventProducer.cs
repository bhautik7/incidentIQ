using System.Collections.Concurrent;
using IncidentIQ.Contracts;
using IncidentIQ.Messaging;

namespace IncidentIQ.Ingestion.Tests;

/// <summary>
/// Records what the endpoint tried to publish, without a broker.
///
/// The HTTP behaviour - validation, status codes, partition keys, correlation
/// ids - is worth testing at speed and in isolation from Kafka. A separate
/// Testcontainers test proves the real broker leg.
/// </summary>
public sealed class FakeEventProducer : IEventProducer
{
    public ConcurrentBag<(string Topic, string Key, object Envelope)> Published { get; } = [];

    /// <summary>Set to make the next publish throw, so the 503 path can be exercised.</summary>
    public Exception? ThrowOnPublish { get; set; }

    public Task<PublishResult> PublishAsync<TPayload>(
        string topic, string partitionKey, EventEnvelope<TPayload> envelope, CancellationToken cancellationToken = default)
    {
        if (ThrowOnPublish is not null)
        {
            return Task.FromException<PublishResult>(ThrowOnPublish);
        }

        Published.Add((topic, partitionKey, envelope));
        return Task.FromResult(new PublishResult(topic, 0, Published.Count));
    }

    public Task<IReadOnlyList<PublishResult>> PublishBatchAsync<TPayload>(
        string topic, IReadOnlyList<KeyedEvent<TPayload>> messages, CancellationToken cancellationToken = default)
    {
        if (ThrowOnPublish is not null)
        {
            return Task.FromException<IReadOnlyList<PublishResult>>(ThrowOnPublish);
        }

        var results = new List<PublishResult>(messages.Count);

        foreach (var (key, envelope) in messages)
        {
            Published.Add((topic, key, envelope!));
            results.Add(new PublishResult(topic, 0, Published.Count));
        }

        return Task.FromResult<IReadOnlyList<PublishResult>>(results);
    }

    public void Flush(TimeSpan timeout) { }

    public IEnumerable<EventEnvelope<TPayload>> EnvelopesOf<TPayload>() =>
        Published.Select(p => p.Envelope).OfType<EventEnvelope<TPayload>>();
}
