using IncidentIQ.Contracts;
using IncidentIQ.Messaging;

namespace IncidentIQ.Outbox.Tests;

/// <summary>
/// A producer whose failures can be turned on and off, so the retry path can be
/// driven deliberately rather than waited for.
/// </summary>
public sealed class ControllableProducer : IEventProducer
{
    public List<(string Topic, string Key, string Payload, IReadOnlyDictionary<string, string>? Headers)> Published { get; } = [];

    /// <summary>When set, every publish fails with this. Clear it to let publishing succeed.</summary>
    public Exception? FailWith { get; set; }

    /// <summary>Fails only for topics matching this predicate, so one message in a batch can fail.</summary>
    public Func<string, string, bool>? FailWhen { get; set; }

    public Task<PublishResult> PublishRawAsync(
        string topic, string partitionKey, byte[] payload,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        var body = System.Text.Encoding.UTF8.GetString(payload);

        if (FailWith is not null || (FailWhen?.Invoke(topic, body) ?? false))
        {
            return Task.FromException<PublishResult>(FailWith ?? new InvalidOperationException("broker unreachable"));
        }

        Published.Add((topic, partitionKey, body, headers));
        return Task.FromResult(new PublishResult(topic, 0, Published.Count));
    }

    public Task<PublishResult> PublishAsync<TPayload>(
        string topic, string partitionKey, EventEnvelope<TPayload> envelope, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The outbox publishes raw bytes only.");

    public Task<IReadOnlyList<PublishResult>> PublishBatchAsync<TPayload>(
        string topic, IReadOnlyList<KeyedEvent<TPayload>> messages, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The outbox publishes raw bytes only.");

    public void Flush(TimeSpan timeout) { }
}
