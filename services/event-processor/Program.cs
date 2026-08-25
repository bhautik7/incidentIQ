using IncidentIQ.Contracts;
using IncidentIQ.Contracts.Payloads;
using IncidentIQ.EventProcessor;
using IncidentIQ.Messaging;
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

// Needed by the consumer to dead-letter what it cannot handle.
builder.Services.AddIncidentIQKafkaProducer(builder.Configuration);

builder.Services.AddIncidentIQKafkaConsumer<LogReceived, LogReceivedHandler>(
    topic: Topics.LogsRaw,
    consumerGroup: ConsumerGroups.IncidentProcessor,
    deadLetterTopic: Topics.LogsFailed);

var app = builder.Build();

app.MapIncidentIqDefaults();

app.Run();
