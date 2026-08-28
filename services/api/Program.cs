using IncidentIQ.Api;
using IncidentIQ.Api.Endpoints;
using IncidentIQ.Api.Realtime;
using IncidentIQ.Contracts;
using IncidentIQ.Contracts.Payloads;
using IncidentIQ.Incidents;
using IncidentIQ.Messaging;
using IncidentIQ.Persistence;
using IncidentIQ.Shared;
using IncidentIQ.Shared.Auth;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// Query/dashboard API. Reads PostgreSQL; it is not on the Kafka path at all,
// so a Kafka outage must not make this service report itself as not ready.
builder.AddIncidentIqDefaults("incidentiq-api", options =>
{
    options.CheckPostgres = true;
});

var connectionString = builder.Configuration.GetConnectionString("Postgres");

if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddIncidentIQPersistence(connectionString);

    builder.Services.AddIncidentIQDatabaseBootstrap(options =>
    {
        // All three default to false. Migrating from the application is a
        // convenience for local development; deployed environments run
        // migrations as a separate, gated step so that two replicas starting
        // at once cannot race.
        options.RunMigrationsOnStartup = builder.Configuration.GetValue("IncidentIQ:RunMigrationsOnStartup", false);
        options.SeedReferenceData = builder.Configuration.GetValue("IncidentIQ:SeedReferenceData", false);
        options.SeedDevelopmentData = builder.Configuration.GetValue("IncidentIQ:SeedDevelopmentData", false);
    });

    // Registered inside the same guard as persistence, because both depend on
    // the DbContext. Outside it, container validation fails at startup and the
    // service cannot even serve /health to explain that it has no database -
    // which is the one thing it must still be able to say.
    //
    // The rules themselves live in the domain service; the API only exposes
    // them. The outbox writer lets a requested analysis be committed by the
    // same transaction that decided to ask for it.
    builder.Services.AddScoped<IncidentLifecycleService>();
    builder.Services.AddScoped<IOutboxWriter, OutboxWriter>();
}

// The same API-key scheme ingestion uses. A key issued to an organization can
// read that organization's incidents and no one else's - the tenant it resolves
// to drives EF's global query filters.
//
// The hub is guarded too, and is the one path allowed to authenticate from the
// query string: a browser cannot set a header on a WebSocket handshake.
builder.Services.AddIncidentIqApiKeyAuth(
    builder.Configuration,
    protectedPathPrefixes: ["/api/v1", "/hubs"],
    queryStringAuthenticatedPrefixes: ["/hubs"]);

builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddSignalR();

// The API consumes Kafka only to push. Both subscriptions broadcast - each
// replica holds different client connections, so all of them need every event
// rather than sharing the partitions out - and both start at "latest", because
// replaying retained history into a dashboard would announce old incidents as
// though they had just happened.
var kafkaBootstrap = builder.Configuration["Kafka:BootstrapServers"];

if (!string.IsNullOrWhiteSpace(kafkaBootstrap))
{
    builder.Services.AddIncidentIQKafkaProducer(builder.Configuration);

    builder.Services.AddIncidentIQKafkaConsumer<IncidentDetected, IncidentDetectedFanout>(
        topic: Topics.IncidentsDetected,
        consumerGroup: "realtime-fanout",
        // No dead-letter topic: a push that cannot be delivered is not worth
        // preserving. The client refetches on reconnect regardless.
        deadLetterTopic: null,
        broadcastToEveryInstance: true,
        autoOffsetReset: "latest");

    builder.Services.AddIncidentIQKafkaConsumer<IncidentAnalysisCompleted, AnalysisCompletedFanout>(
        topic: Topics.IncidentsAnalysisCompleted,
        consumerGroup: "realtime-fanout-analysis",
        deadLetterTopic: null,
        broadcastToEveryInstance: true,
        autoOffsetReset: "latest");

    // Reported, but never fatal. Live updates stopping is a degraded dashboard,
    // not a broken API - the pages all still load by fetching. Marking this
    // Degraded rather than Unhealthy keeps a Kafka outage from pulling the
    // whole read API out of rotation.
    builder.Services.AddHealthChecks()
        .AddCheck<KafkaConsumerHealthCheck>(
            "kafka-fanout",
            failureStatus: HealthStatus.Degraded,
            tags: ["ready"]);
}

var app = builder.Build();

// MapIncidentIqDefaults installs CORS, so it runs first: a browser preflight
// carries no API key, and auth running ahead of CORS would reject it before the
// CORS middleware could short-circuit it.
app.MapIncidentIqDefaults();

// Establishes the authenticated organization...
app.UseIncidentIqApiKeyAuth();

// ...and this puts it where EF's global query filters can see it.
app.UseMiddleware<TenantScopeMiddleware>();

app.MapIncidentEndpoints();
app.MapIncidentActionEndpoints();
app.MapDiagnoseEndpoints();
app.MapLogEndpoints();

// Guarded by the same middleware as the REST routes, so a connection arrives
// with a tenant already established and the hub only has to put it in that
// organization's group.
app.MapHub<IncidentHub>("/hubs/incidents");
app.MapOverviewEndpoints();

app.Run();

// Exposed so the integration test project can host this app in-memory.
public partial class Program;
