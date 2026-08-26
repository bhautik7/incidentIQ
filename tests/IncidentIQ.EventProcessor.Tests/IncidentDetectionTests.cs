using IncidentIQ.Contracts;
using IncidentIQ.Contracts.Payloads;
using IncidentIQ.Domain.Entities;
using IncidentIQ.Domain.Enums;
using IncidentIQ.EventProcessor.Detection;
using IncidentIQ.EventProcessor.Processing;
using IncidentIQ.Messaging;
using IncidentIQ.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;
using Environment = IncidentIQ.Domain.Entities.Environment;

namespace IncidentIQ.EventProcessor.Tests;

/// <summary>
/// Detection end to end against real PostgreSQL: minute buckets, the unique
/// index that suppresses duplicates, deployment correlation, tenant isolation
/// and the incident lifecycle.
/// </summary>
public sealed class IncidentDetectionTests : IAsyncLifetime
{
    private static readonly Guid Acme = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Globex = new("22222222-2222-2222-2222-222222222222");

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16")
        .WithDatabase("incidentiq_detection_test")
        .Build();

    private IncidentIQDbContext _dbContext = null!;
    private IncidentDetector _detector = null!;
    private IncidentLifecycleService _lifecycle = null!;
    private FakeTimeProvider _time = null!;
    private DetectionOptions _options = null!;

    private Guid _acmePaymentsId, _acmeProdId, _globexShippingId, _globexProdId, _acmeUserId;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var options = new DbContextOptionsBuilder<IncidentIQDbContext>()
            .UseIncidentIQPostgres(_postgres.GetConnectionString())
            .Options;

        _dbContext = new IncidentIQDbContext(options, new StaticTenantContext(null));
        await _dbContext.Database.MigrateAsync();

