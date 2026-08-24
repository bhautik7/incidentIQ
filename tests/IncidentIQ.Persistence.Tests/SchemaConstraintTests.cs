using IncidentIQ.Domain.Entities;
using IncidentIQ.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IncidentIQ.Persistence.Tests;

/// <summary>
/// Constraints that exist so correctness does not depend on application code
/// getting it right under concurrency.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SchemaConstraintTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Only_one_active_incident_can_exist_per_pattern()
    {
        await using var dbContext = fixture.CreateDbContext(SeedIds.Acme.OrganizationId);

        // The seeded pattern already has an Open incident. This models the race
        // where two consumer replicas both decide to open one.
        dbContext.Incidents.Add(NewIncident(SeedIds.Acme.PoolPatternId, IncidentStatus.Open));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(error.InnerException);

        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("ux_incidents_active_pattern", postgres.ConstraintName);
    }

    [Fact]
    public async Task The_same_pattern_can_recur_once_the_previous_incident_is_resolved()
    {
        await using var dbContext = fixture.CreateDbContext(SeedIds.Acme.OrganizationId);

        var first = NewIncident(SeedIds.Acme.TimeoutPatternId, IncidentStatus.Open);
        dbContext.Incidents.Add(first);
        await dbContext.SaveChangesAsync();

        first.Status = IncidentStatus.Resolved;
        first.ResolvedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync();

        // The partial index only covers Open and Acknowledged, so the same
        // problem recurring next week becomes a new incident rather than an error.
        dbContext.Incidents.Add(NewIncident(SeedIds.Acme.TimeoutPatternId, IncidentStatus.Open));
        await dbContext.SaveChangesAsync();

        Assert.Equal(1, await dbContext.Incidents
            .CountAsync(i => i.LogPatternId == SeedIds.Acme.TimeoutPatternId && i.Status == IncidentStatus.Open));
    }

    [Fact]
    public async Task A_redelivered_log_event_is_rejected_by_the_idempotency_key()
    {
        await using var dbContext = fixture.CreateDbContext(SeedIds.Acme.OrganizationId);

        var eventId = Guid.CreateVersion7();
        dbContext.LogEvents.Add(NewLogEvent(eventId));
        await dbContext.SaveChangesAsync();

        // Exactly what a Kafka rebalance or a DLQ replay produces.
        dbContext.LogEvents.Add(NewLogEvent(eventId));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Fact]
    public async Task Two_organizations_may_produce_the_identical_fingerprint()
    {
        var fingerprint = new string('f', 64);

        foreach (var (organizationId, serviceId, environmentId) in new[]
                 {
                     (SeedIds.Acme.OrganizationId, SeedIds.Acme.PaymentsApiId, SeedIds.Acme.ProductionId),
                     (SeedIds.Globex.OrganizationId, SeedIds.Globex.ShippingApiId, SeedIds.Globex.ProductionId)
                 })
        {
            await using var dbContext = fixture.CreateDbContext(organizationId);
            dbContext.LogPatterns.Add(new LogPattern
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = organizationId,
                MonitoredServiceId = serviceId,
                EnvironmentId = environmentId,
                Fingerprint = fingerprint,
                Level = LogEventLevel.Error,
                MessageTemplate = "Everyone gets the same NullReferenceException",
                SampleMessage = "Everyone gets the same NullReferenceException",
                FirstSeenAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow
            });

            // Uniqueness is (organization_id, fingerprint): a common library error
            // must not collide across customers.
            await dbContext.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Deleting_an_incident_detaches_its_samples_without_violating_not_null()
    {
        await using var dbContext = fixture.CreateDbContext(SeedIds.Acme.OrganizationId);

        var incident = NewIncident(Guid.NewGuid(), IncidentStatus.Open);
        incident.LogPatternId = SeedIds.Acme.PoolPatternId;
        incident.Status = IncidentStatus.Ignored; // keeps clear of the active-pattern index
        dbContext.Incidents.Add(incident);
        await dbContext.SaveChangesAsync();

        var sample = NewLogEvent(Guid.CreateVersion7());
        sample.IncidentId = incident.Id;
        dbContext.LogEvents.Add(sample);
        await dbContext.SaveChangesAsync();

        // PostgreSQL's default ON DELETE SET NULL would null organization_id too,
        // which is NOT NULL. The constraint is declared with an explicit column
        // list so only incident_id is cleared.
        await dbContext.Database.ExecuteSqlAsync(
            $"DELETE FROM incidents WHERE id = {incident.Id}");

        var detached = await dbContext.LogEvents.AsNoTracking().SingleAsync(e => e.Id == sample.Id);

        Assert.Null(detached.IncidentId);
        Assert.Equal(SeedIds.Acme.OrganizationId, detached.OrganizationId);
    }

    [Fact]
    public async Task Timestamps_are_stamped_by_the_context_not_the_caller()
    {
        await using var dbContext = fixture.CreateDbContext(SeedIds.Acme.OrganizationId);

        var service = new MonitoredService
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = SeedIds.Acme.OrganizationId,
            Key = $"svc-{Guid.NewGuid():N}"[..20],
            DisplayName = "Ad-hoc service"
        };

        dbContext.MonitoredServices.Add(service);
        await dbContext.SaveChangesAsync();

        Assert.NotEqual(default, service.CreatedAt);
        Assert.Equal(service.CreatedAt, service.UpdatedAt);
        Assert.Equal(TimeSpan.Zero, service.CreatedAt.Offset);

        service.DisplayName = "Renamed";
        await dbContext.SaveChangesAsync();

        Assert.True(service.UpdatedAt > service.CreatedAt);
    }

    private static Incident NewIncident(Guid logPatternId, IncidentStatus status) => new()
    {
        Id = Guid.CreateVersion7(),
        OrganizationId = SeedIds.Acme.OrganizationId,
        MonitoredServiceId = SeedIds.Acme.PaymentsApiId,
        EnvironmentId = SeedIds.Acme.ProductionId,
        LogPatternId = logPatternId,
        Title = "test incident",
        Status = status,
        Severity = IncidentSeverity.Low,
        FirstSeenAt = DateTimeOffset.UtcNow,
        LastSeenAt = DateTimeOffset.UtcNow
    };

    private static LogEvent NewLogEvent(Guid eventId) => new()
    {
        OrganizationId = SeedIds.Acme.OrganizationId,
        EventId = eventId,
        MonitoredServiceId = SeedIds.Acme.PaymentsApiId,
        EnvironmentId = SeedIds.Acme.ProductionId,
        OccurredAt = DateTimeOffset.UtcNow,
        ReceivedAt = DateTimeOffset.UtcNow,
        Level = LogEventLevel.Error,
        Message = "boom"
    };
}
