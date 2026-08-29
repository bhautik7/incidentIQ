using System.Net.Http.Json;
using IncidentIQ.Domain.Entities;
using IncidentIQ.Domain.Enums;
using IncidentIQ.Persistence;
using IncidentIQ.Shared.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace IncidentIQ.Api.Tests;

/// <summary>
/// What the dashboard is told about who it is.
///
/// The sidebar used to have an organization name and a person's name written
/// into it as literals. The person named was not the user actions were
/// attributed to, so the page said one name while the incident timeline
/// recorded another - the only place in the UI that stated something untrue.
///
/// The case worth protecting is the second one below. A key bound to no user
/// can read everything and act on nothing, and a UI that cannot tell the
/// difference invites someone to press Resolve and receive a 403 with no idea
/// why.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SessionEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Guid Acme = new("dd111111-1111-1111-1111-111111111111");
    private static readonly Guid AcmeActor = new("dd111111-0000-0000-0000-0000000000a1");

    private const string BoundKey = "iiq_test_session_bound";
    private const string UnboundKey = "iiq_test_session_unbound";

    private WebApplicationFactory<Program> _factory = null!;
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync();
        _factory = new ApiFactory(_connectionString);

        var options = new DbContextOptionsBuilder<IncidentIQDbContext>()
            .UseIncidentIQPostgres(_connectionString)
            .Options;

        await using var db = new IncidentIQDbContext(options, new StaticTenantContext(null));

        db.Organizations.Add(new Organization { Id = Acme, Name = "Acme Corp", Slug = "acme" });
        db.Users.Add(new User
        {
            Id = AcmeActor,
            OrganizationId = Acme,
            Email = "owner@acme.test",
            DisplayName = "Ada Owner",
            Status = UserStatus.Active
        });

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Postgres", connectionString);

            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString,
                ["IncidentIQ:RunMigrationsOnStartup"] = "false",
                ["IncidentIQ:SeedDevelopmentData"] = "false",

                ["Ingestion:ApiKeys:Keys:0:KeyHash"] = ConfiguredApiKeyResolver.Hash(BoundKey),
                ["Ingestion:ApiKeys:Keys:0:TenantId"] = Acme.ToString(),
                ["Ingestion:ApiKeys:Keys:0:Name"] = "acme-dashboard",
                ["Ingestion:ApiKeys:Keys:0:IsActive"] = "true",
                ["Ingestion:ApiKeys:Keys:0:ActorUserId"] = AcmeActor.ToString(),

                // Deliberately no ActorUserId: this is the shape ingestion's
                // own key has, and the shape that cannot perform an action.
                ["Ingestion:ApiKeys:Keys:1:KeyHash"] = ConfiguredApiKeyResolver.Hash(UnboundKey),
                ["Ingestion:ApiKeys:Keys:1:TenantId"] = Acme.ToString(),
                ["Ingestion:ApiKeys:Keys:1:Name"] = "acme-ingestion",
                ["Ingestion:ApiKeys:Keys:1:IsActive"] = "true",
            }));
        }
    }

    private HttpClient Client(string apiKey)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    private sealed record Organization_(Guid Id, string Name, string Slug);
    private sealed record Actor(Guid UserId, string DisplayName, string Email);
    private sealed record Session(Organization_? Organization, Actor? Actor, string ApiKeyName);

    [Fact]
    public async Task Reports_the_organization_and_the_user_actions_are_recorded_against()
    {
        var session = await Client(BoundKey).GetFromJsonAsync<Session>("/api/v1/me");

        Assert.NotNull(session);
        Assert.Equal("Acme Corp", session.Organization?.Name);
        Assert.Equal("Ada Owner", session.Actor?.DisplayName);
        Assert.Equal(AcmeActor, session.Actor?.UserId);
        Assert.Equal("acme-dashboard", session.ApiKeyName);
    }

    [Fact]
    public async Task Says_plainly_when_the_key_is_bound_to_nobody()
    {
        // Not an error, and not a guess at who is holding the keyboard. The
        // dashboard needs this to explain, in advance, why every action button
        // is going to be refused.
        var session = await Client(UnboundKey).GetFromJsonAsync<Session>("/api/v1/me");

        Assert.NotNull(session);
        Assert.Null(session.Actor);
        Assert.Equal("Acme Corp", session.Organization?.Name);
        Assert.Equal("acme-ingestion", session.ApiKeyName);
    }

    [Fact]
    public async Task Refuses_an_unauthenticated_caller()
    {
        var response = await _factory.CreateClient().GetAsync("/api/v1/me");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