        _time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero));
        _options = new DetectionOptions();

        (_acmePaymentsId, _acmeProdId, _acmeUserId) = await SeedTenantAsync(Acme, "acme", "payments-api");
        (_globexShippingId, _globexProdId, _) = await SeedTenantAsync(Globex, "globex", "shipping-api");

        _detector = new IncidentDetector(
            _dbContext,
            new IncidentDetectionStore(_dbContext),
            new OutboxWriter(_dbContext),
            Options.Create(_options),
            _time,
            NullLogger<IncidentDetector>.Instance);

        _lifecycle = new IncidentLifecycleService(_dbContext, _time);
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private async Task<(Guid ServiceId, Guid EnvironmentId, Guid UserId)> SeedTenantAsync(
        Guid tenantId, string slug, string serviceKey)
    {
        var serviceId = Guid.CreateVersion7();
        var environmentId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();

        _dbContext.Organizations.Add(new Organization
        { Id = tenantId, Name = slug, Slug = slug, Status = OrganizationStatus.Active });
        _dbContext.Users.Add(new User
        { Id = userId, OrganizationId = tenantId, Email = $"owner@{slug}.test", DisplayName = "Owner", Status = UserStatus.Active });
        _dbContext.MonitoredServices.Add(new MonitoredService
        { Id = serviceId, OrganizationId = tenantId, Key = serviceKey, DisplayName = serviceKey });
        _dbContext.Environments.Add(new Environment
        { Id = environmentId, OrganizationId = tenantId, Key = "production", DisplayName = "Production", Rank = 100, IsProduction = true });

        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        return (serviceId, environmentId, userId);
    }

    /// <summary>
    /// Creates the log_patterns row the processor would have written, since the
    /// detector consumes events the processor has already persisted.
    /// </summary>
    private async Task<string> SeedPatternAsync(
        Guid tenantId, Guid serviceId, Guid environmentId,
        string fingerprint, DateTimeOffset firstSeen,
        int? httpStatus = null, bool muted = false)
    {
        _dbContext.LogPatterns.Add(new LogPattern
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = tenantId,
            MonitoredServiceId = serviceId,
            EnvironmentId = environmentId,
            Fingerprint = fingerprint,
            Level = LogEventLevel.Error,
            ExceptionType = "System.TimeoutException",
            MessageTemplate = "Connection timeout for user {NUM}",
            SampleMessage = "Connection timeout for user 18273",
            FirstSeenAt = firstSeen,
            LastSeenAt = firstSeen,
            IsMuted = muted,
            HttpStatusCode = httpStatus
        });

        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        return fingerprint;
    }

    private static EventBatchItem<LogNormalized> Event(
        Guid tenantId, string fingerprint, DateTimeOffset at, int? httpStatus = null) =>
        new(EventEnvelope<LogNormalized>.Create(
                EventTypes.LogNormalized,
                tenantId,
                new LogNormalized
                {
                    LogEventId = Guid.CreateVersion7(),
                    Service = "payments-api",
                    Environment = "production",
                    Level = LogSeverity.Error,
                    Fingerprint = fingerprint,
                    MessageTemplate = "Connection timeout for user {NUM}",
                    SampleMessage = "Connection timeout for user 18273",
                    ExceptionType = "System.TimeoutException",
                    Timestamp = at,
                    HttpStatusCode = httpStatus
                }),
            new EventContext(Topics.LogsNormalized, 0, 0, $"{tenantId:D}:payments-api", at, 1));

    /// <summary>
    /// A burst of occurrences spread across the detection window.
    ///
    /// The spread is deliberately bounded: one event per second would push a
    /// 500-event burst over eight minutes, most of it outside the five-minute
    /// window, and the rule would correctly count far fewer than the caller
    /// intended.
    /// </summary>
    private List<EventBatchItem<LogNormalized>> Burst(Guid tenantId, string fingerprint, int count, int? httpStatus = null)
    {
        var spreadSeconds = _options.WindowMinutes * 60 - 30;

        return [.. Enumerable.Range(0, count).Select(i =>
            Event(tenantId, fingerprint, _time.GetUtcNow().AddSeconds(-(i % spreadSeconds)), httpStatus))];
    }

    private Task<List<Incident>> IncidentsAsync(Guid tenantId) =>
        _dbContext.Incidents.AsNoTracking().IgnoreQueryFilters()
            .Where(i => i.OrganizationId == tenantId)
            .ToListAsync();

    // ---------------------------------------------------------------
    // Threshold triggering
    // ---------------------------------------------------------------

    [Fact]
    public async Task Below_the_threshold_nothing_is_opened()
    {
        var fp = await SeedPatternAsync(Acme, _acmePaymentsId, _acmeProdId, new string('a', 64), _time.GetUtcNow().AddDays(-3));

        await _detector.HandleBatchAsync(Burst(Acme, fp, _options.CountThreshold - 1), CancellationToken.None);

        Assert.Empty(await IncidentsAsync(Acme));
    }

    [Fact]
    public async Task Crossing_the_threshold_opens_one_incident_with_an_outbox_message()
    {
        var fp = await SeedPatternAsync(Acme, _acmePaymentsId, _acmeProdId, new string('b', 64), _time.GetUtcNow().AddDays(-3));

        await _detector.HandleBatchAsync(Burst(Acme, fp, _options.CountThreshold), CancellationToken.None);

        var incident = Assert.Single(await IncidentsAsync(Acme));
        Assert.Equal(IncidentStatus.Detected, incident.Status);
        Assert.Equal(DetectionRule.CountThreshold, incident.DetectionRule);
        Assert.Equal($"fp:{fp}", incident.DedupeKey);

        // The incident and both its announcements committed together: one
        // saying it happened, one asking for it to be explained.
        var outbox = await _dbContext.OutboxMessages.AsNoTracking().IgnoreQueryFilters().ToListAsync();

        Assert.Equal(2, outbox.Count);
        Assert.All(outbox, m => Assert.Equal(incident.Id, m.AggregateId));
        Assert.All(outbox, m => Assert.Null(m.PublishedAt));

        var detected = Assert.Single(outbox, m => m.Topic == Topics.IncidentsDetected);
        Assert.Equal(EventTypes.IncidentDetected, detected.EventType);

        var analysis = Assert.Single(outbox, m => m.Topic == Topics.IncidentsAnalysisRequested);
        Assert.Equal(EventTypes.IncidentAnalysisRequested, analysis.EventType);

        // And the timeline records why.
        var timeline = await _dbContext.IncidentEvents.AsNoTracking().IgnoreQueryFilters().ToListAsync();
        Assert.Equal(IncidentEventType.Created, Assert.Single(timeline).Type);
    }

    [Fact]
    public async Task A_muted_pattern_still_counts_but_never_opens_an_incident()
    {
        var fp = await SeedPatternAsync(Acme, _acmePaymentsId, _acmeProdId, new string('c', 64),
            _time.GetUtcNow().AddDays(-3), muted: true);

        await _detector.HandleBatchAsync(Burst(Acme, fp, _options.CountThreshold * 4), CancellationToken.None);

        Assert.Empty(await IncidentsAsync(Acme));

        // Muting suppresses the incident, not the truth about how often it happens.
        var buckets = await _dbContext.LogPatternMetrics.AsNoTracking().IgnoreQueryFilters().ToListAsync();
        Assert.Equal(_options.CountThreshold * 4, buckets.Sum(b => b.Count));
    }

    [Fact]
    public async Task A_server_error_spike_opens_a_service_scoped_incident()
    {
        var fp = await SeedPatternAsync(Acme, _acmePaymentsId, _acmeProdId, new string('d', 64),
            _time.GetUtcNow().AddDays(-3), httpStatus: 503);

        await _detector.HandleBatchAsync(
            Burst(Acme, fp, _options.ServerErrorThreshold, httpStatus: 503), CancellationToken.None);

        var incident = Assert.Single(await IncidentsAsync(Acme));
        Assert.Equal(DetectionRule.ServerErrorSpike, incident.DetectionRule);

        // Scoped to the service, not to any one fingerprint - the spike is
        // about the service being broken.
        Assert.Equal(IncidentDedupeKeys.ForServerErrors(_acmePaymentsId, _acmeProdId), incident.DedupeKey);
        Assert.Null(incident.LogPatternId);
    }

    // ---------------------------------------------------------------
    // Duplicate suppression
    // ---------------------------------------------------------------

    [Fact]
    public async Task A_sustained_error_produces_one_incident_not_hundreds()
    {
        var fp = await SeedPatternAsync(Acme, _acmePaymentsId, _acmeProdId, new string('e', 64), _time.GetUtcNow().AddDays(-3));

        // Ten batches, each on its own past the threshold - exactly what a real
        // outage looks like arriving over several minutes.
        for (var batch = 0; batch < 10; batch++)
        {
            await _detector.HandleBatchAsync(Burst(Acme, fp, _options.CountThreshold), CancellationToken.None);
            _time.Advance(TimeSpan.FromSeconds(30));
        }

        var incident = Assert.Single(await IncidentsAsync(Acme));

        // Later batches folded into the first rather than opening their own.
        Assert.True(incident.OccurrenceCount > _options.CountThreshold,
            $"expected occurrences to accumulate, got {incident.OccurrenceCount}");

        // And only the original detection was announced: two events for the
        // one incident, not two per batch.
        var outbox = await _dbContext.OutboxMessages.AsNoTracking().IgnoreQueryFilters().ToListAsync();

        Assert.Equal(2, outbox.Count);
        Assert.Single(outbox, m => m.Topic == Topics.IncidentsDetected);
        Assert.Single(outbox, m => m.Topic == Topics.IncidentsAnalysisRequested);
    }

    [Fact]
    public async Task Severity_climbs_but_never_falls_while_an_incident_is_open()
    {
        var fp = await SeedPatternAsync(Acme, _acmePaymentsId, _acmeProdId, new string('f', 64), _time.GetUtcNow().AddDays(-3));

        await _detector.HandleBatchAsync(Burst(Acme, fp, _options.CountThreshold * 20), CancellationToken.None);
        Assert.Equal(IncidentSeverity.Critical, (await IncidentsAsync(Acme)).Single().Severity);

        _time.Advance(TimeSpan.FromMinutes(6));
        await _detector.HandleBatchAsync(Burst(Acme, fp, _options.CountThreshold), CancellationToken.None);

        // A quieter minute must not downgrade something that already escalated.
        Assert.Equal(IncidentSeverity.Critical, (await IncidentsAsync(Acme)).Single().Severity);
    }

    // ---------------------------------------------------------------
    // Deployment correlation
    // ---------------------------------------------------------------

    private async Task<Guid> SeedDeploymentAsync(Guid tenantId, Guid serviceId, Guid environmentId, DateTimeOffset at, string version)
    {
        var id = Guid.CreateVersion7();

        _dbContext.Deployments.Add(new Deployment
        {
            Id = id, OrganizationId = tenantId, MonitoredServiceId = serviceId, EnvironmentId = environmentId,
            Version = version, DeployedAt = at, Status = DeploymentStatus.Succeeded
        });

        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        return id;
    }

    [Fact]
    public async Task An_incident_is_correlated_with_the_most_recent_deployment()
    {
        await SeedDeploymentAsync(Acme, _acmePaymentsId, _acmeProdId, _time.GetUtcNow().AddMinutes(-40), "2.30.0");
        var latest = await SeedDeploymentAsync(Acme, _acmePaymentsId, _acmeProdId, _time.GetUtcNow().AddMinutes(-4), "2.31.0");

        var fp = await SeedPatternAsync(Acme, _acmePaymentsId, _acmeProdId, new string('1', 64), _time.GetUtcNow().AddDays(-3));

        await _detector.HandleBatchAsync(Burst(Acme, fp, _options.CountThreshold), CancellationToken.None);

        var incident = Assert.Single(await IncidentsAsync(Acme));
        Assert.Equal(latest, incident.SuspectedDeploymentId);
    }

    [Fact]
    public async Task A_new_error_just_after_a_deployment_opens_below_the_normal_threshold()
    {
        await SeedDeploymentAsync(Acme, _acmePaymentsId, _acmeProdId, _time.GetUtcNow().AddMinutes(-3), "2.31.0");

        // First seen inside the detection window: genuinely novel.
        var fp = await SeedPatternAsync(Acme, _acmePaymentsId, _acmeProdId, new string('2', 64), _time.GetUtcNow().AddMinutes(-2));

        await _detector.HandleBatchAsync(
            Burst(Acme, fp, _options.PostDeploymentCountThreshold), CancellationToken.None);

        var incident = Assert.Single(await IncidentsAsync(Acme));
        Assert.Equal(DetectionRule.NewErrorAfterDeployment, incident.DetectionRule);
        Assert.Equal(IncidentSeverity.Critical, incident.Severity);
        Assert.NotNull(incident.SuspectedDeploymentId);
    }

    [Fact]
    public async Task A_deployment_outside_the_correlation_window_is_not_blamed()
    {
        await SeedDeploymentAsync(Acme, _acmePaymentsId, _acmeProdId, _time.GetUtcNow().AddHours(-8), "2.20.0");

        var fp = await SeedPatternAsync(Acme, _acmePaymentsId, _acmeProdId, new string('3', 64), _time.GetUtcNow().AddDays(-3));

        await _detector.HandleBatchAsync(Burst(Acme, fp, _options.CountThreshold), CancellationToken.None);

        // A stale suspect is worse than none: it sends the investigation the
        // wrong way with false confidence.
        Assert.Null((await IncidentsAsync(Acme)).Single().SuspectedDeploymentId);
    }

    [Fact]
    public async Task A_deployment_of_a_different_service_is_not_blamed()
    {
        var otherService = Guid.CreateVersion7();
        _dbContext.MonitoredServices.Add(new MonitoredService
        { Id = otherService, OrganizationId = Acme, Key = "orders-api", DisplayName = "Orders API" });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        await SeedDeploymentAsync(Acme, otherService, _acmeProdId, _time.GetUtcNow().AddMinutes(-3), "9.9.9");

        var fp = await SeedPatternAsync(Acme, _acmePaymentsId, _acmeProdId, new string('4', 64), _time.GetUtcNow().AddDays(-3));
        await _detector.HandleBatchAsync(Burst(Acme, fp, _options.CountThreshold), CancellationToken.None);

        Assert.Null((await IncidentsAsync(Acme)).Single().SuspectedDeploymentId);
    }

    // ---------------------------------------------------------------
    // Multiple tenants
    // ---------------------------------------------------------------

    [Fact]
    public async Task Identical_errors_in_two_organizations_produce_two_separate_incidents()
    {
        // Same fingerprint string in both tenants. Nothing may be shared.
        const string shared = "5555555555555555555555555555555555555555555555555555555555555555";

        await SeedPatternAsync(Acme, _acmePaymentsId, _acmeProdId, shared, _time.GetUtcNow().AddDays(-3));
        await SeedPatternAsync(Globex, _globexShippingId, _globexProdId, shared, _time.GetUtcNow().AddDays(-3));

        await _detector.HandleBatchAsync(Burst(Acme, shared, _options.CountThreshold), CancellationToken.None);
        await _detector.HandleBatchAsync(Burst(Globex, shared, _options.CountThreshold), CancellationToken.None);

        var acme = Assert.Single(await IncidentsAsync(Acme));
        var globex = Assert.Single(await IncidentsAsync(Globex));

        Assert.NotEqual(acme.Id, globex.Id);
        Assert.Equal(_acmePaymentsId, acme.MonitoredServiceId);
        Assert.Equal(_globexShippingId, globex.MonitoredServiceId);
    }

    [Fact]
    public async Task One_tenants_volume_does_not_open_an_incident_for_another()
    {
        const string shared = "6666666666666666666666666666666666666666666666666666666666666666";

        await SeedPatternAsync(Acme, _acmePaymentsId, _acmeProdId, shared, _time.GetUtcNow().AddDays(-3));
        await SeedPatternAsync(Globex, _globexShippingId, _globexProdId, shared, _time.GetUtcNow().AddDays(-3));

        // Acme is loud. Globex is quiet. Counting across tenants would page Globex.
        await _detector.HandleBatchAsync(Burst(Acme, shared, _options.CountThreshold * 3), CancellationToken.None);
        await _detector.HandleBatchAsync(Burst(Globex, shared, 2), CancellationToken.None);

        Assert.Single(await IncidentsAsync(Acme));
        Assert.Empty(await IncidentsAsync(Globex));
    }

    [Fact]
    public async Task A_mixed_tenant_batch_is_split_correctly()
    {
        const string fpA = "7777777777777777777777777777777777777777777777777777777777777777";
        const string fpG = "8888888888888888888888888888888888888888888888888888888888888888";

        await SeedPatternAsync(Acme, _acmePaymentsId, _acmeProdId, fpA, _time.GetUtcNow().AddDays(-3));
        await SeedPatternAsync(Globex, _globexShippingId, _globexProdId, fpG, _time.GetUtcNow().AddDays(-3));

        // One Kafka batch carrying both tenants' events.
        List<EventBatchItem<LogNormalized>> mixed =
            [.. Burst(Acme, fpA, _options.CountThreshold), .. Burst(Globex, fpG, _options.CountThreshold)];

        await _detector.HandleBatchAsync(mixed, CancellationToken.None);

        Assert.Single(await IncidentsAsync(Acme));
        Assert.Single(await IncidentsAsync(Globex));
    }

    // ---------------------------------------------------------------
    // Lifecycle and resolution behaviour
    // ---------------------------------------------------------------

    private async Task<Incident> OpenIncidentAsync(string fingerprintSeed)
    {
        var fp = await SeedPatternAsync(Acme, _acmePaymentsId, _acmeProdId,
            fingerprintSeed.PadRight(64, '0'), _time.GetUtcNow().AddDays(-3));

        await _detector.HandleBatchAsync(Burst(Acme, fp, _options.CountThreshold), CancellationToken.None);
        _dbContext.ChangeTracker.Clear();

        return (await IncidentsAsync(Acme)).Single(i => i.DedupeKey == $"fp:{fp}");
    }

    [Fact]
    public async Task An_incident_moves_from_detected_through_investigating_to_resolved()
    {
        var incident = await OpenIncidentAsync("a1");

        await _lifecycle.StartInvestigatingAsync(Acme, incident.Id, _acmeUserId);
        _dbContext.ChangeTracker.Clear();

        var investigating = await _dbContext.Incidents.AsNoTracking().IgnoreQueryFilters().FirstAsync(i => i.Id == incident.Id);
        Assert.Equal(IncidentStatus.Investigating, investigating.Status);
        Assert.Equal(_acmeUserId, investigating.InvestigatingUserId);
        Assert.NotNull(investigating.InvestigationStartedAt);

        await _lifecycle.ResolveAsync(Acme, incident.Id, _acmeUserId, "Reverted the DbContext lifetime change.");
        _dbContext.ChangeTracker.Clear();

        var resolved = await _dbContext.Incidents.AsNoTracking().IgnoreQueryFilters().FirstAsync(i => i.Id == incident.Id);
        Assert.Equal(IncidentStatus.Resolved, resolved.Status);
        Assert.Equal("Reverted the DbContext lifetime change.", resolved.ResolutionNotes);

        // The timeline records how it got here, not just where it ended up.
        var timeline = await _dbContext.IncidentEvents.AsNoTracking().IgnoreQueryFilters()
            .Where(e => e.IncidentId == incident.Id).OrderBy(e => e.Id).ToListAsync();

        Assert.Equal(
            [IncidentEventType.Created, IncidentEventType.InvestigationStarted, IncidentEventType.Resolved],
            timeline.Select(e => e.Type));
    }

    [Fact]
    public async Task Resolving_twice_is_rejected_rather_than_silently_overwriting()
    {
        var incident = await OpenIncidentAsync("a2");
        await _lifecycle.ResolveAsync(Acme, incident.Id, _acmeUserId, "first fix");
        _dbContext.ChangeTracker.Clear();

        // Two people acting on stale UI. Overwriting would lose who fixed it and how.
        await Assert.ThrowsAsync<InvalidIncidentTransitionException>(
            () => _lifecycle.ResolveAsync(Acme, incident.Id, _acmeUserId, "second fix"));
    }

    [Fact]
    public async Task A_resolved_incident_stops_absorbing_occurrences_and_a_recurrence_reopens_it()
    {
        var fp = "b1".PadRight(64, '0');
        await SeedPatternAsync(Acme, _acmePaymentsId, _acmeProdId, fp, _time.GetUtcNow().AddDays(-3));
        await _detector.HandleBatchAsync(Burst(Acme, fp, _options.CountThreshold), CancellationToken.None);
        _dbContext.ChangeTracker.Clear();

        var incident = (await IncidentsAsync(Acme)).Single();
        await _lifecycle.ResolveAsync(Acme, incident.Id, _acmeUserId, "fixed");
        _dbContext.ChangeTracker.Clear();

        // It comes back a few minutes later, inside the cooldown.
        _time.Advance(TimeSpan.FromMinutes(5));
        await _detector.HandleBatchAsync(Burst(Acme, fp, _options.CountThreshold), CancellationToken.None);
        _dbContext.ChangeTracker.Clear();

        // Reopened, not duplicated: a flapping error must not produce a new
        // incident every few minutes.
        var after = Assert.Single(await IncidentsAsync(Acme));
        Assert.Equal(incident.Id, after.Id);
        Assert.Equal(IncidentStatus.Detected, after.Status);
        Assert.Null(after.ResolvedAt);

        var timeline = await _dbContext.IncidentEvents.AsNoTracking().IgnoreQueryFilters()
            .Where(e => e.IncidentId == incident.Id).ToListAsync();
        Assert.Contains(timeline, e => e.Type == IncidentEventType.Reopened);
    }

    [Fact]
    public async Task A_recurrence_after_the_cooldown_opens_a_genuinely_new_incident()
    {
        var fp = "b2".PadRight(64, '0');
        await SeedPatternAsync(Acme, _acmePaymentsId, _acmeProdId, fp, _time.GetUtcNow().AddDays(-3));
        await _detector.HandleBatchAsync(Burst(Acme, fp, _options.CountThreshold), CancellationToken.None);
        _dbContext.ChangeTracker.Clear();

        var first = (await IncidentsAsync(Acme)).Single();
        await _lifecycle.ResolveAsync(Acme, first.Id, _acmeUserId, "fixed");
        _dbContext.ChangeTracker.Clear();

        // Weeks later in effect: the same error recurring is a new problem, and
        // conflating it with the old one would hide that the fix regressed.
        _time.Advance(TimeSpan.FromMinutes(_options.ReopenCooldownMinutes + 10));
        await _detector.HandleBatchAsync(Burst(Acme, fp, _options.CountThreshold), CancellationToken.None);
        _dbContext.ChangeTracker.Clear();

        var all = await IncidentsAsync(Acme);
        Assert.Equal(2, all.Count);
        Assert.Single(all, i => i.Status == IncidentStatus.Resolved);
        Assert.Single(all, i => i.Status == IncidentStatus.Detected);
    }

    [Fact]
    public async Task An_ignored_incident_is_distinct_from_a_resolved_one()
    {
        var incident = await OpenIncidentAsync("c1");

        await _lifecycle.IgnoreAsync(Acme, incident.Id, _acmeUserId, "third-party noise");
        _dbContext.ChangeTracker.Clear();

        var ignored = await _dbContext.Incidents.AsNoTracking().IgnoreQueryFilters().FirstAsync(i => i.Id == incident.Id);

        Assert.Equal(IncidentStatus.Ignored, ignored.Status);

        // Nothing was fixed, so there is no resolution to feed the similarity
        // search later.
        Assert.Null(ignored.ResolvedAt);
        Assert.Null(ignored.ResolutionNotes);
    }

    [Fact]
    public async Task Starting_an_investigation_on_a_resolved_incident_is_rejected()
    {
        var incident = await OpenIncidentAsync("c2");
        await _lifecycle.ResolveAsync(Acme, incident.Id, _acmeUserId, "fixed");
        _dbContext.ChangeTracker.Clear();

        await Assert.ThrowsAsync<InvalidIncidentTransitionException>(
            () => _lifecycle.StartInvestigatingAsync(Acme, incident.Id, _acmeUserId));
    }

    [Fact]
    public async Task A_lifecycle_transition_cannot_reach_another_organizations_incident()
    {
        var incident = await OpenIncidentAsync("c3");

        // Right incident id, wrong tenant. It must be invisible, not merely
        // forbidden.
        await Assert.ThrowsAsync<InvalidIncidentTransitionException>(
            () => _lifecycle.ResolveAsync(Globex, incident.Id, _acmeUserId, "not mine"));
    }
}
