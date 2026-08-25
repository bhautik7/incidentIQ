using System.Text.Json;
using IncidentIQ.Contracts;
using IncidentIQ.Contracts.Payloads;

// Regenerates contracts/samples/*.json from the C# types.
//
// The generated files are committed and used as test fixtures by BOTH
// languages, so a change to a C# contract that is not mirrored in Python fails
// a Python test - loudly, at build time, rather than quietly in a consumer.
//
//   dotnet run --project tools/contract-samples -- contracts/samples

var outputDirectory = args.Length > 0 ? args[0] : "contracts/samples";
Directory.CreateDirectory(outputDirectory);

// Fixed values throughout: a fixture that changes on every run is not a fixture.
var tenantId = new Guid("11111111-1111-1111-1111-111111111111");
var correlationId = new Guid("c0000000-0000-0000-0000-00000000c001");
var occurredAt = new DateTimeOffset(2026, 8, 24, 2, 14, 7, 221, TimeSpan.Zero);

var indented = new JsonSerializerOptions(EventJson.Options) { WriteIndented = true };

Write("log-received", new EventEnvelope<LogReceived>
{
    EventId = new Guid("e0000000-0000-0000-0000-000000000001"),
    EventType = EventTypes.LogReceived,
    EventVersion = 1,
    OccurredAt = occurredAt,
    TenantId = tenantId,
    CorrelationId = correlationId,
    Payload = new LogReceived
    {
        LogEventId = new Guid("10000000-0000-0000-0000-000000000001"),
        Service = "payments-api",
        Environment = "production",
        Level = "Error",
        Message = "The connection pool has been exhausted, either raise MaxPoolSize (currently 100) or Timeout (currently 15 seconds)",
        Timestamp = occurredAt,
        ExceptionType = "Npgsql.NpgsqlException",
        StackTrace = "at Npgsql.PoolingDataSource.Get(...)",
        TraceId = "a1b2c3d4e5f60718",
        Host = "payments-api-7d9f-x4k2",
        Properties = new Dictionary<string, string> { ["deploymentVersion"] = "2.31.0" }
    }
});

Write("incident-detected", new EventEnvelope<IncidentDetected>
{
    EventId = new Guid("e0000000-0000-0000-0000-000000000002"),
    EventType = EventTypes.IncidentDetected,
    EventVersion = 1,
    OccurredAt = occurredAt,
    TenantId = tenantId,
    CorrelationId = correlationId,
    Payload = new IncidentDetected
    {
        IncidentId = new Guid("11111111-0000-0000-0000-0000000000e1"),
        LogPatternId = new Guid("11111111-0000-0000-0000-0000000000d1"),
        Service = "payments-api",
        Environment = "production",
        Title = "payments-api: connection pool exhausted",
        Severity = "Critical",
        FirstSeenAt = occurredAt
    }
});

Write("incident-analysis-requested", new EventEnvelope<IncidentAnalysisRequested>
{
    EventId = new Guid("e0000000-0000-0000-0000-000000000003"),
    EventType = EventTypes.IncidentAnalysisRequested,
    EventVersion = 1,
    OccurredAt = occurredAt,
    TenantId = tenantId,
    CorrelationId = correlationId,
    Payload = new IncidentAnalysisRequested
    {
        IncidentId = new Guid("11111111-0000-0000-0000-0000000000e1"),
        AnalysisVersion = 1,
        Reason = "detected",
        RequestedAt = occurredAt
    }
});

Write("incident-analysis-completed", new EventEnvelope<IncidentAnalysisCompleted>
{
    EventId = new Guid("e0000000-0000-0000-0000-000000000004"),
    EventType = EventTypes.IncidentAnalysisCompleted,
    EventVersion = 1,
    OccurredAt = occurredAt,
    TenantId = tenantId,
    CorrelationId = correlationId,
    Payload = new IncidentAnalysisCompleted
    {
        IncidentId = new Guid("11111111-0000-0000-0000-0000000000e1"),
        AnalysisId = new Guid("a0000000-0000-0000-0000-000000000001"),
        AnalysisVersion = 1,
        Status = "Completed",
        ModelName = "claude-sonnet-5",
        Confidence = 0.870m,
        SimilarIncidentCount = 3,
        CompletedAt = occurredAt
    }
});

Write("deployment-created", new EventEnvelope<DeploymentCreated>
{
    EventId = new Guid("e0000000-0000-0000-0000-000000000005"),
    EventType = EventTypes.DeploymentCreated,
    EventVersion = 1,
    OccurredAt = occurredAt,
    TenantId = tenantId,
    CorrelationId = correlationId,
    Payload = new DeploymentCreated
    {
        DeploymentId = new Guid("11111111-0000-0000-0000-0000000000c1"),
        Service = "payments-api",
        Environment = "production",
        Version = "2.31.0",
        DeployedAt = occurredAt.AddMinutes(-4),
        CommitSha = "9f4c2ab7d31e05b6c8a1f2e3d4b5a6c7d8e9f001",
        DeployedBy = "ci-pipeline"
    }
});

Console.WriteLine($"Samples written to {Path.GetFullPath(outputDirectory)}");
return 0;

void Write<TPayload>(string name, EventEnvelope<TPayload> envelope)
{
    var path = Path.Combine(outputDirectory, $"{name}.json");
    File.WriteAllText(path, JsonSerializer.Serialize(envelope, indented) + "\n");
    Console.WriteLine($"  {name}.json");
}
