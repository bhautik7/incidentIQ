using IncidentIQ.Shared;

var builder = WebApplication.CreateBuilder(args);

// Query/dashboard API. Reads PostgreSQL; it is not on the Kafka path at all,
// so a Kafka outage must not make this service report itself as not ready.
builder.AddIncidentIqDefaults("incidentiq-api", options =>
{
    options.CheckPostgres = true;
});

var app = builder.Build();

app.MapIncidentIqDefaults();

app.Run();

// Exposed so the integration test project can host this app in-memory.
public partial class Program;
