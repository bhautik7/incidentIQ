using IncidentIQ.Domain.Entities;
using IncidentIQ.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace IncidentIQ.Persistence.Tests;

/// <summary>
/// The queries the product actually runs, checked against a real database so
/// that the indexes designed for them are the indexes they can use.
/// </summary>
[Collection(PostgresCollection.Name)]
public class QueryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_dashboard_query_returns_active_incidents_newest_first()
    {
        await using var dbContext = fixture.CreateDbContext(SeedIds.Acme.OrganizationId);

        // Matches ix_incidents_organization_id_status_last_seen_at.
        var board = await dbContext.Incidents
            .AsNoTracking()
            .Where(i => i.Status == IncidentStatus.Detected)
            .OrderByDescending(i => i.LastSeenAt)
            .Select(i => new
            {
                i.Id,
                i.Title,
                i.Severity,
                i.OccurrenceCount,
                i.LastSeenAt,
                Service = i.MonitoredService.Key,
                Environment = i.Environment.Key,
                SuspectedVersion = i.SuspectedDeployment != null ? i.SuspectedDeployment.Version : null
            })
            .ToListAsync();

        // Other tests in this collection share the database, so assert on the
        // ordering contract and on the seeded row - not on the row count.
        Assert.NotEmpty(board);
        Assert.Equal(board.OrderByDescending(i => i.LastSeenAt).Select(i => i.Id), board.Select(i => i.Id));

        var incident = Assert.Single(board, i => i.Id == SeedIds.Acme.IncidentId);
        Assert.Equal("payments-api", incident.Service);
        Assert.Equal("production", incident.Environment);
        Assert.Equal(IncidentSeverity.Critical, incident.Severity);
        // The count comes from the pattern's counter, not from rows in log_events.
        Assert.Equal(4200, incident.OccurrenceCount);
        Assert.Equal("2.31.0", incident.SuspectedVersion);
    }

    [Fact]
    public async Task Occurrence_counts_come_from_the_pattern_not_from_stored_rows()
    {
        await using var dbContext = fixture.CreateDbContext(SeedIds.Acme.OrganizationId);

        var pattern = await dbContext.LogPatterns.AsNoTracking()
            .SingleAsync(p => p.Id == SeedIds.Acme.PoolPatternId);

        var storedRows = await dbContext.LogEvents
            .CountAsync(e => e.LogPatternId == SeedIds.Acme.PoolPatternId);

        // This gap is the sampling policy working as designed: 4,200 occurrences
        // are represented by a counter plus a few sample rows, not 4,200 rows.
        Assert.Equal(4200, pattern.OccurrenceCount);
        Assert.Equal(3, storedRows);
    }

    [Fact]
    public async Task The_incident_timeline_reads_oldest_first()
    {
        await using var dbContext = fixture.CreateDbContext(SeedIds.Acme.OrganizationId);

        var timeline = await dbContext.IncidentEvents
            .AsNoTracking()
            .Where(e => e.IncidentId == SeedIds.Acme.IncidentId)
            .OrderBy(e => e.OccurredAt)
            .Select(e => e.Type)
            .ToListAsync();

        Assert.Equal([IncidentEventType.Created, IncidentEventType.Escalated], timeline);
    }

    [Fact]
    public async Task The_deployment_that_preceded_an_incident_can_be_found()
    {
        await using var dbContext = fixture.CreateDbContext(SeedIds.Acme.OrganizationId);

        var incident = await dbContext.Incidents.AsNoTracking()
            .SingleAsync(i => i.Id == SeedIds.Acme.IncidentId);

        // The correlation the product leans on: latest deployment to this
        // service and environment at or before the incident started.
        var culprit = await dbContext.Deployments
            .AsNoTracking()
            .Where(d => d.MonitoredServiceId == incident.MonitoredServiceId
                        && d.EnvironmentId == incident.EnvironmentId
                        && d.DeployedAt <= incident.FirstSeenAt)
            .OrderByDescending(d => d.DeployedAt)
            .FirstOrDefaultAsync();

        Assert.NotNull(culprit);
        Assert.Equal("2.31.0", culprit.Version);
        Assert.True(incident.FirstSeenAt - culprit.DeployedAt < TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task Jsonb_properties_survive_a_round_trip_and_are_queryable()
    {
        await using var dbContext = fixture.CreateDbContext(SeedIds.Acme.OrganizationId);

        var sample = await dbContext.LogEvents.AsNoTracking()
            .Where(e => e.LogPatternId == SeedIds.Acme.PoolPatternId)
            .OrderBy(e => e.Id)
            .FirstAsync();

        Assert.Contains("\"deploymentVersion\": \"2.31.0\"", sample.Properties);

        // jsonb is queryable server-side, which is what makes it a reasonable
        // home for fields we cannot know in advance.
        var matched = await dbContext.Database
            .SqlQuery<long>($"""
                SELECT count(*)::bigint AS "Value" FROM log_events
                WHERE organization_id = {SeedIds.Acme.OrganizationId}
                  AND properties ->> 'deploymentVersion' = '2.31.0'
                """)
            .SingleAsync();

        Assert.Equal(3, matched);
    }

    [Fact]
    public async Task The_outbox_pending_query_sees_only_unpublished_rows()
    {
        await using var dbContext = fixture.CreateDbContext(SeedIds.Acme.OrganizationId);

        dbContext.OutboxMessages.AddRange(
            NewOutboxMessage(publishedAt: null),
            NewOutboxMessage(publishedAt: DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync();

        // Matches ix_outbox_messages_pending, whose partial predicate keeps the
        // index the size of the backlog rather than the size of history.
        var pending = await dbContext.OutboxMessages
            .AsNoTracking()
            .Where(m => m.PublishedAt == null)
            .OrderBy(m => m.Id)
            .ToListAsync();

        Assert.All(pending, m => Assert.Null(m.PublishedAt));
        Assert.Single(pending);
    }

    [Fact]
    public async Task Similar_incidents_can_be_found_by_vector_distance()
    {
        await using var dbContext = fixture.CreateDbContext(SeedIds.Acme.OrganizationId);

        var target = Embedding(1.0f, 0.0f);
        var near = Embedding(0.99f, 0.14f);
        var far = Embedding(0.0f, 1.0f);

        var resolved = await SeedResolvedIncidentWithEmbedding(dbContext, "pool exhaustion, fixed by DI revert", near);
        var unrelated = await SeedResolvedIncidentWithEmbedding(dbContext, "disk full on log volume", far);

        // The query that justifies pgvector: nearest neighbours, filtered
        // relationally in the same statement - one round trip, one index.
        var ranked = await dbContext.AiAnalyses
            .AsNoTracking()
            .Where(a => a.Embedding != null && a.Incident.Status == IncidentStatus.Resolved)
            .OrderBy(a => a.Embedding!.CosineDistance(target))
            .Select(a => a.Incident.Title)
            .Take(2)
            .ToListAsync();

        Assert.Equal(resolved, ranked[0]);
        Assert.Equal(unrelated, ranked[1]);
    }

    private static async Task<string> SeedResolvedIncidentWithEmbedding(
        IncidentIQDbContext dbContext, string title, Vector embedding)
    {
        var incident = new Incident
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = SeedIds.Acme.OrganizationId,
            MonitoredServiceId = SeedIds.Acme.PaymentsApiId,
            EnvironmentId = SeedIds.Acme.ProductionId,
            LogPatternId = SeedIds.Acme.PoolPatternId,
            DedupeKey = $"fp:{SeedIds.Acme.PoolPatternId}",
            Title = title,
            Status = IncidentStatus.Resolved,
            Severity = IncidentSeverity.High,
            FirstSeenAt = DateTimeOffset.UtcNow.AddDays(-90),
            LastSeenAt = DateTimeOffset.UtcNow.AddDays(-90),
            ResolvedAt = DateTimeOffset.UtcNow.AddDays(-90),
            ResolutionNotes = "Reverted the DI lifetime change."
        };

        dbContext.Incidents.Add(incident);
        dbContext.AiAnalyses.Add(new AiAnalysis
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = SeedIds.Acme.OrganizationId,
            IncidentId = incident.Id,
            AnalysisVersion = 1,
            Status = AiAnalysisStatus.Completed,
            Embedding = embedding
        });

        await dbContext.SaveChangesAsync();
        return title;
    }

    /// <summary>A 1536-dimension vector with only the first two components set.</summary>
    private static Vector Embedding(float x, float y)
    {
        var values = new float[1536];
        values[0] = x;
        values[1] = y;
        return new Vector(values);
    }

    private static OutboxMessage NewOutboxMessage(DateTimeOffset? publishedAt) => new()
    {
        OrganizationId = SeedIds.Acme.OrganizationId,
        EventId = Guid.CreateVersion7(),
        CorrelationId = Guid.CreateVersion7(),
        AggregateType = "Incident",
        AggregateId = SeedIds.Acme.IncidentId,
        EventType = "incident.detected",
        Topic = "incidents.detected",
        PartitionKey = $"{SeedIds.Acme.OrganizationId:D}:{SeedIds.Acme.IncidentId:D}",
        Payload = """{"incidentId":"11111111-0000-0000-0000-0000000000e1"}""",
        OccurredAt = DateTimeOffset.UtcNow,
        PublishedAt = publishedAt
    };
}
