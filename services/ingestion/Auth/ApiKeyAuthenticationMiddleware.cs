using IncidentIQ.Ingestion.Api;

namespace IncidentIQ.Ingestion.Auth;

/// <summary>
/// Establishes tenant and correlation id for every ingestion request, before
/// anything else runs.
///
/// Runs ahead of rate limiting on purpose: the limiter partitions by tenant, so
/// the tenant has to be known first. An unauthenticated request is cheap to
/// reject and must not consume another organization's quota.
/// </summary>
public sealed class ApiKeyAuthenticationMiddleware(RequestDelegate next, ILogger<ApiKeyAuthenticationMiddleware> logger)
{
    public const string ApiKeyHeader = "X-Api-Key";

    private const string IngestionPathPrefix = "/api/v1/logs";

    public async Task InvokeAsync(HttpContext context, IApiKeyResolver resolver)
    {
        // A client-supplied correlation id lets a caller tie our logs to its
        // own trace; otherwise we mint one so every request has exactly one.
        var correlationId = ReadCorrelationId(context);
        context.SetCorrelationId(correlationId);
        context.Response.Headers[LogIngestionEndpoints.CorrelationIdHeader] = correlationId.ToString();

        if (!context.Request.Path.StartsWithSegments(IngestionPathPrefix))
        {
            await next(context);
            return;
        }

        var apiKey = context.Request.Headers[ApiKeyHeader].ToString();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            await WriteUnauthorizedAsync(context, correlationId, "Missing X-Api-Key header.");
            return;
        }

        var tenant = resolver.Resolve(apiKey);

        if (tenant is null)
        {
            // The key itself is never logged, and the response never
            // distinguishes "unknown" from "disabled" - both would help someone
            // probing for valid keys.
            logger.LogWarning(
                "Rejected ingestion request with an unrecognised API key. correlationId={CorrelationId} remoteIp={RemoteIp}",
                correlationId, context.Connection.RemoteIpAddress);

            await WriteUnauthorizedAsync(context, correlationId, "The supplied API key is not valid.");
            return;
        }

        context.SetTenantContext(tenant);

        // Every log line for the rest of this request carries these, so a
        // correlation id is enough to reconstruct what happened.
        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["tenantId"] = tenant.TenantId,
            ["correlationId"] = correlationId,
            ["apiKeyName"] = tenant.ApiKeyName
        }))
        {
            await next(context);
        }
    }

    private static Guid ReadCorrelationId(HttpContext context)
    {
        var supplied = context.Request.Headers[LogIngestionEndpoints.CorrelationIdHeader].ToString();

        // A malformed value is replaced rather than rejected: refusing a whole
        // log batch over a bad trace header would be a poor trade.
        return Guid.TryParse(supplied, out var parsed) && parsed != Guid.Empty
            ? parsed
            : Guid.CreateVersion7();
    }

    private static Task WriteUnauthorizedAsync(HttpContext context, Guid correlationId, string detail)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/problem+json";

        return context.Response.WriteAsJsonAsync(new
        {
            type = "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.2",
            title = "Unauthorized",
            status = StatusCodes.Status401Unauthorized,
            detail,
            correlationId
        });
    }
}

public static class HttpContextExtensions
{
    private const string TenantKey = "IncidentIQ.Tenant";
    private const string CorrelationKey = "IncidentIQ.CorrelationId";

    public static void SetTenantContext(this HttpContext context, TenantContext tenant) =>
        context.Items[TenantKey] = tenant;

    public static TenantContext? GetTenantContext(this HttpContext context) =>
        context.Items.TryGetValue(TenantKey, out var value) ? value as TenantContext : null;

    public static void SetCorrelationId(this HttpContext context, Guid correlationId) =>
        context.Items[CorrelationKey] = correlationId;

    public static Guid GetCorrelationId(this HttpContext context) =>
        context.Items.TryGetValue(CorrelationKey, out var value) && value is Guid id ? id : Guid.Empty;
}
