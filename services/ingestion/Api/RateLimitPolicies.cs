using System.Threading.RateLimiting;
using IncidentIQ.Ingestion.Auth;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace IncidentIQ.Ingestion.Api;

public static class RateLimitPolicies
{
    public const string PerTenant = "per-tenant";

    /// <summary>
    /// Partitions the limiter by tenant, so one organization's burst cannot
    /// starve another's. A global limiter would let the noisiest customer set
    /// everyone else's throughput.
    ///
    /// Token bucket rather than fixed window: a fixed window lets a client
    /// spend its entire allowance in the first millisecond and then stall, and
    /// synchronises every client onto the same boundary. A bucket smooths the
    /// steady rate while still allowing a genuine burst.
    ///
    /// The quota counts requests, not events. Combined with MaxBatchSize that
    /// bounds events too. An event-denominated quota is fairer and is the next
    /// step, but it needs the limiter to acquire a variable number of permits,
    /// which means reading the body before limiting - a trade not worth making
    /// until quotas are actually being sold.
    /// </summary>
    public static void AddIncidentIqRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(limiter =>
        {
            limiter.AddPolicy(PerTenant, context =>
            {
                var options = context.RequestServices
                    .GetRequiredService<IOptions<IngestionOptions>>().Value;

                // Authentication runs first, so this is populated. Anonymous
                // requests fall back to the remote IP, which keeps an
                // unauthenticated flood from reaching the endpoint at all.
                var partitionKey = context.GetTenantContext()?.TenantId.ToString()
                                   ?? context.Connection.RemoteIpAddress?.ToString()
                                   ?? "unknown";

                return RateLimitPartition.GetTokenBucketLimiter(partitionKey, _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = options.RateLimitBucketCapacity,
                    TokensPerPeriod = options.RateLimitTokensPerPeriod,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(options.RateLimitPeriodSeconds),
                    AutoReplenishment = true,
                    // Queue nothing. A queued log batch is a request holding a
                    // connection and a buffer while its data goes stale; the
                    // client is better served by a prompt 429 and a retry.
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                });
            });

            limiter.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.ContentType = "application/problem+json";

                // Tell the client when to come back instead of leaving it to guess.
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var value)
                    ? (int)Math.Ceiling(value.TotalSeconds)
                    : 1;

                context.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString();

                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    type = "https://datatracker.ietf.org/doc/html/rfc6585#section-4",
                    title = "Too Many Requests",
                    status = StatusCodes.Status429TooManyRequests,
                    detail = "Rate limit exceeded for this organization. Retry after the interval in the Retry-After header.",
                    retryAfterSeconds = retryAfter,
                    correlationId = context.HttpContext.GetCorrelationId()
                }, cancellationToken);
            };
        });
    }
}
