using System.Diagnostics;
using IncidentIQ.Contracts;
using IncidentIQ.Contracts.Payloads;
using IncidentIQ.Shared.Auth;
using IncidentIQ.Messaging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace IncidentIQ.Ingestion.Api;

public static class LogIngestionEndpoints
{
    public const string CorrelationIdHeader = "X-Correlation-Id";

    public static IEndpointRouteBuilder MapLogIngestionEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/logs")
            .RequireRateLimiting(RateLimitPolicies.PerTenant)
            .WithTags("ingestion");

        group.MapPost("/batch", IngestBatchAsync)
            .WithName("IngestLogBatch")
            .Produces<LogBatchResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return routes;
    }

    private static async Task<IResult> IngestBatchAsync(
        [FromBody] LogBatchRequest request,
        HttpContext httpContext,
        IEventProducer producer,
        LogEventValidator validator,
        IOptions<IngestionOptions> ingestionOptions,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("IncidentIQ.Ingestion.LogBatch");
        var options = ingestionOptions.Value;

        // Set by ApiKeyAuthenticationMiddleware; the endpoint is unreachable without it.
        var tenant = httpContext.GetTenantContext()!;
        var correlationId = httpContext.GetCorrelationId();

        var events = request.Events;

        if (events is null || events.Count == 0)
        {
            return Problem(StatusCodes.Status400BadRequest, "Empty batch",
                "The request must contain at least one event in 'events'.", correlationId);
        }

        if (events.Count > options.MaxBatchSize)
        {
            return Problem(StatusCodes.Status400BadRequest, "Batch too large",
                $"A batch may contain at most {options.MaxBatchSize} events; this request contained {events.Count}.",
                correlationId);
        }

        var stopwatch = Stopwatch.StartNew();
        var now = timeProvider.GetUtcNow();

        var accepted = new List<KeyedEvent<LogReceived>>(events.Count);
        var errors = new List<LogEventError>();

        for (var index = 0; index < events.Count; index++)
        {
            var candidate = events[index];
            var outcome = validator.Validate(candidate, now);

            if (!outcome.IsValid)
            {
                errors.Add(new LogEventError
                {
                    Index = index,
                    Field = outcome.Field,
                    Message = outcome.Message
                });
                continue;
            }

            var payload = new LogReceived
            {
                // Falling back to a server-generated id keeps the batch usable,
                // but the client has given up idempotency: its own retry will
                // produce a second event that nothing can recognise as a duplicate.
                LogEventId = candidate.EventId ?? Guid.CreateVersion7(),
                Service = candidate.Service!.Trim(),
                Environment = candidate.Environment!.Trim(),
                Level = outcome.Severity,
                Message = candidate.Message!,
                Timestamp = candidate.Timestamp!.Value.ToUniversalTime(),
                ExceptionType = NullIfBlank(candidate.ExceptionType),
                StackTrace = NullIfBlank(candidate.StackTrace),
                TraceId = NullIfBlank(candidate.TraceId),
                SpanId = NullIfBlank(candidate.SpanId),
                Host = NullIfBlank(candidate.Host),
                Properties = candidate.Metadata is { Count: > 0 } ? candidate.Metadata : null
            };

            var envelope = EventEnvelope<LogReceived>.Create(
                EventTypes.LogReceived,
                tenant.TenantId,
                payload,
                correlationId);

            // Every event for one organization's service lands on one partition,
            // so the processor sees them in order on a single consumer instance.
            var partitionKey = PartitionKeys.ForService(tenant.TenantId, payload.Service);

            accepted.Add(new KeyedEvent<LogReceived>(partitionKey, envelope));
        }

        // Everything failed validation: that is a client bug, not a partial
        // success, and it deserves a 400 rather than a cheerful 202.
        if (accepted.Count == 0)
        {
            logger.LogWarning(
                "Rejected entire batch. tenantId={TenantId} apiKey={ApiKeyName} correlationId={CorrelationId} count={Count}",
                tenant.TenantId, tenant.ApiKeyName, correlationId, events.Count);

            return Results.Json(new LogBatchResponse
            {
                Accepted = 0,
                Rejected = errors.Count,
                CorrelationId = correlationId,
                Errors = errors
            }, statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            await producer.PublishBatchAsync(Topics.LogsRaw, accepted, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client hung up mid-request. Nothing was promised, so say
            // nothing - and do not burn a log line per abandoned request.
            logger.LogDebug("Batch cancelled by client. correlationId={CorrelationId}", correlationId);
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            // Kafka is the one hard dependency of this path. A 503 tells the
            // client to retry with its original event ids, which is exactly the
            // behaviour that makes the retry safe.
            logger.LogError(ex,
                "Kafka publish failed. tenantId={TenantId} correlationId={CorrelationId} count={Count}",
                tenant.TenantId, correlationId, accepted.Count);

            return Problem(StatusCodes.Status503ServiceUnavailable, "Ingestion temporarily unavailable",
                "The events could not be accepted. Retry with the same event ids.", correlationId);
        }

        stopwatch.Stop();

        // One line per batch. At ingestion volume a line per event would cost
        // more than the ingestion itself.
        logger.LogInformation(
            "Accepted batch. tenantId={TenantId} apiKey={ApiKeyName} correlationId={CorrelationId} "
            + "accepted={Accepted} rejected={Rejected} durationMs={DurationMs}",
            tenant.TenantId, tenant.ApiKeyName, correlationId,
            accepted.Count, errors.Count, stopwatch.Elapsed.TotalMilliseconds);

        return Results.Accepted(value: new LogBatchResponse
        {
            Accepted = accepted.Count,
            Rejected = errors.Count,
            CorrelationId = correlationId,
            Errors = errors
        });
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// RFC 7807 problem details, with the correlation id included so a client
    /// reporting a failure hands us the one value that finds it in the logs.
    /// </summary>
    private static IResult Problem(int statusCode, string title, string detail, Guid correlationId) =>
        Results.Problem(
            title: title,
            detail: detail,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?> { ["correlationId"] = correlationId });
}
