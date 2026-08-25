using IncidentIQ.Contracts;
using IncidentIQ.Contracts.Payloads;
using IncidentIQ.Messaging;

namespace IncidentIQ.Ingestion;

/// <summary>
/// Publishes synthetic events so the Kafka path can be exercised before any
/// real ingestion exists.
///
/// This is not the ingestion API. It takes no client input beyond a couple of
/// optional query values, it is disabled unless
/// <c>IncidentIQ:EnableDevelopmentEndpoints</c> is true, and it will be deleted
/// once the real endpoint lands.
/// </summary>
public static class DevelopmentEndpoints
{
    public static WebApplication MapDevelopmentPublishEndpoints(this WebApplication app, IConfiguration configuration)
    {
        if (!configuration.GetValue("IncidentIQ:EnableDevelopmentEndpoints", false))
        {
            return app;
        }

        var group = app.MapGroup("/dev").WithTags("development");

        group.MapPost("/publish/log-received", async (
            IEventProducer producer,
            ILoggerFactory loggerFactory,
            Guid? tenantId,
            string? service,
            string? environment,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("IncidentIQ.Ingestion.Development");

            var tenant = tenantId ?? DemoTenantId;
            var serviceName = service ?? "payments-api";

            var payload = new LogReceived
            {
                LogEventId = Guid.CreateVersion7(),
                Service = serviceName,
                Environment = environment ?? "production",
                Level = "Error",
                Message = "The connection pool has been exhausted, either raise MaxPoolSize (currently 100) or Timeout (currently 15 seconds)",
                Timestamp = DateTimeOffset.UtcNow,
                ExceptionType = "Npgsql.NpgsqlException",
                StackTrace = "at Npgsql.PoolingDataSource.Get(...)\nat Npgsql.NpgsqlConnection.Open(...)",
                TraceId = Guid.NewGuid().ToString("N")[..16],
                Host = "payments-api-7d9f-x4k2",
                Properties = new Dictionary<string, string>
                {
                    ["deploymentVersion"] = "2.31.0",
                    ["pod"] = "payments-api-7d9f"
                }
            };

            var envelope = EventEnvelope<LogReceived>.Create(EventTypes.LogReceived, tenant, payload);

            // Same organization + service always lands on the same partition,
            // and therefore reaches the same consumer instance in order.
            var key = PartitionKeys.ForService(tenant, serviceName);

            var result = await producer.PublishAsync(Topics.LogsRaw, key, envelope, cancellationToken);

            logger.LogInformation(
                "Test event published. correlationId={CorrelationId} key={Key} -> {Topic}[{Partition}]@{Offset}",
                envelope.CorrelationId, key, result.Topic, result.Partition, result.Offset);

            return Results.Accepted(value: new
            {
                envelope.EventId,
                envelope.EventType,
                envelope.CorrelationId,
                partitionKey = key,
                result.Topic,
                result.Partition,
                result.Offset
            });
        });

        return app;
    }

    /// <summary>Matches the Acme organization created by the development seeder.</summary>
    private static readonly Guid DemoTenantId = new("11111111-1111-1111-1111-111111111111");
}
