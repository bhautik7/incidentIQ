using IncidentIQ.Domain.Entities;
using IncidentIQ.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IncidentIQ.Persistence.Tests;

/// <summary>
/// The guarantee the whole schema is built around: one organization's data is
/// unreachable from another, whether or not the application remembers to ask.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TenantIsolationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Queries_return_only_the_current_organizations_rows()
    {
        await using var acme = fixture.CreateDbContext(SeedIds.Acme.OrganizationId);

        var incidents = await acme.Incidents.ToListAsync();
        var services = await acme.MonitoredServices.Select(s => s.Key).ToListAsync();

        Assert.All(incidents, i => Assert.Equal(SeedIds.Acme.OrganizationId, i.OrganizationId));
        Assert.Contains("payments-api", services);
        Assert.DoesNotContain("shipping-api", services);
    }

    [Fact]
    public async Task A_direct_lookup_by_another_organizations_primary_key_finds_nothing()
    {
        await using var acme = fixture.CreateDbContext(SeedIds.Acme.OrganizationId);

        // The id is real and correct - it simply belongs to Globex. Guessing or
        // leaking an id must not be enough to read the row.
        var stolen = await acme.Incidents.FirstOrDefaultAsync(i => i.Id == SeedIds.Globex.IncidentId);

        Assert.Null(stolen);
    }

    [Fact]
    public async Task A_context_with_no_organization_sees_nothing()
    {
        await using var anonymous = fixture.CreateDbContext(null);

        // Fail closed: the filter becomes "organization_id = NULL", which matches
        // no rows. A misrouted request returns an empty result, never a leak.
        Assert.Empty(await anonymous.Incidents.ToListAsync());
        Assert.Empty(await anonymous.MonitoredServices.ToListAsync());
    }

    [Fact]
    public async Task The_database_rejects_an_incident_that_points_at_another_organizations_service()
    {
        await using var acme = fixture.CreateDbContext(SeedIds.Acme.OrganizationId);

        // A bug in application code, deliberately written: an Acme incident that
        // references Globex's service. The composite foreign key
        // (organization_id, monitored_service_id) has no matching pair, so
        // PostgreSQL refuses it. Nothing in C# had to remember to check.
        acme.Incidents.Add(new Incident
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = SeedIds.Acme.OrganizationId,
            MonitoredServiceId = SeedIds.Globex.ShippingApiId,
            EnvironmentId = SeedIds.Acme.ProductionId,
            LogPatternId = SeedIds.Acme.TimeoutPatternId,
            Title = "cross-tenant reference that must not be possible",
            Status = IncidentStatus.Open,
            Severity = IncidentSeverity.Low,
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow
        });

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => acme.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(error.InnerException);

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgres.SqlState);
    }

    [Fact]
    public async Task The_same_email_can_exist_in_two_organizations()
    {
        var email = $"shared-{Guid.NewGuid():N}@example.test";

        foreach (var organizationId in new[] { SeedIds.Acme.OrganizationId, SeedIds.Globex.OrganizationId })
        {
            await using var dbContext = fixture.CreateDbContext(organizationId);
            dbContext.Users.Add(new User
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = organizationId,
                Email = email,
                DisplayName = "Consultant",
                Status = UserStatus.Active
            });

            // Uniqueness is (organization_id, email), so this succeeds twice.
            await dbContext.SaveChangesAsync();
        }

        await using var verify = fixture.CreateDbContext(SeedIds.Acme.OrganizationId);
        Assert.Equal(1, await verify.Users.CountAsync(u => u.Email == email));
    }
}
