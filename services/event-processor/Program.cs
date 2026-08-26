using IncidentIQ.Contracts;
using IncidentIQ.Contracts.Payloads;
using IncidentIQ.EventProcessor.Processing;
using IncidentIQ.Messaging;
using IncidentIQ.Outbox;
using IncidentIQ.Persistence;
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

builder.Services.Configure<ProcessingOptions>(builder.Configuration.GetSection(ProcessingOptions.SectionName));

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException(
        "ConnectionStrings__Postgres is required: the processor cannot persist anything without it.");

builder.Services.AddIncidentIQPersistence(connectionString);
builder.Services.AddScoped<TopologyResolver>();
builder.Services.AddScoped<LogBatchWriter>();

// Needed both to publish logs.normalized and to dead-letter what cannot be handled.
builder.Services.AddIncidentIQKafkaProducer(builder.Configuration);

// Drains outbox_messages to Kafka. Hosted here because this is the service that
// writes incidents; any host with both a database and a producer could run it.
builder.Services.AddIncidentIQOutbox(builder.Configuration);

builder.Services.AddIncidentIQKafkaBatchConsumer<LogReceived, LogReceivedBatchHandler>(
    topic: Topics.LogsRaw,
    consumerGroup: ConsumerGroups.IncidentProcessor,
    deadLetterTopic: Topics.LogsFailed,
    maxBatchSize: builder.Configuration.GetValue("Processing:MaxBatchSize", 500),
    maxBatchWaitMs: builder.Configuration.GetValue("Processing:MaxBatchWaitMs", 250));

var app = builder.Build();

app.MapIncidentIqDefaults();

app.Run();
