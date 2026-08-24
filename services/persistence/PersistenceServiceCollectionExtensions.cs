using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IncidentIQ.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Single place where the provider is configured. Design-time migration
    /// generation goes through the same method, so the migrations that get
    /// generated always match the model the application actually runs.
    /// </summary>
    public static DbContextOptionsBuilder UseIncidentIQPostgres(
        this DbContextOptionsBuilder builder,
        string connectionString)
    {
        return builder
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.UseVector();
                // Transient faults are expected: PostgreSQL restarts, failovers,
                // and the brief unavailability during a deployment.
                npgsql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null);
                npgsql.MigrationsHistoryTable("__ef_migrations_history");
            })
            // PostgreSQL folds unquoted identifiers to lower case, so PascalCase
            // names would have to be quoted in every hand-written query. This
            // database gets queried by hand constantly during triage.
            .UseSnakeCaseNamingConvention();
    }

    /// <summary>
    /// Generic overload so a typed options builder keeps its type, which is what
    /// <see cref="DesignTimeDbContextFactory"/> needs to construct the context
    /// directly.
    /// </summary>
    public static DbContextOptionsBuilder<TContext> UseIncidentIQPostgres<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        string connectionString)
        where TContext : DbContext
    {
        UseIncidentIQPostgres((DbContextOptionsBuilder)builder, connectionString);
        return builder;
    }

    public static IServiceCollection AddIncidentIQPersistence(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddScoped<AmbientTenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<AmbientTenantContext>());

        services.AddDbContext<IncidentIQDbContext>(options => options.UseIncidentIQPostgres(connectionString));

        return services;
    }
}
