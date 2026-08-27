using IncidentIQ.Api;
using IncidentIQ.Api.Endpoints;
using IncidentIQ.Incidents;
using IncidentIQ.Persistence;
using IncidentIQ.Shared;
using IncidentIQ.Shared.Auth;

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
builder.Services.AddIncidentIqApiKeyAuth(builder.Configuration, "/api/v1");
builder.Services.AddSingleton(TimeProvider.System);

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
app.MapOverviewEndpoints();

app.Run();

// Exposed so the integration test project can host this app in-memory.
public partial class Program;
