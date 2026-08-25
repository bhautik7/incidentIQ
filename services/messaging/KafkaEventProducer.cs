using System.Text;
using Confluent.Kafka;
using IncidentIQ.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IncidentIQ.Messaging;

/// <summary>
/// One producer per process, shared by every caller.
///
/// A Kafka producer is thread-safe and holds connections, batching buffers and
/// idempotence state. Creating one per request would lose all batching, exhaust
/// connections, and break the idempotence guarantee it exists to provide.
/// </summary>
public sealed class KafkaEventProducer : IEventProducer, IDisposable
{
    private readonly IProducer<string, byte[]> _producer;
    private readonly ILogger<KafkaEventProducer> _logger;
    private bool _disposed;

    public KafkaEventProducer(IOptions<KafkaOptions> options, ILogger<KafkaEventProducer> logger)
    {
        _logger = logger;
        var kafka = options.Value;

        var config = new ProducerConfig
        {
            BootstrapServers = kafka.BootstrapServers,
            ClientId = $"{kafka.ClientId}-producer",
            EnableIdempotence = kafka.Producer.EnableIdempotence,
            Acks = kafka.Producer.Acks.Equals("all", StringComparison.OrdinalIgnoreCase) ? Acks.All : Acks.Leader,
            LingerMs = kafka.Producer.LingerMs,
            CompressionType = Enum.Parse<CompressionType>(kafka.Producer.CompressionType, ignoreCase: true),
            MessageTimeoutMs = kafka.Producer.MessageTimeoutMs,
            EnableDeliveryReports = true
        };

        _producer = new ProducerBuilder<string, byte[]>(config)
            .SetErrorHandler((_, error) =>
            {
                // Transient errors are logged and retried internally; only a
                // fatal one means this producer instance is unusable.
                if (error.IsFatal)
                {
                    _logger.LogCritical("Kafka producer entered a fatal state: {Reason}", error.Reason);
                }
                else
                {
                    _logger.LogWarning("Kafka producer error: {Reason}", error.Reason);
                }
            })
            .Build();

        _logger.LogInformation(
            "Kafka producer ready. Brokers={Brokers} Idempotence={Idempotence} Acks={Acks}",
            kafka.BootstrapServers, config.EnableIdempotence, config.Acks);
    }

    public async Task<PublishResult> PublishAsync<TPayload>(
        string topic,
        string partitionKey,
        EventEnvelope<TPayload> envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);

        var result = await _producer.ProduceAsync(topic, BuildMessage(partitionKey, envelope), cancellationToken);

        _logger.LogInformation(
            "Published {EventType} v{EventVersion} to {Topic}[{Partition}]@{Offset} key={Key} eventId={EventId} correlationId={CorrelationId}",
            envelope.EventType, envelope.EventVersion, result.Topic, result.Partition.Value, result.Offset.Value,
            partitionKey, envelope.EventId, envelope.CorrelationId);

        return new PublishResult(result.Topic, result.Partition.Value, result.Offset.Value);
    }

    public async Task<IReadOnlyList<PublishResult>> PublishBatchAsync<TPayload>(
        string topic,
        IReadOnlyList<KeyedEvent<TPayload>> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        if (messages.Count == 0)
        {
            return [];
        }

        // Enqueue everything first. ProduceAsync hands the message to
        // librdkafka synchronously and returns a task for the acknowledgement,
        // so this loop fills the batch rather than waiting on the network.
        var pending = new Task<DeliveryResult<string, byte[]>>[messages.Count];

        for (var i = 0; i < messages.Count; i++)
        {
            var (partitionKey, envelope) = messages[i];
            pending[i] = _producer.ProduceAsync(topic, BuildMessage(partitionKey, envelope), cancellationToken);
        }

        var delivered = await Task.WhenAll(pending);

        var results = new PublishResult[delivered.Length];
        for (var i = 0; i < delivered.Length; i++)
        {
            results[i] = new PublishResult(delivered[i].Topic, delivered[i].Partition.Value, delivered[i].Offset.Value);
        }

        // One line per batch, not per message. At ingestion volume, logging
        // each event would cost more than producing it.
        _logger.LogInformation(
            "Published batch of {Count} to {Topic} across partition(s) [{Partitions}]",
            results.Length, topic, string.Join(", ", results.Select(r => r.Partition).Distinct().Order()));

        return results;
    }

    private static Message<string, byte[]> BuildMessage<TPayload>(string partitionKey, EventEnvelope<TPayload> envelope) =>
        new()
        {
            Key = partitionKey,
            Value = EventJson.SerializeToUtf8Bytes(envelope),
            // Duplicated from the body so a router, a monitoring tool or a
            // dead-letter inspector can work without deserialising anything.
            Headers = BuildHeaders(envelope)
        };

    public void Flush(TimeSpan timeout)
    {
        if (_disposed)
        {
            return;
        }

        var remaining = _producer.Flush(timeout);
        if (remaining > 0)
        {
            _logger.LogError("Producer flush timed out with {Count} message(s) still buffered.", remaining);
        }
    }

    private static Headers BuildHeaders<TPayload>(EventEnvelope<TPayload> envelope) =>
    [
        new Header(EventHeaders.EventId, Encoding.UTF8.GetBytes(envelope.EventId.ToString())),
        new Header(EventHeaders.EventType, Encoding.UTF8.GetBytes(envelope.EventType)),
        new Header(EventHeaders.EventVersion, Encoding.UTF8.GetBytes(envelope.EventVersion.ToString())),
        new Header(EventHeaders.TenantId, Encoding.UTF8.GetBytes(envelope.TenantId.ToString())),
        new Header(EventHeaders.CorrelationId, Encoding.UTF8.GetBytes(envelope.CorrelationId.ToString()))
    ];

    public void Dispose()
    {
        // Dispose must tolerate being called more than once - the DI container
        // will do exactly that when one instance is registered under two
        // service types, and Confluent's handle throws once destroyed.
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Anything still buffered is delivered before the process exits.
        var remaining = _producer.Flush(TimeSpan.FromSeconds(10));
        if (remaining > 0)
        {
            _logger.LogError("Producer disposed with {Count} message(s) still buffered.", remaining);
        }

        _producer.Dispose();
    }
}
