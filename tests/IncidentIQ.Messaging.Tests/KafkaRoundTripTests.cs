using System.Collections.Concurrent;
using Confluent.Kafka;
using IncidentIQ.Contracts;
using IncidentIQ.Contracts.Payloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IncidentIQ.Messaging.Tests;

/// <summary>
/// Per-host recording sink. An instance rather than a static, so two hosts in
/// the same test run cannot see each other's messages.
/// </summary>
internal sealed class EventSink
{
    public BlockingCollection<(EventEnvelope<LogReceived> Envelope, EventContext Context)> Received { get; } = [];

    /// <summary>Messages containing this are made to fail, to exercise the dead-letter path.</summary>
    public string? FailMessagesContaining { get; init; }

    public (EventEnvelope<LogReceived> Envelope, EventContext Context) Take()
    {
        Assert.True(Received.TryTake(out var item, TimeSpan.FromSeconds(30)), "no event reached the consumer within 30s");
        return item;
    }
}

internal sealed class RecordingHandler(EventSink sink) : IEventHandler<LogReceived>
{
    public Task HandleAsync(EventEnvelope<LogReceived> envelope, EventContext context, CancellationToken cancellationToken)
    {
        if (sink.FailMessagesContaining is not null && envelope.Payload.Message.Contains(sink.FailMessagesContaining))
        {
            throw new PermanentEventException("Deliberate failure for the dead-letter test.");
        }

        sink.Received.Add((envelope, context), cancellationToken);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The full transport, against a real broker: producer -> Kafka -> consumer.
/// </summary>
[Collection(KafkaCollection.Name)]
public class KafkaRoundTripTests(KafkaFixture fixture)
{
    private static readonly Guid TenantId = new("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task A_published_event_arrives_at_the_consumer_intact()
    {
        var topic = await fixture.CreateTopicAsync("roundtrip", partitions: 3);
        await using var host = BuildHost(topic, out var sink);
        await host.StartAsync();

        var producer = host.GetRequiredService<IEventProducer>();
        var envelope = NewEnvelope("payments-api", "pool exhausted");
        var key = PartitionKeys.ForService(TenantId, "payments-api");

        var published = await producer.PublishAsync(topic, key, envelope);
        var (consumed, context) = sink.Take();

        // Every envelope field must survive the wire unchanged - these are what
        // tracing, idempotency and tenant routing all depend on.
        Assert.Equal(envelope.EventId, consumed.EventId);
        Assert.Equal(envelope.EventType, consumed.EventType);
        Assert.Equal(envelope.EventVersion, consumed.EventVersion);
        Assert.Equal(envelope.TenantId, consumed.TenantId);
        Assert.Equal(envelope.CorrelationId, consumed.CorrelationId);
        Assert.Equal(envelope.OccurredAt, consumed.OccurredAt);
        Assert.Equal(envelope.Payload.LogEventId, consumed.Payload.LogEventId);
        Assert.Equal("payments-api", consumed.Payload.Service);

        Assert.Equal(topic, context.Topic);
        Assert.Equal(published.Partition, context.Partition);
        Assert.Equal(published.Offset, context.Offset);
        Assert.Equal(key, context.Key);

        await host.StopAsync();
    }

    [Fact]
    public async Task Events_sharing_a_partition_key_land_on_one_partition_in_order()
    {
        var topic = await fixture.CreateTopicAsync("ordering", partitions: 3);
        await using var host = BuildHost(topic, out var sink);
        await host.StartAsync();

        var producer = host.GetRequiredService<IEventProducer>();
        var key = PartitionKeys.ForService(TenantId, "orders-api");

        var expected = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            var envelope = NewEnvelope("orders-api", $"message {i}");
            expected.Add(envelope.EventId);
            await producer.PublishAsync(topic, key, envelope);
        }

        var received = Enumerable.Range(0, 10).Select(_ => sink.Take()).ToList();

        // One key -> one partition. This is the property incident correlation
        // relies on: the same service always reaches the same consumer.
        Assert.Single(received.Select(r => r.Context.Partition).Distinct());

        // And within that partition, delivery order matches production order.
        Assert.Equal(expected, received.Select(r => r.Envelope.EventId));
        Assert.Equal(received.Select(r => r.Context.Offset).Order(), received.Select(r => r.Context.Offset));

        await host.StopAsync();
    }

    [Fact]
    public async Task Different_services_are_spread_across_partitions()
    {
        var topic = await fixture.CreateTopicAsync("spread", partitions: 3);
        await using var host = BuildHost(topic, out var sink);
        await host.StartAsync();

        var producer = host.GetRequiredService<IEventProducer>();
        string[] services = ["svc-a", "svc-b", "svc-c", "svc-d", "svc-e", "svc-f", "svc-g", "svc-h"];

        foreach (var service in services)
        {
            await producer.PublishAsync(topic, PartitionKeys.ForService(TenantId, service), NewEnvelope(service, "hello"));
        }

        var partitions = services.Select(_ => sink.Take().Context.Partition).ToHashSet();

        // Different keys hash across the partitions, which is what makes
        // horizontal scaling possible at all.
        Assert.True(partitions.Count > 1, $"expected several partitions, got {partitions.Count}");

        await host.StopAsync();
    }

    [Fact]
    public async Task A_permanently_failing_message_is_dead_lettered_and_does_not_block_the_partition()
    {
        var topic = await fixture.CreateTopicAsync("dlq-source", partitions: 1);
        var deadLetterTopic = await fixture.CreateTopicAsync("dlq-target", partitions: 1);

        await using var host = BuildHost(topic, out var sink, deadLetterTopic, failMessagesContaining: "poison");
        await host.StartAsync();

        var producer = host.GetRequiredService<IEventProducer>();
        var key = PartitionKeys.ForService(TenantId, "dlq-service");

        await producer.PublishAsync(topic, key, NewEnvelope("dlq-service", "poison message"));
        await producer.PublishAsync(topic, key, NewEnvelope("dlq-service", "good message"));

        // One partition, so the good message is strictly behind the poison one.
        // It must still arrive: a bad message stalling its partition forever is
        // exactly the failure the dead-letter path exists to prevent.
        var (envelope, _) = sink.Take();
        Assert.Equal("good message", envelope.Payload.Message);

        var failure = ReadOne(deadLetterTopic);
        Assert.Equal(topic, failure.Payload.SourceTopic);
        Assert.Contains("Deliberate failure", failure.Payload.Reason);
        // The original bytes are preserved verbatim so a replay is byte-identical.
        Assert.Contains("poison message", failure.Payload.OriginalPayload);

        await host.StopAsync();
    }

    [Fact]
    public async Task Offsets_are_committed_so_a_restarted_consumer_does_not_replay_everything()
    {
        var topic = await fixture.CreateTopicAsync("commit", partitions: 1);
        var group = $"commit-group-{Guid.NewGuid():N}";
        var key = PartitionKeys.ForService(TenantId, "commit-service");

        await using (var host = BuildHost(topic, out var sink, consumerGroup: group))
        {
            await host.StartAsync();

            await host.GetRequiredService<IEventProducer>()
                .PublishAsync(topic, key, NewEnvelope("commit-service", "before restart"));

            Assert.Equal("before restart", sink.Take().Envelope.Payload.Message);

            // Graceful shutdown commits stored offsets and leaves the group.
            await host.StopAsync();
        }

        await using var restarted = BuildHost(topic, out var restartedSink, consumerGroup: group);
        await restarted.StartAsync();

        await restarted.GetRequiredService<IEventProducer>()
            .PublishAsync(topic, key, NewEnvelope("commit-service", "after restart"));

        // Same group, so it resumes from the committed offset. Seeing the first
        // message again would mean the commit never happened.
        Assert.Equal("after restart", restartedSink.Take().Envelope.Payload.Message);

        await restarted.StopAsync();
    }

    // ---- helpers ----

    private static EventEnvelope<LogReceived> NewEnvelope(string service, string message) =>
        EventEnvelope<LogReceived>.Create(EventTypes.LogReceived, TenantId, new LogReceived
        {
            LogEventId = Guid.CreateVersion7(),
            Service = service,
            Environment = "production",
            Level = "Error",
            Message = message,
            Timestamp = DateTimeOffset.UtcNow
        });

    private EventEnvelope<LogFailed> ReadOne(string topic)
    {
        using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = fixture.BootstrapServers,
            GroupId = $"reader-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        }).Build();

        consumer.Subscribe(topic);
        var result = consumer.Consume(TimeSpan.FromSeconds(30));
        Assert.NotNull(result);
        consumer.Close();

        return EventJson.Deserialize<LogFailed>(result.Message.Value);
    }

    private ServiceProvider BuildHost(
        string topic,
        out EventSink sink,
        string? deadLetterTopic = null,
        string? failMessagesContaining = null,
        string? consumerGroup = null)
    {
        sink = new EventSink { FailMessagesContaining = failMessagesContaining };

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddConsole().SetMinimumLevel(LogLevel.Warning));

        services.Configure<KafkaOptions>(options =>
        {
            options.BootstrapServers = fixture.BootstrapServers;
            options.ClientId = "messaging-tests";
            // Commit promptly so the restart test does not wait on the default interval.
            options.Consumer.CommitIntervalMs = 500;
            options.Consumer.CommitEveryMessages = 1;
        });

        services.AddSingleton<IEventProducer, KafkaEventProducer>();
        services.AddSingleton(sink);
        services.AddScoped<RecordingHandler>();
        services.AddSingleton(new KafkaConsumerSubscription<LogReceived>
        {
            Topic = topic,
            ConsumerGroup = consumerGroup ?? $"group-{Guid.NewGuid():N}",
            DeadLetterTopic = deadLetterTopic
        });
        services.AddSingleton<IHostedService, KafkaConsumerService<LogReceived, RecordingHandler>>();

        return services.BuildServiceProvider();
    }
}

internal static class ServiceProviderHostExtensions
{
    public static async Task StartAsync(this ServiceProvider provider)
    {
        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None);
        }

        // Give the consumer group time to complete its first partition assignment.
        await Task.Delay(TimeSpan.FromSeconds(3));
    }

    public static async Task StopAsync(this ServiceProvider provider)
    {
        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }
}
