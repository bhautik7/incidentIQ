using IncidentIQ.Ingestion.Auth;
using IncidentIQ.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace IncidentIQ.Ingestion.Tests;

/// <summary>
/// Hosts the real ingestion application in memory with the real middleware,
/// the real validator and the real rate limiter - only Kafka is substituted.
/// </summary>
public sealed class IngestionApiFactory : WebApplicationFactory<Program>
{
    public const string ValidApiKey = "iiq_test_key";
    public static readonly Guid TenantId = new("11111111-1111-1111-1111-111111111111");

    public FakeEventProducer Producer { get; } = new();

    /// <summary>Overrides applied on top of appsettings, used to make limits testable.</summary>
    public Dictionary<string, string?> Settings { get; } = new()
    {
        ["Ingestion:ApiKeys:Keys:0:KeyHash"] = ConfiguredApiKeyResolver.Hash(ValidApiKey),
        ["Ingestion:ApiKeys:Keys:0:TenantId"] = TenantId.ToString(),
        ["Ingestion:ApiKeys:Keys:0:Name"] = "test-key",
        ["Ingestion:ApiKeys:Keys:0:IsActive"] = "true",
        ["Kafka:BootstrapServers"] = "localhost:59092"
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(Settings));

        builder.ConfigureServices(services =>
        {
            // The Kafka producer is a singleton that opens connections on
            // construction, so it is replaced rather than merely intercepted.
            services.RemoveAll<IEventProducer>();
            services.RemoveAll<KafkaEventProducer>();
            services.AddSingleton<IEventProducer>(Producer);
        });
    }

    public HttpClient CreateApiClient(string? apiKey = ValidApiKey)
    {
        var client = CreateClient();

        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add(ApiKeyAuthenticationMiddleware.ApiKeyHeader, apiKey);
        }

        return client;
    }
}
