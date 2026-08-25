using IncidentIQ.Ingestion;
using IncidentIQ.Messaging;
using IncidentIQ.Shared;

var builder = WebApplication.CreateBuilder(args);

// Log intake. Its only downstream dependency is Kafka - it deliberately never
// touches PostgreSQL, which is what keeps the write path fast and available.
builder.AddIncidentIqDefaults("incidentiq-ingestion", options =>
{
    options.CheckKafka = true;
});

builder.Services.AddIncidentIQKafkaProducer(builder.Configuration);

var app = builder.Build();

app.MapIncidentIqDefaults();

// Synthetic publishers used to exercise the transport end to end. Off unless
// explicitly enabled; real log ingestion is a later phase.
app.MapDevelopmentPublishEndpoints(builder.Configuration);

app.Run();
