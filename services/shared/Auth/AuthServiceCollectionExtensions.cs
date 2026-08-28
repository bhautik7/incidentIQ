using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IncidentIQ.Shared.Auth;

public static class AuthServiceCollectionExtensions
{
    /// <summary>
    /// Registers API-key authentication for the given path prefixes.
    ///
    /// Both services authenticate the same way against the same key list, so a
    /// key issued for an organization works for ingesting its logs and for
    /// reading its incidents - and cannot read anyone else's.
    /// </summary>
    public static IServiceCollection AddIncidentIqApiKeyAuth(
        this IServiceCollection services,
        IConfiguration configuration,
        params string[] protectedPathPrefixes)
    {
        services.Configure<ApiKeyOptions>(configuration.GetSection(ApiKeyOptions.SectionName));
        services.Configure<ApiKeyAuthenticationOptions>(
            options => options.ProtectedPathPrefixes = protectedPathPrefixes);

        services.AddSingleton<IApiKeyResolver, ConfiguredApiKeyResolver>();

        return services;
    }

    /// <summary>
    /// Guards these prefixes, and additionally lets them authenticate with an
    /// <c>access_token</c> query parameter.
    ///
    /// For hubs only: a browser cannot put a header on a WebSocket handshake.
    /// Kept as a separate call so that opting a path into URL credentials is a
    /// deliberate act rather than something inherited by every route.
    /// </summary>
    public static IServiceCollection AddIncidentIqApiKeyAuth(
        this IServiceCollection services,
        IConfiguration configuration,
        string[] protectedPathPrefixes,
        string[] queryStringAuthenticatedPrefixes)
    {
        services.Configure<ApiKeyOptions>(configuration.GetSection(ApiKeyOptions.SectionName));
        services.Configure<ApiKeyAuthenticationOptions>(options =>
        {
            options.ProtectedPathPrefixes = protectedPathPrefixes;
            options.QueryStringAuthenticatedPrefixes = queryStringAuthenticatedPrefixes;
        });

        services.AddSingleton<IApiKeyResolver, ConfiguredApiKeyResolver>();

        return services;
    }

    public static IApplicationBuilder UseIncidentIqApiKeyAuth(this IApplicationBuilder app) =>
        app.UseMiddleware<ApiKeyAuthenticationMiddleware>();
}
