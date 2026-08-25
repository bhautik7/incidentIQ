using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Testcontainers.Kafka;

namespace IncidentIQ.Messaging.Tests;

/// <summary>
/// A real broker for the whole test class run.
///
/// A real broker rather than a fake, because everything these tests exercise -
/// routing by key, manual offset commits, rebalance on shutdown, dead-lettering
/// - exists only in Kafka. A mocked producer would prove nothing.
/// </summary>
public sealed class KafkaFixture : IAsyncLifetime
{
    private readonly KafkaContainer _container = new KafkaBuilder("confluentinc/cp-kafka:7.8.0").Build();

    public string BootstrapServers => _container.GetBootstrapAddress();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>
    /// Creates a topic with an explicit partition count.
    ///
    /// Each test gets its own topic. Sharing one would mean every test replays
    /// every earlier test's messages, because a new consumer group starts at the
    /// beginning - which looks exactly like a routing bug and is not one.
    /// </summary>
    public async Task<string> CreateTopicAsync(string prefix, int partitions)
    {
        var name = $"{prefix}.{Guid.NewGuid():N}";

        using var admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = BootstrapServers }).Build();

        await admin.CreateTopicsAsync(
        [
            new TopicSpecification { Name = name, NumPartitions = partitions, ReplicationFactor = 1 }
        ]);

        return name;
    }
}

[CollectionDefinition(Name)]
public class KafkaCollection : ICollectionFixture<KafkaFixture>
{
    public const string Name = "kafka";
}
