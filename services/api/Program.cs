using IncidentIQ.Persistence;
using IncidentIQ.Shared;

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
}

var app = builder.Build();

app.MapIncidentIqDefaults();

app.Run();

// Exposed so the integration test project can host this app in-memory.
public partial class Program;
