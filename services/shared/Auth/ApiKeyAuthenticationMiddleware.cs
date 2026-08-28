using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IncidentIQ.Shared.Auth;

/// <summary>
/// Establishes tenant and correlation id for every guarded request, before
/// anything else runs.
///
/// Runs ahead of rate limiting on purpose: the limiter partitions by tenant, so
/// the tenant has to be known first. An unauthenticated request is cheap to
/// reject and must not consume another organization's quota.
/// </summary>
public sealed class ApiKeyAuthenticationOptions
{
    /// <summary>
    /// Path prefixes this middleware guards. Ingestion protects /api/v1/logs,
    /// the query API protects everything under /api/v1 - so the prefix is a
    /// parameter rather than a constant.
    /// </summary>
    public string[] ProtectedPathPrefixes { get; set; } = [];

    /// <summary>
    /// Prefixes where the key may also arrive as an <c>access_token</c> query
    /// parameter instead of a header.
    ///
    /// This exists for exactly one reason: a browser cannot set headers on a
    /// WebSocket handshake, so a hub cannot be reached with <c>X-Api-Key</c> at
    /// all. It is opt-in per prefix rather than global because a credential in a
    /// URL is genuinely worse - URLs reach proxy logs, browser history and
    /// referrer headers in a way headers do not.
    ///
    /// Serilog's request logging records <c>RequestPath</c>, which excludes the
    /// query string, so the key is not written to this service's own logs.
    /// </summary>
    public string[] QueryStringAuthenticatedPrefixes { get; set; } = [];
}

public sealed class ApiKeyAuthenticationMiddleware(
    RequestDelegate next,
    IOptions<ApiKeyAuthenticationOptions> options,
    ILogger<ApiKeyAuthenticationMiddleware> logger)
{
    public const string ApiKeyHeader = "X-Api-Key";
    public const string CorrelationIdHeader = "X-Correlation-Id";

    /// <summary>The name SignalR's JavaScript client uses for its access token.</summary>
    public const string AccessTokenQueryParameter = "access_token";

    private const string BearerPrefix = "Bearer ";

    public async Task InvokeAsync(HttpContext context, IApiKeyResolver resolver)
    {
        // A client-supplied correlation id lets a caller tie our logs to its
        // own trace; otherwise we mint one so every request has exactly one.
        var correlationId = ReadCorrelationId(context);
        context.SetCorrelationId(correlationId);
        context.Response.Headers[CorrelationIdHeader] = correlationId.ToString();

        var guarded = options.Value.ProtectedPathPrefixes
            .Any(prefix => context.Request.Path.StartsWithSegments(prefix));

        if (!guarded)
        {
            await next(context);
            return;
        }

        var apiKey = context.Request.Headers[ApiKeyHeader].ToString();

        if (string.IsNullOrWhiteSpace(apiKey)
            && options.Value.QueryStringAuthenticatedPrefixes
                .Any(prefix => context.Request.Path.StartsWithSegments(prefix)))
        {
            // A hub connection presents its key two different ways over its
            // life, and both have to be accepted or it fails halfway through
            // connecting:
            //
            //   negotiate  - an ordinary POST, so the client sends a bearer
            //                header and never touches the query string;
            //   websocket  - a handshake the browser will not let it add
            //                headers to, so the token moves to the URL.
            //
            // Checked only after X-Api-Key, so a normal caller never ends up
            // authenticating from a URL.
            var bearer = context.Request.Headers.Authorization.ToString();

            apiKey = bearer.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
                ? bearer[BearerPrefix.Length..].Trim()
                : context.Request.Query[AccessTokenQueryParameter].ToString();
        }

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
        var supplied = context.Request.Headers[CorrelationIdHeader].ToString();

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
