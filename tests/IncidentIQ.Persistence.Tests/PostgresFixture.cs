using IncidentIQ.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace IncidentIQ.Persistence.Tests;

/// <summary>
/// A real PostgreSQL for the whole test class run.
///
/// Deliberately not an in-memory provider: every behaviour worth testing here -
/// partial unique indexes, composite foreign keys, ON DELETE SET NULL on a
/// subset of columns, pgvector distance operators - exists only in PostgreSQL.
/// An in-memory provider would pass these tests while the real database failed.
///
/// The image is pgvector's, so the `vector` extension the migration creates is
/// actually available.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg16")
        .WithDatabase("incidentiq_test")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Applying the real migrations, not EnsureCreated: this is also the test
        // that the migration chain runs cleanly against an empty database.
        await using var dbContext = CreateDbContext(null);
        await dbContext.Database.MigrateAsync();

        var seeder = new DatabaseSeeder(dbContext, NullLogger<DatabaseSeeder>.Instance);
        await seeder.SeedReferenceDataAsync();
        await seeder.SeedDevelopmentDataAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>
    /// A context scoped to one organization, or to none. Passing null models an
    /// unauthenticated caller, whose queries must return nothing.
    /// </summary>
    public IncidentIQDbContext CreateDbContext(Guid? organizationId)
    {
        var options = new DbContextOptionsBuilder<IncidentIQDbContext>()
            .UseIncidentIQPostgres(ConnectionString)
            .Options;

        return new IncidentIQDbContext(options, new StaticTenantContext(organizationId));
    }
}

[CollectionDefinition(Name)]
public class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
