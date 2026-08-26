using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IncidentIQ.Domain.Entities;
using IncidentIQ.Domain.Enums;
using IncidentIQ.Persistence;
using IncidentIQ.Shared.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Environment = IncidentIQ.Domain.Entities.Environment;

namespace IncidentIQ.Api.Tests;

/// <summary>
/// The read API against a real database, with two organizations seeded.
///
/// Tenant isolation is the property most worth proving here: the endpoints
/// contain no explicit organization filter, relying entirely on EF's global
/// query filters and the tenant the API key resolves to. That is the right
/// design, and it is only safe if it is tested.
/// </summary>
public sealed class IncidentEndpointTests : IAsyncLifetime
{
    private static readonly Guid Acme = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Globex = new("22222222-2222-2222-2222-222222222222");

    private const string AcmeKey = "iiq_test_acme_key";
    private const string GlobexKey = "iiq_test_globex_key";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16")
        .WithDatabase("incidentiq_api_test")
        .Build();

    private WebApplicationFactory<Program> _factory = null!;
    private Guid _acmeIncidentId;
    private Guid _globexIncidentId;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var connectionString = _postgres.GetConnectionString();

        _factory = new ApiFactory(connectionString);

        var options = new DbContextOptionsBuilder<IncidentIQDbContext>()
            .UseIncidentIQPostgres(connectionString)
            .Options;

        await using var db = new IncidentIQDbContext(options, new StaticTenantContext(null));
        await db.Database.MigrateAsync();

        _acmeIncidentId = await SeedAsync(db, Acme, "acme", "payments-api",
            "NpgsqlException: connection pool exhausted", IncidentSeverity.Critical, withAnalysis: true);
        _globexIncidentId = await SeedAsync(db, Globex, "globex", "shipping-api",
            "TimeoutException: carrier lookup timed out", IncidentSeverity.Medium, withAnalysis: false);
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            // Program.cs reads the connection string while building, before
            // ConfigureAppConfiguration callbacks apply. UseSetting reaches the
            // initial configuration, so persistence actually gets registered.
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

