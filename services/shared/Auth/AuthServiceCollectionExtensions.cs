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

    public static IApplicationBuilder UseIncidentIqApiKeyAuth(this IApplicationBuilder app) =>
        app.UseMiddleware<ApiKeyAuthenticationMiddleware>();
}
