using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IncidentIQ.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c>. It never connects unless a command needs a
/// database, so migrations can be generated without any infrastructure running.
///
/// The connection string comes from INCIDENTIQ_MIGRATIONS_CONNECTION, falling
/// back to the local Docker Compose PostgreSQL. No password is embedded here -
/// the fallback carries the documented local development value only.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<IncidentIQDbContext>
{
    private const string DefaultLocalConnection =
        "Host=localhost;Port=5433;Database=incidentiq;Username=incidentiq;Password=dev_only_change_me";

    public IncidentIQDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            System.Environment.GetEnvironmentVariable("INCIDENTIQ_MIGRATIONS_CONNECTION")
            ?? DefaultLocalConnection;

        var options = new DbContextOptionsBuilder<IncidentIQDbContext>()
            .UseIncidentIQPostgres(connectionString)
            .Options;

        // Design time has no request and therefore no tenant.
        return new IncidentIQDbContext(options, new StaticTenantContext(null));
    }
}
