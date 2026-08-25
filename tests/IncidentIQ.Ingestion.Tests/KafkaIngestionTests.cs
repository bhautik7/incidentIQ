using System.Net;
using System.Net.Http.Json;
using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using IncidentIQ.Contracts;
using IncidentIQ.Contracts.Payloads;
using IncidentIQ.Ingestion.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.Kafka;

namespace IncidentIQ.Ingestion.Tests;

/// <summary>
/// Proves the real leg: HTTP request -> real Kafka broker -> consumed message.
///
/// The fake-producer tests cover HTTP behaviour quickly. This one exists to
/// catch what a fake cannot: serialisation the broker rejects, headers that do
/// not survive, and partition keys that do not route the way the design claims.
/// </summary>
public sealed class KafkaIngestionTests : IAsyncLifetime
{
    // Same broker image as docker-compose, so the test exercises what runs locally.
    private readonly KafkaContainer _kafka = new KafkaBuilder("confluentinc/cp-kafka:7.8.0").Build();

    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        await _kafka.StartAsync();

        // Three partitions, matching production, so the routing assertions below
        // mean something.
        using var admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = _kafka.GetBootstrapAddress() }).Build();

        await admin.CreateTopicsAsync([
            new TopicSpecification { Name = Topics.LogsRaw, NumPartitions = 3, ReplicationFactor = 1 }
        ]);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // The real KafkaEventProducer is left in place.
                ["Kafka:BootstrapServers"] = _kafka.GetBootstrapAddress(),
                ["Ingestion:ApiKeys:Keys:0:KeyHash"] = ConfiguredApiKeyResolver.Hash(IngestionApiFactory.ValidApiKey),
                ["Ingestion:ApiKeys:Keys:0:TenantId"] = IngestionApiFactory.TenantId.ToString(),
                ["Ingestion:ApiKeys:Keys:0:Name"] = "kafka-test-key",
                ["Ingestion:ApiKeys:Keys:0:IsActive"] = "true"
            }));
        });
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _kafka.DisposeAsync();
    }

    private HttpClient CreateClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationMiddleware.ApiKeyHeader, IngestionApiFactory.ValidApiKey);
        return client;
    }

    private List<ConsumeResult<string, byte[]>> Drain(string groupId, int expected, TimeSpan timeout)
    {
        using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = _kafka.GetBootstrapAddress(),
            GroupId = groupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        }).Build();

        consumer.Subscribe(Topics.LogsRaw);

        var collected = new List<ConsumeResult<string, byte[]>>();
        var deadline = DateTime.UtcNow + timeout;

        while (collected.Count < expected && DateTime.UtcNow < deadline)
        {
            var result = consumer.Consume(TimeSpan.FromMilliseconds(500));
            if (result is not null)
            {
                collected.Add(result);
            }
        }

        consumer.Close();
        return collected;
    }

    [Fact]
    public async Task Batch_posted_over_http_arrives_on_logs_raw_intact()
    {
        var client = CreateClient();
        var correlationId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/logs/batch")
        {
            Content = JsonContent.Create(new
            {
                events = new[]
                {
                    new
                    {
                        eventId,
                        service = "payments-api",
                        environment = "production",
                        timestamp = DateTimeOffset.UtcNow,
                        severity = "error",
                        message = "The connection pool has been exhausted",
                        exceptionType = "Npgsql.NpgsqlException",
                        traceId = "trace-abc",
                        metadata = new Dictionary<string, string> { ["deploymentVersion"] = "2.31.0" }
                    }
                }
            })
        };
        request.Headers.Add(Api.LogIngestionEndpoints.CorrelationIdHeader, correlationId.ToString());

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var consumed = Drain("test-intact", expected: 1, TimeSpan.FromSeconds(30));
        var message = Assert.Single(consumed);

        var envelope = EventJson.Deserialize<LogReceived>(message.Message.Value);

        Assert.Equal(EventTypes.LogReceived, envelope.EventType);
        Assert.Equal(IngestionApiFactory.TenantId, envelope.TenantId);
        Assert.Equal(correlationId, envelope.CorrelationId);
        Assert.Equal(eventId, envelope.Payload.LogEventId);
        Assert.Equal("payments-api", envelope.Payload.Service);

        // "error" was normalised to the canonical spelling on the way through.
        Assert.Equal(LogSeverity.Error, envelope.Payload.Level);
        Assert.Equal("2.31.0", envelope.Payload.Properties!["deploymentVersion"]);

        // Headers must survive the round trip, since dead-letter tooling reads
        // them without deserialising the body.
        Assert.Equal(correlationId.ToString(), ReadHeader(message, EventHeaders.CorrelationId));
        Assert.Equal(IngestionApiFactory.TenantId.ToString(), ReadHeader(message, EventHeaders.TenantId));
        Assert.Equal(EventTypes.LogReceived, ReadHeader(message, EventHeaders.EventType));

        Assert.Equal($"{IngestionApiFactory.TenantId:D}:payments-api", message.Message.Key);
    }

    [Fact]
    public async Task All_events_for_one_service_land_on_a_single_partition()
    {
        var client = CreateClient();

        // 60 events across three services. If the key strategy works, each
        // service occupies exactly one partition - which is what lets a single
        // consumer instance own a service's whole stream and correlate it
        // without racing another replica.
        var services = new[] { "payments-api", "orders-api", "inventory-api" };
        var events = Enumerable.Range(0, 60).Select(i => new
        {
            eventId = Guid.CreateVersion7(),
            service = services[i % services.Length],
            environment = "production",
            timestamp = DateTimeOffset.UtcNow,
            severity = "Warning",
            message = $"event {i}"
        }).ToArray();

        var response = await client.PostAsJsonAsync("/api/v1/logs/batch", new { events });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var consumed = Drain("test-partitioning", expected: 60, TimeSpan.FromSeconds(40));
        Assert.Equal(60, consumed.Count);

        foreach (var service in services)
        {
            var partitions = consumed
                .Where(c => c.Message.Key.EndsWith($":{service}", StringComparison.Ordinal))
                .Select(c => c.Partition.Value)
                .Distinct()
                .ToList();

            Assert.Single(partitions);
        }
    }

    [Fact]
    public async Task Nothing_is_published_when_the_whole_batch_fails_validation()
    {
        var client = CreateClient();

        // Compare high watermarks around the request rather than draining the
        // topic. Other tests in this class share this broker and topic, so a
        // consumer reading from the beginning would see their messages and this
        // assertion would be about test ordering rather than about the endpoint.
        var before = HighWatermarks();

        var response = await client.PostAsJsonAsync("/api/v1/logs/batch", new
        {
            events = new[]
            {
                new { service = "", environment = "production", timestamp = DateTimeOffset.UtcNow, severity = "Error", message = "rejected" }
            }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Validation happens before publishing, so a rejected batch must leave
        // no trace on the topic.
        Assert.Equal(before, HighWatermarks());
    }

    /// <summary>End offset of every partition of logs.raw, keyed by partition.</summary>
    private Dictionary<int, long> HighWatermarks()
    {
        using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = _kafka.GetBootstrapAddress(),
            GroupId = $"watermark-{Guid.NewGuid():N}"
        }).Build();

        return Enumerable.Range(0, 3).ToDictionary(
            partition => partition,
            partition => consumer.QueryWatermarkOffsets(
                new TopicPartition(Topics.LogsRaw, partition), TimeSpan.FromSeconds(10)).High.Value);
    }

    private static string? ReadHeader(ConsumeResult<string, byte[]> result, string name) =>
        result.Message.Headers.TryGetLastBytes(name, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;
}
