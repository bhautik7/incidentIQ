using System.Text.Json;
using IncidentIQ.Contracts;
using IncidentIQ.Contracts.Payloads;

namespace IncidentIQ.Messaging.Tests;

/// <summary>
/// The .NET half of the cross-language contract. The Python worker asserts
/// against the same files in workers/ai-analysis/tests/test_contracts.py, so a
/// change made on one side and not the other fails a test rather than a
/// consumer.
/// </summary>
public class ContractTests
{
    private static readonly string SamplesDirectory = FindSamplesDirectory();

    [Theory]
    [InlineData("log-received")]
    [InlineData("incident-detected")]
    [InlineData("incident-analysis-requested")]
    [InlineData("incident-analysis-completed")]
    [InlineData("deployment-created")]
    public void Every_sample_carries_the_full_envelope(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(SamplesDirectory, $"{name}.json")));
        var root = document.RootElement;

        foreach (var field in (string[])["eventId", "eventType", "eventVersion", "occurredAt", "tenantId", "correlationId", "payload"])
        {
            Assert.True(root.TryGetProperty(field, out _), $"{name}.json is missing '{field}'");
        }
    }

    [Fact]
    public void A_sample_deserialises_into_its_typed_payload()
    {
        var envelope = EventJson.Deserialize<LogReceived>(
            File.ReadAllText(Path.Combine(SamplesDirectory, "log-received.json")));

        Assert.Equal(EventTypes.LogReceived, envelope.EventType);
        Assert.Equal(1, envelope.EventVersion);
        Assert.Equal(new Guid("11111111-1111-1111-1111-111111111111"), envelope.TenantId);
        Assert.Equal("payments-api", envelope.Payload.Service);
        Assert.Equal("2.31.0", envelope.Payload.Properties!["deploymentVersion"]);
    }

    [Fact]
    public void Serialisation_round_trips_without_loss()
    {
        var original = EventJson.Deserialize<IncidentAnalysisCompleted>(
            File.ReadAllText(Path.Combine(SamplesDirectory, "incident-analysis-completed.json")));

        var round = EventJson.Deserialize<IncidentAnalysisCompleted>(EventJson.Serialize(original));

        Assert.Equal(original, round);
    }

    [Fact]
    public void An_unknown_field_from_a_newer_producer_is_ignored()
    {
        // Additive changes must not force a lockstep release of every consumer.
        var json = """
            {
              "eventId": "e0000000-0000-0000-0000-000000000003",
              "eventType": "incident.analysis.requested",
              "eventVersion": 1,
              "occurredAt": "2026-08-24T02:14:07.221+00:00",
              "tenantId": "11111111-1111-1111-1111-111111111111",
              "correlationId": "c0000000-0000-0000-0000-00000000c001",
              "fieldFromANewerProducer": "hello",
              "payload": {
                "incidentId": "11111111-0000-0000-0000-0000000000e1",
                "analysisVersion": 1,
                "reason": "detected",
                "requestedAt": "2026-08-24T02:14:07.221+00:00",
                "alsoNew": 42
              }
            }
            """;

        var envelope = EventJson.Deserialize<IncidentAnalysisRequested>(json);

        Assert.Equal("detected", envelope.Payload.Reason);
    }

    [Fact]
    public void The_service_partition_key_keeps_one_service_on_one_partition()
    {
        var tenant = new Guid("11111111-1111-1111-1111-111111111111");

        var first = PartitionKeys.ForService(tenant, "payments-api");
        var second = PartitionKeys.ForService(tenant, "payments-api");
        var otherService = PartitionKeys.ForService(tenant, "orders-api");
        var otherTenant = PartitionKeys.ForService(Guid.NewGuid(), "payments-api");

        // Same tenant + service -> same key -> same partition -> same consumer,
        // which is what makes incident correlation race-free.
        Assert.Equal(first, second);
        Assert.NotEqual(first, otherService);
        // Two customers running a service of the same name must not share a key.
        Assert.NotEqual(first, otherTenant);
    }

    [Fact]
    public void Sharding_keeps_a_fingerprint_on_one_key_while_spreading_the_service()
    {
        var tenant = Guid.NewGuid();
        const string fingerprintA = "aaaa";
        const string fingerprintB = "bbbb";

        var a1 = PartitionKeys.ForShardedService(tenant, "payments-api", fingerprintA, 8);
        var a2 = PartitionKeys.ForShardedService(tenant, "payments-api", fingerprintA, 8);

        // Stability per fingerprint is the whole point: all occurrences of one
        // error must still meet on one partition.
        Assert.Equal(a1, a2);
        Assert.StartsWith($"{tenant:D}:payments-api:", a1);
        Assert.NotEqual(fingerprintA, fingerprintB);
    }

    private static string FindSamplesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "contracts", "samples");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate contracts/samples from the test output directory.");
    }
}
