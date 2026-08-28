using IncidentIQ.Api.Realtime;
using IncidentIQ.Shared.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IncidentIQ.Api.Tests;

/// <summary>
/// The hub is the one place in the product where tenant isolation is not
/// enforced by EF's global query filters.
///
/// Everywhere else, a missing filter fails closed: a query with no tenant
/// matches no rows. A hub has no query. The server decides who to push to, so
/// if the group name were wrong, one organization's incidents would arrive on
/// another's dashboard and nothing would object. That property is worth a test
/// that actually opens two connections and watches where a message lands.
///
/// No database here on purpose. The isolation being tested is a property of the
/// hub and the API key, not of persistence, and a Testcontainer would make this
/// slow enough to be skipped.
/// </summary>
public sealed class RealtimeHubTests : IAsyncLifetime
{
    private static readonly Guid Acme = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Globex = new("22222222-2222-2222-2222-222222222222");

    private const string AcmeKey = "iiq_hub_acme";
    private const string GlobexKey = "iiq_hub_globex";

    private WebApplicationFactory<Program> _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new HubFactory();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>
    /// The API without a connection string: persistence never registers, which
    /// is exactly the configuration that must still serve the hub.
    /// </summary>
    private sealed class HubFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Ingestion:ApiKeys:Keys:0:KeyHash"] = ConfiguredApiKeyResolver.Hash(AcmeKey),
                    ["Ingestion:ApiKeys:Keys:0:TenantId"] = Acme.ToString(),
                    ["Ingestion:ApiKeys:Keys:0:Name"] = "acme",
                    ["Ingestion:ApiKeys:Keys:0:IsActive"] = "true",

                    ["Ingestion:ApiKeys:Keys:1:KeyHash"] = ConfiguredApiKeyResolver.Hash(GlobexKey),
                    ["Ingestion:ApiKeys:Keys:1:TenantId"] = Globex.ToString(),
                    ["Ingestion:ApiKeys:Keys:1:Name"] = "globex",
                    ["Ingestion:ApiKeys:Keys:1:IsActive"] = "true",
                }));
        }
    }

    /// <summary>
    /// A hub connection over the in-memory test server.
    ///
    /// The access token is supplied the way the browser client supplies it, so
    /// this exercises the same authentication path rather than a shortcut.
    /// </summary>
    private HubConnection Connect(string apiKey)
    {
        var handler = _factory.Server.CreateHandler();

        return new HubConnectionBuilder()
            .WithUrl($"{_factory.Server.BaseAddress}hubs/incidents", options =>
            {
                options.HttpMessageHandlerFactory = _ => handler;
                options.AccessTokenProvider = () => Task.FromResult<string?>(apiKey);
            })
            .Build();
    }

    private IHubContext<IncidentHub> HubContext =>
        _factory.Services.GetRequiredService<IHubContext<IncidentHub>>();

    [Fact]
    public async Task A_client_receives_its_own_organizations_incidents()
    {
        await using var connection = Connect(AcmeKey);

        var received = new TaskCompletionSource<string>();
        connection.On<IncidentDetectedNotification>(
            RealtimeEvents.IncidentDetected, n => received.TrySetResult(n.Title));

        await connection.StartAsync();

        await HubContext.Clients
            .Group(RealtimeEvents.GroupFor(Acme))
            .SendAsync(RealtimeEvents.IncidentDetected, Notification("Acme database timeout"));

        var title = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Acme database timeout", title);
    }

    [Fact]
    public async Task A_client_never_receives_another_organizations_incidents()
    {
        // The test this file exists for. Both connections are open and both are
        // listening; only one of them should hear anything at all.
        await using var acme = Connect(AcmeKey);
        await using var globex = Connect(GlobexKey);

        var acmeHeard = new TaskCompletionSource<string>();
        var globexHeard = new TaskCompletionSource<string>();

        acme.On<IncidentDetectedNotification>(
            RealtimeEvents.IncidentDetected, n => acmeHeard.TrySetResult(n.Title));
        globex.On<IncidentDetectedNotification>(
            RealtimeEvents.IncidentDetected, n => globexHeard.TrySetResult(n.Title));

        await acme.StartAsync();
        await globex.StartAsync();

        await HubContext.Clients
            .Group(RealtimeEvents.GroupFor(Acme))
            .SendAsync(RealtimeEvents.IncidentDetected, Notification("Acme only"));

        Assert.Equal("Acme only", await acmeHeard.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        // Globex must still be waiting. Asserting on a timeout is weak evidence
        // in general, but here the alternative - that the message is merely slow
        // - would still be a leak the moment it arrived.
        var leaked = await Task.WhenAny(globexHeard.Task, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.NotSame(globexHeard.Task, leaked);
    }

    [Fact]
    public async Task A_connection_without_a_key_is_refused()
    {
        await using var connection = Connect(apiKey: string.Empty);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());

        Assert.Contains("401", failure.Message);
    }

    [Fact]
    public async Task A_connection_with_an_unknown_key_is_refused()
    {
        await using var connection = Connect("iiq_not_a_real_key");

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());

        Assert.Contains("401", failure.Message);
    }

    private static IncidentDetectedNotification Notification(string title) => new()
    {
        IncidentId = Guid.CreateVersion7(),
        Service = "payments-api",
        Environment = "production",
        Severity = "Critical",
        Title = title,
        DetectedAt = DateTimeOffset.UtcNow
    };
}
