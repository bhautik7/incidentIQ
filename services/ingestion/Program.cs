using IncidentIQ.Ingestion;
using IncidentIQ.Ingestion.Api;
using IncidentIQ.Ingestion.Auth;
using IncidentIQ.Messaging;
using IncidentIQ.Shared;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

// Log intake. Its only downstream dependency is Kafka - it deliberately never
// touches PostgreSQL, which is what keeps the write path fast and available.
builder.AddIncidentIqDefaults("incidentiq-ingestion", options =>
{
    options.CheckKafka = true;
});

builder.Services.Configure<IngestionOptions>(builder.Configuration.GetSection(IngestionOptions.SectionName));
builder.Services.Configure<ApiKeyOptions>(builder.Configuration.GetSection(ApiKeyOptions.SectionName));

builder.Services.AddSingleton<IApiKeyResolver, ConfiguredApiKeyResolver>();
builder.Services.AddSingleton<LogEventValidator>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddIncidentIQKafkaProducer(builder.Configuration);
builder.Services.AddIncidentIqRateLimiting();

// Reject oversized bodies at the server rather than after buffering them.
// MaxBatchSize bounds the event count; this bounds the bytes, which is a
// different attack and a different accident.
var maxBodyBytes = builder.Configuration.GetValue(
    $"{IngestionOptions.SectionName}:MaxRequestBodyBytes",
    5L * 1024 * 1024);

builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = maxBodyBytes);
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = maxBodyBytes);

var app = builder.Build();

app.MapIncidentIqDefaults();

// Order matters. Authentication first, because the rate limiter partitions by
// the tenant it establishes.
app.UseMiddleware<ApiKeyAuthenticationMiddleware>();
app.UseRateLimiter();

app.MapLogIngestionEndpoints();

// Synthetic publishers used to exercise the transport end to end. Off unless
// explicitly enabled.
app.MapDevelopmentPublishEndpoints(builder.Configuration);

app.Run();

// Exposed so the integration test project can host this app in-memory.
public partial class Program;