                ["Ingestion:ApiKeys:Keys:1:KeyHash"] = ConfiguredApiKeyResolver.Hash(GlobexKey),
                ["Ingestion:ApiKeys:Keys:1:TenantId"] = Globex.ToString(),
                ["Ingestion:ApiKeys:Keys:1:Name"] = "globex",
                ["Ingestion:ApiKeys:Keys:1:IsActive"] = "true",
            }));
        }
    }

    private static async Task<Guid> SeedAsync(
        IncidentIQDbContext db, Guid tenantId, string slug, string serviceKey,
        string title, IncidentSeverity severity, bool withAnalysis)
    {
        var serviceId = Guid.CreateVersion7();
        var environmentId = Guid.CreateVersion7();
        var patternId = Guid.CreateVersion7();
        var deploymentId = Guid.CreateVersion7();
        var incidentId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        db.Organizations.Add(new Organization { Id = tenantId, Name = slug, Slug = slug });
        db.MonitoredServices.Add(new MonitoredService
        { Id = serviceId, OrganizationId = tenantId, Key = serviceKey, DisplayName = serviceKey });
        db.Environments.Add(new Environment
        { Id = environmentId, OrganizationId = tenantId, Key = "production", DisplayName = "Production", IsProduction = true });
        db.Deployments.Add(new Deployment
        {
            Id = deploymentId, OrganizationId = tenantId, MonitoredServiceId = serviceId,
            EnvironmentId = environmentId, Version = "2.31.0", CommitSha = "9f4c2ab",
            DeployedAt = now.AddMinutes(-10), Status = DeploymentStatus.Succeeded
        });
        db.LogPatterns.Add(new LogPattern
        {
            Id = patternId, OrganizationId = tenantId, MonitoredServiceId = serviceId,
            EnvironmentId = environmentId, Fingerprint = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            Level = LogEventLevel.Error, ExceptionType = "System.TimeoutException",
            MessageTemplate = "Connection timeout for user {NUM}",
            SampleMessage = "Connection timeout for user 18273",
            OccurrenceCount = 412, FirstSeenAt = now.AddMinutes(-6), LastSeenAt = now
        });
        db.Incidents.Add(new Incident
        {
            Id = incidentId, OrganizationId = tenantId, MonitoredServiceId = serviceId,
            EnvironmentId = environmentId, LogPatternId = patternId,
            DedupeKey = $"fp:{incidentId}", DetectionRule = DetectionRule.NewErrorAfterDeployment,
            Title = title, Status = IncidentStatus.Detected, Severity = severity,
            OccurrenceCount = 412, FirstSeenAt = now.AddMinutes(-6), LastSeenAt = now,
            SuspectedDeploymentId = deploymentId
        });
        db.IncidentEvents.Add(new IncidentEvent
        {
            OrganizationId = tenantId, IncidentId = incidentId, Type = IncidentEventType.Created,
            OccurredAt = now.AddMinutes(-6), ActorType = ActorType.System, Message = "Opened by rule."
        });
        db.LogEvents.Add(new LogEvent
        {
            OrganizationId = tenantId, EventId = Guid.CreateVersion7(), MonitoredServiceId = serviceId,
            EnvironmentId = environmentId, LogPatternId = patternId,
            // Deliberately no IncidentId - production never sets it.
            OccurredAt = now, ReceivedAt = now, Level = LogEventLevel.Error,
            Message = "Connection timeout for user 18273", Host = "pod-a"
        });

        if (withAnalysis)
        {
            db.AiAnalyses.Add(new AiAnalysis
            {
                Id = Guid.CreateVersion7(), OrganizationId = tenantId, IncidentId = incidentId,
                AnalysisVersion = 1, Status = AiAnalysisStatus.Completed,
                ModelProvider = "anthropic", ModelName = "claude-opus-5", Confidence = 0.72m,
                Summary = "payments-api is timing out.", ProbableCause = "Release 2.31.0.",
                SuggestedActions = """["Check pool metrics","Diff 2.31.0"]""",
                SimilarIncidents = "[]"
            });
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return incidentId;
    }

    private HttpClient Client(string? apiKey)
    {
        var client = _factory.CreateClient();

        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add(ApiKeyAuthenticationMiddleware.ApiKeyHeader, apiKey);
        }

        return client;
    }

    // ---------------- Authentication ----------------

    [Fact]
    public async Task Listing_without_an_api_key_is_rejected()
    {
        var response = await Client(null).GetAsync("/api/v1/incidents");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------- Tenant isolation ----------------

    [Fact]
    public async Task A_key_sees_only_its_own_organizations_incidents()
    {
        var body = await Client(AcmeKey).GetFromJsonAsync<JsonElement>("/api/v1/incidents");
        var items = body.GetProperty("items").EnumerateArray().ToList();

        var incident = Assert.Single(items);
        Assert.Equal(_acmeIncidentId, incident.GetProperty("id").GetGuid());
        Assert.Equal("payments-api", incident.GetProperty("service").GetString());
    }

    [Fact]
    public async Task The_other_organization_sees_only_its_own()
    {
        var body = await Client(GlobexKey).GetFromJsonAsync<JsonElement>("/api/v1/incidents");
        var incident = Assert.Single(body.GetProperty("items").EnumerateArray().ToList());

        Assert.Equal(_globexIncidentId, incident.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Fetching_another_organizations_incident_by_id_is_a_404()
    {
        // 404 rather than 403: confirming the id exists would itself leak.
        var response = await Client(AcmeKey).GetAsync($"/api/v1/incidents/{_globexIncidentId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Stats_are_scoped_to_the_calling_organization()
    {
        var body = await Client(AcmeKey).GetFromJsonAsync<JsonElement>("/api/v1/stats");

        Assert.Equal(1, body.GetProperty("detected").GetInt32());
        Assert.Equal(1, body.GetProperty("critical").GetInt32());
        Assert.Equal(412, body.GetProperty("totalOccurrences").GetInt64());
    }

    [Fact]
    public async Task Services_are_scoped_to_the_calling_organization()
    {
        var services = await Client(GlobexKey).GetFromJsonAsync<JsonElement>("/api/v1/services");
        var service = Assert.Single(services.EnumerateArray().ToList());

        Assert.Equal("shipping-api", service.GetProperty("key").GetString());
        Assert.Equal(1, service.GetProperty("activeIncidents").GetInt32());
    }

    // ---------------- Detail ----------------

    [Fact]
    public async Task Detail_returns_everything_the_incident_page_needs()
    {
        var body = await Client(AcmeKey).GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{_acmeIncidentId}");

        Assert.Equal("Critical", body.GetProperty("incident").GetProperty("severity").GetString());

        var pattern = body.GetProperty("pattern");
        Assert.Equal("Connection timeout for user {NUM}", pattern.GetProperty("messageTemplate").GetString());

        var deployment = body.GetProperty("deployment");
        Assert.Equal("2.31.0", deployment.GetProperty("version").GetString());
        Assert.True(deployment.GetProperty("minutesBeforeIncident").GetDouble() > 0);

        Assert.Single(body.GetProperty("timeline").EnumerateArray().ToList());

        // Samples are joined by pattern, not by incident: log_events.incident_id
        // is never populated, because samples are written during normalisation,
        // before detection has decided an incident exists.
        Assert.Single(body.GetProperty("samples").EnumerateArray().ToList());
    }

    [Fact]
    public async Task Detail_includes_the_analysis_and_says_which_model_wrote_it()
    {
        var body = await Client(AcmeKey).GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{_acmeIncidentId}");
        var analysis = body.GetProperty("analysis");

        Assert.Equal("anthropic", analysis.GetProperty("modelProvider").GetString());
        Assert.Equal("claude-opus-5", analysis.GetProperty("modelName").GetString());
        Assert.Equal(2, analysis.GetProperty("suggestedActions").GetArrayLength());
    }

    [Fact]
    public async Task An_incident_with_no_analysis_yet_returns_null_rather_than_failing()
    {
        var body = await Client(GlobexKey).GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{_globexIncidentId}");

        Assert.Equal(JsonValueKind.Null, body.GetProperty("analysis").ValueKind);
        Assert.False(body.GetProperty("incident").GetProperty("hasAnalysis").GetBoolean());
    }

    // ---------------- Filtering and paging ----------------

    [Fact]
    public async Task Filtering_by_severity_works()
    {
        var match = await Client(AcmeKey).GetFromJsonAsync<JsonElement>("/api/v1/incidents?severity=Critical");
        Assert.Equal(1, match.GetProperty("totalCount").GetInt32());

        var miss = await Client(AcmeKey).GetFromJsonAsync<JsonElement>("/api/v1/incidents?severity=Low");
        Assert.Equal(0, miss.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Filtering_by_service_works()
    {
        var miss = await Client(AcmeKey).GetFromJsonAsync<JsonElement>("/api/v1/incidents?service=orders-api");

        Assert.Equal(0, miss.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Search_matches_the_title_case_insensitively()
    {
        var body = await Client(AcmeKey).GetFromJsonAsync<JsonElement>("/api/v1/incidents?search=POOL");

        Assert.Equal(1, body.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task An_unknown_status_is_a_400_that_lists_the_valid_ones()
    {
        var response = await Client(AcmeKey).GetAsync("/api/v1/incidents?status=exploded");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Detected", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Page_size_is_clamped_so_one_request_cannot_ask_for_everything()
    {
        var body = await Client(AcmeKey).GetFromJsonAsync<JsonElement>("/api/v1/incidents?pageSize=100000");

        Assert.Equal(100, body.GetProperty("pageSize").GetInt32());
    }
}
