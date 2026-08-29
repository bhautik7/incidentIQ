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
/// The diagnose endpoint against a real database.
///
/// The property worth proving is that opening an incident on demand does not
/// break the invariant the whole product rests on: one active incident per
/// pattern. This endpoint is the second thing in the system able to open one,
/// and a second opener that does not respect the dedupe key would produce
/// exactly the duplicate flood IncidentIQ exists to prevent.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DiagnoseEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Guid Acme = new("aa111111-1111-1111-1111-111111111111");
    private static readonly Guid Globex = new("bb222222-2222-2222-2222-222222222222");

    private const string AcmeKey = "iiq_test_diagnose_acme";
    private const string GlobexKey = "iiq_test_diagnose_globex";

    private const string LoudFingerprint = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string QuietFingerprint = "2222222222222222222222222222222222222222222222222222222222222222";

    private WebApplicationFactory<Program> _factory = null!;
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        // Its own database inside the shared container, already migrated.
        _connectionString = await postgres.CreateDatabaseAsync();
        _factory = new ApiFactory(_connectionString);

        await using var db = NewContext();

        await SeedAsync(db, Acme, "acme", "payments-api");
        await SeedAsync(db, Globex, "globex", "payments-api");
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>
    /// A context scoped to one tenant, because the global query filter fails
    /// closed: a null tenant matches no rows at all, so an unscoped context
    /// reads an empty database and every assertion about what was written
    /// would pass or fail for the wrong reason.
    /// </summary>
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

                ["Ingestion:ApiKeys:Keys:1:KeyHash"] = ConfiguredApiKeyResolver.Hash(GlobexKey),
                ["Ingestion:ApiKeys:Keys:1:TenantId"] = Globex.ToString(),
                ["Ingestion:ApiKeys:Keys:1:Name"] = "globex",
                ["Ingestion:ApiKeys:Keys:1:IsActive"] = "true",
            }));
        }
    }

    /// <summary>
    /// Two error patterns for one service, one of them four times louder.
    ///
    /// Neither is anywhere near a detection threshold, which is the situation
    /// this endpoint exists for: an upload that no rule would ever open an
    /// incident for.
    /// </summary>
    private static async Task SeedAsync(IncidentIQDbContext db, Guid tenantId, string slug, string serviceKey)
    {
        var serviceId = Guid.CreateVersion7();
        var environmentId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        db.Organizations.Add(new Organization { Id = tenantId, Name = slug, Slug = slug });
        db.MonitoredServices.Add(new MonitoredService
        { Id = serviceId, OrganizationId = tenantId, Key = serviceKey, DisplayName = serviceKey });
        db.Environments.Add(new Environment
        {
            Id = environmentId, OrganizationId = tenantId, Key = "production",
            DisplayName = "Production", IsProduction = true
        });

        // Fingerprints are per tenant in production; here they are shared on
        // purpose, so a leak across the tenant boundary would open an incident
        // in the wrong organization and fail loudly rather than silently pass.
        AddPattern(db, tenantId, serviceId, environmentId, LoudFingerprint,
            "Invalid column name '{TOKEN}'.", "Invalid column name 'Status'.", now, occurrences: 4);

        AddPattern(db, tenantId, serviceId, environmentId, QuietFingerprint,
            "Timeout expired after {NUM}ms", "Timeout expired after 30000ms", now, occurrences: 1);

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static void AddPattern(
        IncidentIQDbContext db, Guid tenantId, Guid serviceId, Guid environmentId,
        string fingerprint, string template, string sample, DateTimeOffset now, int occurrences)
    {
        var patternId = Guid.CreateVersion7();

        db.LogPatterns.Add(new LogPattern
        {
            Id = patternId,
            OrganizationId = tenantId,
            MonitoredServiceId = serviceId,
            EnvironmentId = environmentId,
            Fingerprint = fingerprint,
            Level = LogEventLevel.Error,
            ExceptionType = "System.Data.SqlClient.SqlException",
            MessageTemplate = template,
            SampleMessage = sample,
            OccurrenceCount = occurrences,
            FirstSeenAt = now.AddMinutes(-10),
            LastSeenAt = now.AddMinutes(-1)
        });

        // The raw lines, which is what the endpoint ranks by - deliberately not
        // the minute buckets, which a different consumer group writes later.
        for (var i = 0; i < occurrences; i++)
        {
            db.RawLogEvents.Add(new RawLogEvent
            {
                OrganizationId = tenantId,
                EventId = Guid.CreateVersion7(),
                MonitoredServiceId = serviceId,
                EnvironmentId = environmentId,
                LogPatternId = patternId,
                OccurredAt = now.AddMinutes(-2),
                ReceivedAt = now,
                Level = LogEventLevel.Error,
                Message = sample,
                Host = "pod-a"
            });
        }
    }

    private HttpClient Client(string apiKey)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    private static object Request(string service = "payments-api", int lookbackMinutes = 30) => new
    {
        service,
        environment = "production",
        since = DateTimeOffset.UtcNow.AddMinutes(-lookbackMinutes)
    };

    private sealed record Result(
        string Status, Guid? IncidentId, string? Fingerprint, string? Title,
        long OccurrenceCount, int PatternsFound, string Message);

    [Fact]
    public async Task Opens_an_incident_for_the_loudest_pattern_in_the_window()
    {
        var response = await Client(AcmeKey).PostAsJsonAsync("/api/v1/diagnose", Request());
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<Result>();

        Assert.NotNull(result);
        Assert.Equal("opened", result.Status);
        Assert.Equal(LoudFingerprint, result.Fingerprint);
        Assert.Equal(4, result.OccurrenceCount);
        Assert.Equal(2, result.PatternsFound);

        await using var db = NewContext(Acme);
        var incident = await db.Incidents.AsNoTracking().SingleAsync(i => i.Id == result.IncidentId);

        // The rule is the whole audit trail for an incident nothing detected.
        Assert.Equal(DetectionRule.UserRequested, incident.DetectionRule);
        Assert.Equal(Acme, incident.OrganizationId);
        Assert.Equal($"fp:{LoudFingerprint}", incident.DedupeKey);
        Assert.Equal(IncidentStatus.Detected, incident.Status);

        // Announced and queued for analysis by the same transaction, or the
        // incident would sit on the dashboard forever with nothing to say.
        var outbox = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.AggregateId == incident.Id)
            .Select(m => m.EventType)
            .ToListAsync();

        Assert.Contains("incident.detected", outbox);
        Assert.Contains("incident.analysis.requested", outbox);
    }

    [Fact]
    public async Task Returns_the_open_incident_rather_than_opening_a_second_one()
    {
        var client = Client(AcmeKey);

        var first = await (await client.PostAsJsonAsync("/api/v1/diagnose", Request()))
            .Content.ReadFromJsonAsync<Result>();
        var second = await (await client.PostAsJsonAsync("/api/v1/diagnose", Request()))
            .Content.ReadFromJsonAsync<Result>();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("existing", second.Status);
        Assert.Equal(first.IncidentId, second.IncidentId);

        await using var db = NewContext(Acme);
        var count = await db.Incidents.AsNoTracking()
            .CountAsync(i => i.OrganizationId == Acme && i.DedupeKey == $"fp:{LoudFingerprint}");

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Reports_pending_for_a_service_that_has_not_been_processed()
    {
        var response = await Client(AcmeKey)
            .PostAsJsonAsync("/api/v1/diagnose", Request(service: "never-seen-api"));

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<Result>();

        Assert.NotNull(result);
        // Pending rather than 404: ingestion creates the service row as it
        // processes the first event, so this is "not yet", not "no such thing".
        Assert.Equal("pending", result.Status);
        Assert.Null(result.IncidentId);
    }

    [Fact]
    public async Task Cannot_open_an_incident_in_another_organization()
    {
        // Globex's patterns carry the same fingerprints as Acme's, so a tenant
        // filter that leaked would find Acme's rows and attribute this incident
        // to the wrong organization.
        var result = await (await Client(GlobexKey).PostAsJsonAsync("/api/v1/diagnose", Request()))
            .Content.ReadFromJsonAsync<Result>();

        Assert.NotNull(result);
        Assert.NotNull(result.IncidentId);

        await using var globexDb = NewContext(Globex);
        var incident = await globexDb.Incidents.AsNoTracking().SingleAsync(i => i.Id == result.IncidentId);

        Assert.Equal(Globex, incident.OrganizationId);

        await using var acmeDb = NewContext(Acme);
        Assert.Equal(0, await acmeDb.Incidents.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Requires_a_service_and_an_environment()
    {
        var response = await Client(AcmeKey)
            .PostAsJsonAsync("/api/v1/diagnose", new { environment = "production" });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }
}
