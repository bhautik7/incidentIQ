using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IncidentIQ.Shared.Auth;

/// <summary>The organization a request belongs to, established from its API key.</summary>
public sealed record TenantContext(Guid TenantId, string ApiKeyName);

public interface IApiKeyResolver
{
    /// <summary>Returns the owning tenant, or null when the key is unknown or disabled.</summary>
    TenantContext? Resolve(string apiKey);
}

public sealed class ApiKeyEntry
{
    /// <summary>Lower-case hex SHA-256 of the API key. The key itself is never stored.</summary>
    public string KeyHash { get; set; } = string.Empty;

    public Guid TenantId { get; set; }

    /// <summary>Label for logs and revocation, e.g. "acme-prod-agent".</summary>
    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}

public sealed class ApiKeyOptions
{
    public const string SectionName = "Ingestion:ApiKeys";

    public List<ApiKeyEntry> Keys { get; set; } = [];
}

/// <summary>
/// Resolves API keys from configuration.
///
/// Ingestion has no database connection by design - that is what keeps the
/// write path fast and available when PostgreSQL is not. Tenant lookup must
/// therefore be satisfied without a query, and configuration is the simplest
/// thing that does so.
///
/// The interface, not this implementation, is the point. The production
/// version reads the api_keys table into an in-memory snapshot refreshed in the
/// background, so the request path still performs no query. Swapping it changes
/// this one class; the endpoint does not know the difference.
/// </summary>
public sealed class ConfiguredApiKeyResolver : IApiKeyResolver
{
    private readonly Dictionary<string, TenantContext> _byHash;
    private readonly ILogger<ConfiguredApiKeyResolver> _logger;

    public ConfiguredApiKeyResolver(IOptions<ApiKeyOptions> options, ILogger<ConfiguredApiKeyResolver> logger)
    {
        _logger = logger;

        _byHash = options.Value.Keys
            .Where(k => k.IsActive && !string.IsNullOrWhiteSpace(k.KeyHash) && k.TenantId != Guid.Empty)
            .ToDictionary(
                k => k.KeyHash.Trim().ToLowerInvariant(),
                k => new TenantContext(k.TenantId, k.Name),
                StringComparer.Ordinal);

        if (_byHash.Count == 0)
        {
            // Loud, because the symptom is otherwise "every request returns 401"
            // with nothing explaining why.
            _logger.LogWarning(
                "No ingestion API keys are configured. Every request will be rejected with 401. "
                + "Populate Ingestion:ApiKeys:Keys.");
        }
        else
        {
            _logger.LogInformation("Loaded {Count} ingestion API key(s).", _byHash.Count);
        }
    }

    public TenantContext? Resolve(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        return _byHash.GetValueOrDefault(Hash(apiKey));
    }

    /// <summary>Lower-case hex SHA-256, matching how keys are stored in configuration and in the database.</summary>
    public static string Hash(string apiKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
}
