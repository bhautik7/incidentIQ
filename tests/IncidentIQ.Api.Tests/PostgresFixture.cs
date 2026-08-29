using IncidentIQ.Persistence;
using IncidentIQ.Shared.Auth;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IncidentIQ.Api.Tests;

/// <summary>
/// One PostgreSQL container for the whole test assembly, and a fresh database
/// inside it for every test.
///
/// Each test class used to start its own container, and xUnit constructs a new
/// class instance per test - so a suite of 67 tests started 67 containers,
/// migrated 67 schemas, and took anywhere from ninety seconds to half an hour
/// depending on what else the machine was doing. A suite that might take half
/// an hour is a suite that stops being run before pushing, and tests nobody
/// runs are worse than no tests: they carry the authority of a green tick
/// nobody has earned.
///
/// The container starts once and migrations run once, into a template. Every
/// test then gets its own database created from that template, which Postgres
/// does by copying files rather than replaying schema - fast enough to keep the
/// isolation that made a container per test attractive in the first place.
///
/// That isolation is not a nicety here. These tests assert on tenant
/// boundaries, on counts of rows in a whole table, and on an incident being
/// opened for the first time; sharing one database between them would make
/// each test's correctness depend on which tests ran before it.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>
    /// The migrated database every test's own database is copied from.
    ///
    /// Never connected to after migration: Postgres refuses to copy a template
    /// while anything is attached to it.
    /// </summary>
    private const string TemplateDatabase = "incidentiq_template";

    /// <summary>Connected to only in order to issue CREATE DATABASE.</summary>
    private const string MaintenanceDatabase = "postgres";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16")
        .WithDatabase(TemplateDatabase)
        .Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var options = new DbContextOptionsBuilder<IncidentIQDbContext>()
            .UseIncidentIQPostgres(ConnectionStringFor(TemplateDatabase))
            .Options;

        await using (var db = new IncidentIQDbContext(options, new StaticTenantContext(null)))
        {
            await db.Database.MigrateAsync();
        }

        // Postgres refuses to copy a database anything is connected to, and
        // "anything" includes connections nobody in this file opened: EF's
        // pooled connection survives disposal, and the container's own
        // readiness probing reconnects on its own schedule. Clearing the pool
        // was tried first and was not enough - the suite passed 21 tests and
        // then failed 58 with "source database is being accessed by other
        // users", because something reconnected partway through.
        //
        // So the template is closed to connections instead of merely left
        // alone. Copying it does not require one, and a database that refuses
        // connections cannot acquire a straggler between two tests.
        NpgsqlConnection.ClearAllPools();

        await ExecuteOnMaintenanceDatabaseAsync(
            $"""ALTER DATABASE "{TemplateDatabase}" ALLOW_CONNECTIONS false;""");

        // Anything already attached is unaffected by the ALTER, so the
        // survivors are evicted explicitly.
        await ExecuteOnMaintenanceDatabaseAsync(
            $"""
            SELECT pg_terminate_backend(pid) FROM pg_stat_activity
            WHERE datname = '{TemplateDatabase}' AND pid <> pg_backend_pid();
            """);
    }

    private async Task ExecuteOnMaintenanceDatabaseAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionStringFor(MaintenanceDatabase));
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    /// <summary>
    /// A database of this test's own, already migrated.
    ///
    /// Not dropped afterwards. They live inside a container that is thrown away
    /// when the assembly finishes, and dropping each one would add a round trip
    /// per test to reclaim disk nobody is short of.
    /// </summary>
    public async Task<string> CreateDatabaseAsync()
    {
        // Prefixed with a letter and unquoted-identifier-safe: a bare GUID
        // starts with a digit often enough to break a CREATE DATABASE.
        var name = $"t{Guid.NewGuid():N}";

        // Not parameterisable - CREATE DATABASE takes an identifier, not a
        // value - so the name is generated here rather than accepted from
        // anywhere a caller could reach.
        await ExecuteOnMaintenanceDatabaseAsync(
            $"""CREATE DATABASE "{name}" TEMPLATE "{TemplateDatabase}";""");

        return ConnectionStringFor(name);
    }

    /// <summary>
    /// A connection string for one database, with a pool that gives its
    /// connections back.
    ///
    /// Npgsql pools per connection string, and every test here has a different
    /// one, so the defaults leave 79 pools each holding idle connections open
    /// for the rest of the run - which exhausted PostgreSQL's 100-connection
    /// limit around test 62 and failed the remaining 17 with "sorry, too many
    /// clients already". The pool is capped and pruned aggressively instead:
    /// these tests are sequential and short, so a large warm pool buys nothing
    /// and costs the whole suite.
    /// </summary>
    private string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            Database = database,
            MaxPoolSize = 4,
            ConnectionIdleLifetime = 2,
            ConnectionPruningInterval = 1
        }.ConnectionString;
}

/// <summary>
/// Binds every API test class to the one container.
///
/// A collection also serialises the classes in it, which is the trade being
/// made: three containers running in parallel become one container running
/// tests one after another, and the tests are now short enough that this is
/// still far faster.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
