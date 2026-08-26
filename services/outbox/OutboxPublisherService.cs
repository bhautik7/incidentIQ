using IncidentIQ.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Prometheus;

namespace IncidentIQ.Outbox;

/// <summary>
/// Polls the outbox on an interval and drains whatever is due.
///
/// Polling rather than change data capture. CDC removes the poll latency
/// entirely, but costs a Kafka Connect deployment, logical replication slots,
/// and connector state to operate. At incident volume - hundreds of events a
/// day, not millions - a 500 ms poll is the simpler thing that works.
/// </summary>
public sealed class OutboxPublisherService(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> options,
    ILogger<OutboxPublisherService> logger) : BackgroundService
{
    private readonly OutboxOptions _options = options.Value;

    private static readonly Counter Published = Metrics.CreateCounter(
        "incidentiq_outbox_published_total", "Outbox messages published to Kafka.");

    private static readonly Counter Failed = Metrics.CreateCounter(
        "incidentiq_outbox_failed_total", "Outbox publish attempts that failed.");

    private static readonly Counter DeadLettered = Metrics.CreateCounter(
        "incidentiq_outbox_dead_lettered_total", "Outbox messages that exhausted their attempts.");

    private static readonly Gauge Backlog = Metrics.CreateGauge(
        "incidentiq_outbox_pending", "Outbox messages waiting to be published.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Outbox publisher is disabled in this host.");
            return;
        }

        logger.LogInformation(
            "Outbox publisher started. pollMs={Poll} batch={Batch} maxAttempts={MaxAttempts}",
            _options.PollIntervalMs, _options.BatchSize, _options.MaxAttempts);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var drainer = scope.ServiceProvider.GetRequiredService<OutboxDrainer>();

                var result = await drainer.DrainOnceAsync(stoppingToken);

                if (result.Published > 0)
                {
                    Published.Inc(result.Published);
                }

                if (result.Failed > 0)
                {
                    Failed.Inc(result.Failed);
                }

                if (result.DeadLettered > 0)
                {
                    DeadLettered.Inc(result.DeadLettered);

                    // Nothing will retry these. Someone has to look.
                    logger.LogError(
                        "{Count} outbox message(s) exhausted {MaxAttempts} attempts and were dead-lettered.",
                        result.DeadLettered, _options.MaxAttempts);
                }

                if (result.Published > 0 || result.Failed > 0)
                {
                    logger.LogInformation(
                        "Outbox drained. claimed={Claimed} published={Published} failed={Failed} deadLettered={DeadLettered}",
                        result.Claimed, result.Published, result.Failed, result.DeadLettered);
                }

                // A full batch means there is more waiting, so poll again
                // immediately rather than sleeping through a backlog.
                if (result.Claimed >= _options.BatchSize)
                {
                    continue;
                }

                await UpdateBacklogGaugeAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let the loop die. An unavailable database is a
                // temporary condition; a stopped publisher is a silent one.
                logger.LogError(ex, "Outbox drain failed; retrying after the poll interval.");
            }

            try
            {
                await Task.Delay(_options.PollIntervalMs, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Outbox publisher stopped.");
    }

    /// <summary>
    /// Backlog depth is the alert that matters: if it grows without bound,
    /// events are being written faster than they can be delivered.
    /// </summary>
    private static async Task UpdateBacklogGaugeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var dbContext = services.GetRequiredService<IncidentIQDbContext>();

        var pending = await dbContext.OutboxMessages
            .IgnoreQueryFilters()
            .CountAsync(m => m.PublishedAt == null && m.DeadLetteredAt == null, cancellationToken);

        Backlog.Set(pending);
    }
}

/// <summary>
/// Deletes published rows once they are older than the retention window.
///
/// Without this the outbox grows forever. Published rows are kept for a while
/// rather than deleted immediately because "was this event actually sent, and
/// when?" is a question worth being able to answer during an investigation.
/// </summary>
public sealed class OutboxJanitorService(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> options,
    ILogger<OutboxJanitorService> logger) : BackgroundService
{
    private readonly OutboxOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<IncidentIQDbContext>();

                var cutoff = DateTimeOffset.UtcNow.AddHours(-_options.RetentionHours);

                // Dead-lettered rows are deliberately never swept: they are
                // unresolved problems, not completed work.
                var deleted = await dbContext.OutboxMessages
                    .IgnoreQueryFilters()
                    .Where(m => m.PublishedAt != null && m.PublishedAt < cutoff)
                    .ExecuteDeleteAsync(stoppingToken);

                if (deleted > 0)
                {
                    logger.LogInformation("Outbox janitor removed {Count} published message(s).", deleted);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox janitor pass failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(_options.JanitorIntervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

public static class OutboxServiceCollectionExtensions
{
    public static IServiceCollection AddIncidentIQOutbox(
        this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.SectionName));

        services.AddScoped<IOutboxWriter, OutboxWriter>();
        services.AddScoped<OutboxDrainer>();
        services.TryAddSingletonTimeProvider();

        services.AddHostedService<OutboxPublisherService>();
        services.AddHostedService<OutboxJanitorService>();

        return services;
    }

    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (services.All(d => d.ServiceType != typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }
}
