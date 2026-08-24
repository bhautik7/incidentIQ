using IncidentIQ.EventProcessor;
using IncidentIQ.Shared;

var builder = WebApplication.CreateBuilder(args);

// A background worker, but hosted as a web application so that Kubernetes,
// Docker and Prometheus can reach the same /health and /metrics endpoints
// they use for every other service.
builder.AddIncidentIqDefaults("incidentiq-event-processor", options =>
{
    options.CheckPostgres = true;
    options.CheckKafka = true;
});

builder.Services.AddHostedService<ProcessorWorker>();

var app = builder.Build();

app.MapIncidentIqDefaults();

app.Run();
