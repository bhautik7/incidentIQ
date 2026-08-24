using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IncidentIQ.Persistence;

public sealed class DatabaseBootstrapOptions
{
    /// <summary>
    /// Applying migrations from the application is convenient locally and a
    /// liability in production, where two replicas starting at once race and a
    /// failed migration takes the app down with it. Deployed environments should
    /// run migrations as a separate, gated step.
    /// </summary>
    public bool RunMigrationsOnStartup { get; set; }

    /// <summary>Platform roles. Safe everywhere.</summary>
    public bool SeedReferenceData { get; set; }

    /// <summary>Sample organizations. Development only.</summary>
    public bool SeedDevelopmentData { get; set; }
}

/// <summary>
/// Brings the database to a usable state at startup, according to configuration.
/// Every step is off by default and idempotent.
/// </summary>
public sealed class DatabaseBootstrapHostedService(
    IServiceScopeFactory scopeFactory,
    DatabaseBootstrapOptions options,
    ILogger<DatabaseBootstrapHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.RunMigrationsOnStartup && !options.SeedReferenceData && !options.SeedDevelopmentData)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IncidentIQDbContext>();

        if (options.RunMigrationsOnStartup)
        {
            logger.LogInformation("Applying database migrations...");
            await dbContext.Database.MigrateAsync(cancellationToken);

            var applied = await dbContext.Database.GetAppliedMigrationsAsync(cancellationToken);
            logger.LogInformation("Database is at migration {Migration}.", applied.LastOrDefault() ?? "(none)");
        }

        var seeder = new DatabaseSeeder(
            dbContext,
            scope.ServiceProvider.GetRequiredService<ILogger<DatabaseSeeder>>());

        if (options.SeedReferenceData)
        {
            await seeder.SeedReferenceDataAsync(cancellationToken);
        }

        if (options.SeedDevelopmentData)
        {
            await seeder.SeedDevelopmentDataAsync(cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class DatabaseBootstrapExtensions
{
    public static IServiceCollection AddIncidentIQDatabaseBootstrap(
        this IServiceCollection services,
        Action<DatabaseBootstrapOptions> configure)
    {
        var options = new DatabaseBootstrapOptions();
        configure(options);

        services.AddSingleton(options);
        services.AddHostedService<DatabaseBootstrapHostedService>();

        return services;
    }
}
