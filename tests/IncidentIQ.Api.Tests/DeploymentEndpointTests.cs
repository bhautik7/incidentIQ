using System.Net;
using System.Net.Http.Json;
using IncidentIQ.Domain.Entities;
using IncidentIQ.Domain.Enums;
using IncidentIQ.Persistence;
using IncidentIQ.Shared.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;
using Environment = IncidentIQ.Domain.Entities.Environment;

namespace IncidentIQ.Api.Tests;

/// <summary>
/// Recording a release.
///
/// The behaviour worth protecting is the back-correlation. A deploy job
/// reports a release seconds to minutes after it happened, and an incident
/// opened inside that gap points at no deployment - so the one screen where
/// the correlation matters most shows "no deployment was correlated" for the
/// incident the release actually caused.
/// </summary>
public sealed class DeploymentEndpointTests : IAsyncLifetime
{
    private static readonly Guid Acme = new("cc111111-1111-1111-1111-111111111111");
    private const string AcmeKey = "iiq_test_deploy_acme";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16")
        .WithDatabase("incidentiq_deploy_test")
        .Build();

    private WebApplicationFactory<Program> _factory = null!;
    private string _connectionString = null!;
    private Guid _serviceId;
    private Guid _environmentId;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();
        _factory = new ApiFactory(_connectionString);

        await using var db = NewContext();
        await db.Database.MigrateAsync();

        _serviceId = Guid.CreateVersion7();
        _environmentId = Guid.CreateVersion7();

        db.Organizations.Add(new Organization { Id = Acme, Name = "acme", Slug = "acme" });
        db.MonitoredServices.Add(new MonitoredService
        { Id = _serviceId, OrganizationId = Acme, Key = "payments-api", DisplayName = "payments-api" });
        db.Environments.Add(new Environment
        {
            Id = _environmentId, OrganizationId = Acme, Key = "production",
            DisplayName = "Production", IsProduction = true
        });

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private IncidentIQDbContext NewContext(Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<IncidentIQDbContext>()
            .UseIncidentIQPostgres(_connectionString)
            .Options;

        return new IncidentIQDbContext(options, new StaticTenantContext(tenantId));
    }

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
                ["Ingestion:ApiKeys:Keys:0:KeyHash"] = ConfiguredApiKeyResolver.Hash(AcmeKey),
                ["Ingestion:ApiKeys:Keys:0:TenantId"] = Acme.ToString(),
                ["Ingestion:ApiKeys:Keys:0:Name"] = "acme",
                ["Ingestion:ApiKeys:Keys:0:IsActive"] = "true",
            }));
        }
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", AcmeKey);
        return client;
    }

    private sealed record Result(
        Guid DeploymentId, string Service, string Environment, string Version,
        DateTimeOffset DeployedAt, IReadOnlyList<Guid> CorrelatedIncidentIds);

    private async Task<Guid> SeedIncidentAsync(DateTimeOffset firstSeen, IncidentStatus status)
    {
        var id = Guid.CreateVersion7();

        await using var db = NewContext();
        db.Incidents.Add(new Incident
        {
            Id = id,
            OrganizationId = Acme,
            MonitoredServiceId = _serviceId,
            EnvironmentId = _environmentId,
            DedupeKey = $"fp:{id:N}",
            DetectionRule = DetectionRule.CountThreshold,
            Title = "Something broke",
            Status = status,
            Severity = IncidentSeverity.High,
            OccurrenceCount = 10,
            FirstSeenAt = firstSeen,
            LastSeenAt = firstSeen.AddMinutes(1)
        });

        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task Records_a_release_and_makes_it_visible_to_correlation()
    {
        var deployedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        var response = await Client().PostAsJsonAsync("/api/v1/deployments", new
        {
            service = "payments-api",
            environment = "production",
            version = "2.8.4",
            commitSha = "7c1e9a4b2f08",
            deployedBy = "release-pipeline",
            deployedAt
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<Result>();
        Assert.NotNull(result);

        await using var db = NewContext(Acme);
        var stored = await db.Deployments.AsNoTracking().SingleAsync(d => d.Id == result.DeploymentId);

        Assert.Equal("2.8.4", stored.Version);
        Assert.Equal(_serviceId, stored.MonitoredServiceId);
        Assert.Equal(DeploymentStatus.Succeeded, stored.Status);
    }

    [Fact]
    public async Task Adopts_an_open_incident_that_began_after_the_release()
    {
        var deployedAt = DateTimeOffset.UtcNow.AddMinutes(-20);
        var incidentId = await SeedIncidentAsync(deployedAt.AddMinutes(3), IncidentStatus.Detected);

        var result = await (await Client().PostAsJsonAsync("/api/v1/deployments", new
        {
            service = "payments-api", environment = "production", version = "2.8.5", deployedAt
        })).Content.ReadFromJsonAsync<Result>();

        Assert.NotNull(result);
        Assert.Contains(incidentId, result.CorrelatedIncidentIds);

        await using var db = NewContext(Acme);
        var incident = await db.Incidents.AsNoTracking().SingleAsync(i => i.Id == incidentId);

        Assert.Equal(result.DeploymentId, incident.SuspectedDeploymentId);
    }

    [Fact]
    public async Task Leaves_alone_an_incident_that_began_before_the_release()
    {
        var deployedAt = DateTimeOffset.UtcNow.AddMinutes(-20);

        // Started first, so the release cannot have caused it. Attaching a
        // suspect here would be worse than attaching none - a wrong suspect
        // gets acted on.
        var incidentId = await SeedIncidentAsync(deployedAt.AddMinutes(-5), IncidentStatus.Detected);

        var result = await (await Client().PostAsJsonAsync("/api/v1/deployments", new
        {
            service = "payments-api", environment = "production", version = "2.8.6", deployedAt
        })).Content.ReadFromJsonAsync<Result>();

        Assert.NotNull(result);
        Assert.DoesNotContain(incidentId, result.CorrelatedIncidentIds);

        await using var db = NewContext(Acme);
        var incident = await db.Incidents.AsNoTracking().SingleAsync(i => i.Id == incidentId);

        Assert.Null(incident.SuspectedDeploymentId);
    }

    [Fact]
    public async Task Creates_the_service_when_the_release_is_the_first_thing_it_has_reported()
    {
        var result = await (await Client().PostAsJsonAsync("/api/v1/deployments", new
        {
            service = "brand-new-api", environment = "staging", version = "0.1.0"
        })).Content.ReadFromJsonAsync<Result>();

        Assert.NotNull(result);

        await using var db = NewContext(Acme);
        Assert.True(await db.MonitoredServices.AsNoTracking().AnyAsync(s => s.Key == "brand-new-api"));
        Assert.True(await db.Environments.AsNoTracking().AnyAsync(e => e.Key == "staging"));
    }

    [Fact]
    public async Task Refuses_a_release_dated_in_the_future()
    {
        // A future deployment sorts above every real one in "what shipped just
        // before this?" and would be blamed for incidents that predate it.
        var response = await Client().PostAsJsonAsync("/api/v1/deployments", new
        {
            service = "payments-api",
            environment = "production",
            version = "9.9.9",
            deployedAt = DateTimeOffset.UtcNow.AddHours(2)
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Requires_a_version()
    {
        var response = await Client().PostAsJsonAsync("/api/v1/deployments", new
        {
            service = "payments-api", environment = "production"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
