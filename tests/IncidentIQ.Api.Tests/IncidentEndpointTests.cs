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

    // Ordering cannot be proven against a single row, and the two tenants above
    // hold exactly one incident each precisely so the isolation tests can assert
    // Single. Sorting therefore gets a tenant of its own.
    private static readonly Guid Initech = new("33333333-3333-3333-3333-333333333333");

    // Acme's key acts as a user; Globex's deliberately does not, so the
    // "this key cannot act as a person" path has something to exercise.
    private static readonly Guid AcmeActor = new("11111111-0000-0000-0000-0000000000a1");
    private static readonly Guid AcmeSecondUser = new("11111111-0000-0000-0000-0000000000a2");
    private static readonly Guid GlobexUser = new("22222222-0000-0000-0000-0000000000b1");

    private const string AcmeKey = "iiq_test_acme_key";
    private const string GlobexKey = "iiq_test_globex_key";
    private const string InitechKey = "iiq_test_initech_key";

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

        await SeedSortFixtureAsync(db);
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
                ["Ingestion:ApiKeys:Keys:0:ActorUserId"] = AcmeActor.ToString(),

                ["Ingestion:ApiKeys:Keys:1:KeyHash"] = ConfiguredApiKeyResolver.Hash(GlobexKey),
                ["Ingestion:ApiKeys:Keys:1:TenantId"] = Globex.ToString(),
                ["Ingestion:ApiKeys:Keys:1:Name"] = "globex",
                ["Ingestion:ApiKeys:Keys:1:IsActive"] = "true",

                ["Ingestion:ApiKeys:Keys:2:KeyHash"] = ConfiguredApiKeyResolver.Hash(InitechKey),
                ["Ingestion:ApiKeys:Keys:2:TenantId"] = Initech.ToString(),
                ["Ingestion:ApiKeys:Keys:2:Name"] = "initech",
                ["Ingestion:ApiKeys:Keys:2:IsActive"] = "true",
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

        foreach (var (userId, email, name) in tenantId == Acme
            ? [(AcmeActor, "ada@acme.test", "Ada Owner"), (AcmeSecondUser, "ravi@acme.test", "Ravi Responder")]
            : new[] { (GlobexUser, "gina@globex.test", "Gina Owner") })
        {
            db.Users.Add(new User
            {
                Id = userId, OrganizationId = tenantId, Email = email,
                DisplayName = name, Status = UserStatus.Active
            });
        }

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

    /// <summary>
    /// A tenant with four incidents that disagree on every sortable column, so
    /// each ordering has a different correct answer and a sort that silently
    /// does nothing cannot pass.
    ///
    /// Severity is stored as a string, so the alphabetical order of the four
    /// values (Critical, High, Low, Medium) is deliberately not the severity
    /// order. A sort that fell through to the database's default collation
    /// would put Low second, and these tests would catch it.
    /// </summary>
    private static async Task SeedSortFixtureAsync(IncidentIQDbContext db)
    {
        var environmentId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        db.Organizations.Add(new Organization { Id = Initech, Name = "initech", Slug = "initech" });
        db.Environments.Add(new Environment
        {
            Id = environmentId, OrganizationId = Initech, Key = "production",
            DisplayName = "Production", IsProduction = true
        });

        // title, service, severity, status, minutes since last seen, occurrences
        //
        // Service keys run in reverse alphabetical order against the titles, so
        // a sort that quietly ignored the column would return the title order
        // and be caught. The three Medium/Detected rows exist to create real
        // ties, which is the only way to exercise the tiebreaker.
        var rows = new[]
        {
            ("Alpha pool exhausted", "zulu-api", IncidentSeverity.Low, IncidentStatus.Resolved, 1, 9000L),
            ("Bravo lookup timeout", "yankee-api", IncidentSeverity.Critical, IncidentStatus.Ignored, 2, 40L),
            ("Charlie queue backlog", "xray-api", IncidentSeverity.Medium, IncidentStatus.Detected, 3, 700L),
            ("Delta cache miss storm", "whiskey-api", IncidentSeverity.High, IncidentStatus.Investigating, 4, 15L),
            ("Echo disk pressure", "victor-api", IncidentSeverity.Medium, IncidentStatus.Detected, 5, 300L),
            ("Foxtrot retry storm", "uniform-api", IncidentSeverity.Medium, IncidentStatus.Detected, 6, 120L)
        };

        foreach (var (title, serviceKey, severity, status, minutesAgo, occurrences) in rows)
        {
            var serviceId = Guid.CreateVersion7();
            var incidentId = Guid.CreateVersion7();

            db.MonitoredServices.Add(new MonitoredService
            { Id = serviceId, OrganizationId = Initech, Key = serviceKey, DisplayName = serviceKey });

            db.Incidents.Add(new Incident
            {
                Id = incidentId, OrganizationId = Initech, MonitoredServiceId = serviceId,
                EnvironmentId = environmentId, DedupeKey = $"fp:{incidentId}",
                DetectionRule = DetectionRule.CountThreshold, Title = title,
                Status = status, Severity = severity, OccurrenceCount = occurrences,
                FirstSeenAt = now.AddMinutes(-minutesAgo - 30), LastSeenAt = now.AddMinutes(-minutesAgo)
            });
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>One field of the sort fixture, in the order the endpoint returned it.</summary>
    private async Task<List<string>> SortedFieldAsync(string query, string field = "title")
    {
        var body = await Client(InitechKey).GetFromJsonAsync<JsonElement>($"/api/v1/incidents?status=all&{query}");

        return body.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty(field).GetString()!)
            .ToList();
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

    // ---------------- Lifecycle actions ----------------

    private static StringContent Json(string json) => new(json, System.Text.Encoding.UTF8, "application/json");

    [Fact]
    public async Task Acknowledging_takes_the_incident_and_records_who_took_it()
    {
        var response = await Client(AcmeKey).PostAsync($"/api/v1/incidents/{_acmeIncidentId}/acknowledge", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Investigating",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var detail = await Client(AcmeKey).GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{_acmeIncidentId}");
        Assert.Equal("Ada Owner", detail.GetProperty("owner").GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Acknowledging_twice_is_a_409_that_names_both_states()
    {
        await Client(AcmeKey).PostAsync($"/api/v1/incidents/{_acmeIncidentId}/acknowledge", null);
        var second = await Client(AcmeKey).PostAsync($"/api/v1/incidents/{_acmeIncidentId}/acknowledge", null);

        // Two people acting on the same stale screen is the common case, and
        // the second one needs to be told what already happened.
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var body = await second.Content.ReadAsStringAsync();
        Assert.Contains("Investigating", body);
        Assert.Contains("Detected", body);
    }

    [Fact]
    public async Task A_key_not_bound_to_a_user_cannot_perform_an_attributable_action()
    {
        // Globex's key authenticates fine - it just is not a person, and
        // resolving an incident has to be attributable to one.
        var response = await Client(GlobexKey).PostAsync($"/api/v1/incidents/{_globexIncidentId}/acknowledge", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("not bound to a user", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Acting_on_another_organizations_incident_is_a_404()
    {
        var response = await Client(AcmeKey).PostAsync($"/api/v1/incidents/{_globexIncidentId}/acknowledge", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Resolving_keeps_the_resolution_notes()
    {
        var response = await Client(AcmeKey).PostAsync($"/api/v1/incidents/{_acmeIncidentId}/resolve",
            Json("""{"resolutionNotes":"Raised the pool ceiling."}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var detail = await Client(AcmeKey).GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{_acmeIncidentId}");
        Assert.Equal("Resolved", detail.GetProperty("incident").GetProperty("status").GetString());

        var timeline = detail.GetProperty("timeline").EnumerateArray().ToList();
        Assert.Contains(timeline, entry => entry.GetProperty("message").GetString()!.Contains("Raised the pool ceiling"));
    }

    [Fact]
    public async Task Resolving_is_allowed_straight_from_detected()
    {
        // Plenty of incidents are fixed by whoever spots them, with no handover.
        var response = await Client(AcmeKey).PostAsync($"/api/v1/incidents/{_acmeIncidentId}/resolve", Json("{}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Assigning_names_the_assignee_on_the_timeline()
    {
        var response = await Client(AcmeKey).PostAsync($"/api/v1/incidents/{_acmeIncidentId}/assign",
            Json($$"""{"userId":"{{AcmeSecondUser}}"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var detail = await Client(AcmeKey).GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{_acmeIncidentId}");

        // Assignment is not a status change: it stays where it was.
        Assert.Equal("Detected", detail.GetProperty("incident").GetProperty("status").GetString());
        Assert.Equal("Ravi Responder", detail.GetProperty("owner").GetProperty("displayName").GetString());

        var timeline = detail.GetProperty("timeline").EnumerateArray().ToList();
        Assert.Contains(timeline, entry =>
            entry.GetProperty("type").GetString() == "Assigned"
            && entry.GetProperty("actorName").GetString() == "Ada Owner");
    }

    [Fact]
    public async Task A_user_from_another_organization_cannot_be_assigned()
    {
        // The lookup runs through the global query filter, so a cross-tenant
        // id does not resolve - it does not merely fail a check.
        var response = await Client(AcmeKey).PostAsync($"/api/v1/incidents/{_acmeIncidentId}/assign",
            Json($$"""{"userId":"{{GlobexUser}}"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_note_lands_on_the_same_timeline_as_the_automated_events()
    {
        var response = await Client(AcmeKey).PostAsync($"/api/v1/incidents/{_acmeIncidentId}/notes",
            Json("""{"note":"Pool saturation confirmed in pg_stat_activity."}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var detail = await Client(AcmeKey).GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{_acmeIncidentId}");
        var timeline = detail.GetProperty("timeline").EnumerateArray().ToList();

        var note = Assert.Single(timeline, entry => entry.GetProperty("type").GetString() == "Commented");
        Assert.Equal("Ada Owner", note.GetProperty("actorName").GetString());
    }

    [Fact]
    public async Task An_empty_note_is_rejected_rather_than_recorded()
    {
        var response = await Client(AcmeKey).PostAsync($"/api/v1/incidents/{_acmeIncidentId}/notes",
            Json("""{"note":"   "}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Ignoring_without_a_reason_is_rejected()
    {
        // Ignoring is the one transition with no evidence behind it, so the
        // reason is the entire record of why.
        var response = await Client(AcmeKey).PostAsync($"/api/v1/incidents/{_acmeIncidentId}/ignore", Json("{}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Available_actions_follow_the_status()
    {
        var detected = await Client(AcmeKey).GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{_acmeIncidentId}");
        var beforeActions = detected.GetProperty("availableActions").EnumerateArray()
            .Select(a => a.GetString()).ToList();

        Assert.Contains("acknowledge", beforeActions);
        Assert.DoesNotContain("reopen", beforeActions);

        await Client(AcmeKey).PostAsync($"/api/v1/incidents/{_acmeIncidentId}/resolve", Json("{}"));

        var resolved = await Client(AcmeKey).GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{_acmeIncidentId}");
        var afterActions = resolved.GetProperty("availableActions").EnumerateArray()
            .Select(a => a.GetString()).ToList();

        Assert.Contains("reopen", afterActions);
        Assert.DoesNotContain("acknowledge", afterActions);
    }

    [Fact]
    public async Task Requesting_an_analysis_queues_the_next_version_through_the_outbox()
    {
        var response = await Client(AcmeKey).PostAsync($"/api/v1/incidents/{_acmeIncidentId}/analyze", null);

        // 202: asked for, not performed.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // The fixture already holds a completed version 1, so the next is 2.
        Assert.Equal(2, body.GetProperty("analysisVersion").GetInt32());

        var options = new DbContextOptionsBuilder<IncidentIQDbContext>()
            .UseIncidentIQPostgres(_postgres.GetConnectionString())
            .Options;

        await using var db = new IncidentIQDbContext(options, new StaticTenantContext(Acme));

        // Enqueued rather than produced: the request commits with the decision
        // to make it, so a broker that is down delays it instead of losing it.
        var queued = await db.OutboxMessages
            .Where(m => m.AggregateId == _acmeIncidentId)
            .ToListAsync();

        Assert.Contains(queued, m => m.Topic == "incidents.analysis.requested");
    }

    [Fact]
    public async Task The_member_list_does_not_cross_organizations()
    {
        var acme = await Client(AcmeKey).GetFromJsonAsync<JsonElement>("/api/v1/users");
        var names = acme.EnumerateArray().Select(u => u.GetProperty("displayName").GetString()).ToList();

        Assert.Equal(["Ada Owner", "Ravi Responder"], names);
    }

    // ---------------- Sorting ----------------

    [Fact]
    public async Task The_default_order_is_most_recently_active_first()
    {
        // No sort parameter at all: the queue's default question is "what is
        // still firing", so recency leads.
        Assert.Equal(
            ["Alpha pool exhausted", "Bravo lookup timeout", "Charlie queue backlog",
             "Delta cache miss storm", "Echo disk pressure", "Foxtrot retry storm"],
            await SortedFieldAsync(string.Empty));
    }

    [Fact]
    public async Task Sorting_by_severity_ranks_the_enum_rather_than_the_stored_string()
    {
        // The failure this guards against is real: severity is a string column,
        // so an unranked ORDER BY yields Critical, High, Low, Medium - putting
        // the least urgent incident second.
        Assert.Equal(
            ["Critical", "High", "Medium", "Medium", "Medium", "Low"],
            await SortedFieldAsync("sort=severity", "severity"));

        Assert.Equal(
            ["Low", "Medium", "Medium", "Medium", "High", "Critical"],
            await SortedFieldAsync("sort=severity&direction=asc", "severity"));
    }

    [Fact]
    public async Task Sorting_by_status_ranks_by_how_much_attention_it_wants()
    {
        Assert.Equal(
            ["Detected", "Detected", "Detected", "Investigating", "Resolved", "Ignored"],
            await SortedFieldAsync("sort=status", "status"));
    }

    [Fact]
    public async Task Sorting_by_occurrences_is_numeric()
    {
        // 9000 before 700 before 120 before 40. Lexical ordering of the same
        // values would put 120 first.
        Assert.Equal(
            ["Alpha pool exhausted", "Charlie queue backlog", "Echo disk pressure",
             "Foxtrot retry storm", "Bravo lookup timeout", "Delta cache miss storm"],
            await SortedFieldAsync("sort=occurrences"));
    }

    [Fact]
    public async Task Sorting_by_service_ascends_by_default_because_names_read_that_way()
    {
        Assert.Equal(
            ["uniform-api", "victor-api", "whiskey-api", "xray-api", "yankee-api", "zulu-api"],
            await SortedFieldAsync("sort=service", "service"));
    }

    [Fact]
    public async Task Sorting_by_first_seen_is_a_real_column_and_not_an_alias_for_last_seen()
    {
        Assert.Equal(
            ["Foxtrot retry storm", "Echo disk pressure", "Delta cache miss storm",
             "Charlie queue backlog", "Bravo lookup timeout", "Alpha pool exhausted"],
            await SortedFieldAsync("sort=firstSeen&direction=asc"));
    }

    [Fact]
    public async Task Sort_keys_and_directions_are_case_insensitive()
    {
        Assert.Equal(
            await SortedFieldAsync("sort=severity&direction=asc"),
            await SortedFieldAsync("sort=SEVERITY&direction=ASC"));
    }

    [Fact]
    public async Task Paging_a_sort_with_tied_rows_repeats_nothing_and_loses_nothing()
    {
        // Three incidents share the status Detected. Without a total order the
        // database may break that tie differently per query, which with offset
        // paging shows one row on both page one and page two while another is
        // never shown at all.
        var seen = new List<Guid>();

        for (var page = 1; page <= 3; page++)
        {
            var body = await Client(InitechKey).GetFromJsonAsync<JsonElement>(
                $"/api/v1/incidents?status=all&sort=status&pageSize=2&page={page}");

            seen.AddRange(body.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("id").GetGuid()));
        }

        Assert.Equal(6, seen.Count);
        Assert.Equal(6, seen.Distinct().Count());
    }

    [Fact]
    public async Task An_unknown_sort_column_is_a_400_that_lists_the_valid_ones()
    {
        var response = await Client(InitechKey).GetAsync("/api/v1/incidents?sort=drop%20table");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("severity", body);
        Assert.Contains("lastSeen", body);
    }

    [Fact]
    public async Task An_unknown_sort_direction_is_a_400()
    {
        var response = await Client(InitechKey).GetAsync("/api/v1/incidents?sort=severity&direction=sideways");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Sorting_does_not_widen_what_a_tenant_can_see()
    {
        // Ordering rewrites the query, which is exactly the kind of change that
        // can drop a global filter without anyone noticing.
        var body = await Client(AcmeKey)
            .GetFromJsonAsync<JsonElement>("/api/v1/incidents?status=all&sort=severity");

        var incident = Assert.Single(body.GetProperty("items").EnumerateArray().ToList());
        Assert.Equal(_acmeIncidentId, incident.GetProperty("id").GetGuid());
    }

    // ---------------- Overview aggregations ----------------
    //
    // These endpoints use raw SQL and therefore bypass EF's global query
    // filters entirely. Tenant scoping is hand-written in every statement, so
    // it is verified here rather than assumed.

    [Fact]
    public async Task Overview_is_scoped_to_the_calling_organization()
    {
        var acme = await Client(AcmeKey).GetFromJsonAsync<JsonElement>("/api/v1/overview?windowMinutes=1440");
        var globex = await Client(GlobexKey).GetFromJsonAsync<JsonElement>("/api/v1/overview?windowMinutes=1440");

        Assert.Equal(1, acme.GetProperty("activeIncidents").GetProperty("value").GetDouble());
        Assert.Equal(1, globex.GetProperty("activeIncidents").GetProperty("value").GetDouble());

        // One service each. A missing organization_id predicate would show two.
        Assert.Equal(1, acme.GetProperty("totalServices").GetInt32());
        Assert.Equal(1, globex.GetProperty("totalServices").GetInt32());
    }

    [Fact]
    public async Task Overview_markers_do_not_cross_organizations()
    {
        var acme = await Client(AcmeKey).GetFromJsonAsync<JsonElement>("/api/v1/overview?windowMinutes=1440");
        var services = acme.GetProperty("markers").EnumerateArray()
            .Select(marker => marker.GetProperty("service").GetString())
            .ToList();

        Assert.NotEmpty(services);
        Assert.All(services, service => Assert.Equal("payments-api", service));
    }

    [Fact]
    public async Task Overview_returns_a_gapless_timeline()
    {
        var body = await Client(AcmeKey).GetFromJsonAsync<JsonElement>("/api/v1/overview?windowMinutes=60");

        var points = body.GetProperty("timeline").EnumerateArray().ToList();
        var bucketMinutes = body.GetProperty("bucketMinutes").GetInt32();

        // One-minute buckets over an hour. Quiet periods must be zeros, not
        // missing points - a chart that skips empty buckets compresses time.
        Assert.Equal(1, bucketMinutes);
        Assert.InRange(points.Count, 60, 62);

        var timestamps = points.Select(p => p.GetProperty("bucketStart").GetDateTimeOffset()).ToList();
        for (var i = 1; i < timestamps.Count; i++)
        {
            Assert.Equal(1, (timestamps[i] - timestamps[i - 1]).TotalMinutes);
        }
    }

    [Theory]
    [InlineData(30, 1)]
    [InlineData(360, 5)]
    [InlineData(1440, 15)]
    [InlineData(10080, 60)]
    [InlineData(43200, 360)]
    public async Task Bucket_width_scales_with_the_window(int windowMinutes, int expectedBucket)
    {
        // A fixed width fails at both ends: 43,200 one-minute points cannot be
        // rendered, and one-hour buckets over 15 minutes is a single bar.
        var body = await Client(AcmeKey)
            .GetFromJsonAsync<JsonElement>($"/api/v1/overview?windowMinutes={windowMinutes}");

        Assert.Equal(expectedBucket, body.GetProperty("bucketMinutes").GetInt32());
    }

    [Fact]
    public async Task Change_percent_is_null_rather_than_infinite_when_the_previous_window_was_zero()
    {
        var body = await Client(AcmeKey).GetFromJsonAsync<JsonElement>("/api/v1/overview?windowMinutes=1440");

        // Nothing existed before the seeded data, so every delta divides by
        // zero. Reporting "+100%" or "+Inf%" would be a lie dressed as precision.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("activeIncidents").GetProperty("changePercent").ValueKind);
    }

    [Fact]
    public async Task Overview_without_an_api_key_is_rejected()
    {
        var response = await Client(null).GetAsync("/api/v1/overview");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Service_health_is_scoped_and_derived_from_active_incidents()
    {
        var services = await Client(AcmeKey).GetFromJsonAsync<JsonElement>("/api/v1/services/health");
        var service = Assert.Single(services.EnumerateArray().ToList());

        Assert.Equal("payments-api", service.GetProperty("key").GetString());
        // One active Critical incident, so the service is Critical - the worst
        // active severity drives the badge rather than an average.
        Assert.Equal("Critical", service.GetProperty("health").GetString());
        Assert.Equal(1, service.GetProperty("activeIncidents").GetInt32());
    }

    [Fact]
    public async Task A_service_with_no_active_incidents_is_healthy()
    {
        var services = await Client(GlobexKey).GetFromJsonAsync<JsonElement>("/api/v1/services/health");
        var service = Assert.Single(services.EnumerateArray().ToList());

        // Globex's incident is Medium, so degraded rather than critical.
        Assert.Equal("shipping-api", service.GetProperty("key").GetString());
        Assert.Equal("Degraded", service.GetProperty("health").GetString());
    }

    [Fact]
    public async Task Page_size_is_clamped_so_one_request_cannot_ask_for_everything()
    {
        var body = await Client(AcmeKey).GetFromJsonAsync<JsonElement>("/api/v1/incidents?pageSize=100000");

        Assert.Equal(100, body.GetProperty("pageSize").GetInt32());
    }
}
