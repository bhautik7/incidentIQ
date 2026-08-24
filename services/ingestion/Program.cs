using IncidentIQ.Shared;

var builder = WebApplication.CreateBuilder(args);

// Log intake. Its only downstream dependency is Kafka - it deliberately never
// touches PostgreSQL, which is what keeps the write path fast and available.
builder.AddIncidentIqDefaults("incidentiq-ingestion", options =>
{
    options.CheckKafka = true;
});

var app = builder.Build();

app.MapIncidentIqDefaults();

app.Run();
