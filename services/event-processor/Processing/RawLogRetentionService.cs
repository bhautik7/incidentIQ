using IncidentIQ.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace IncidentIQ.EventProcessor.Processing;

public sealed class RawLogRetentionOptions
{
    public const string SectionName = "RawLogRetention";

    /// <summary>
    /// How far back the log explorer can see.
    ///
    /// This is the one number that decides how large the raw table gets, and it
    /// is short on purpose: the explorer answers "what happened during this
    /// outage", which is a question about hours, not months. Anything older is
    /// served by patterns and incidents, which are orders of magnitude smaller
    /// and are kept indefinitely.
    /// </summary>
    public int RetentionHours { get; set; } = 48;

    /// <summary>How often the sweep runs. Frequent enough that the table never carries much more than the window.</summary>
    public int SweepIntervalMinutes { get; set; } = 30;

    /// <summary>
    /// Rows deleted per statement.
    ///
    /// The delete is chunked rather than issued as one statement because a
    /// single unbounded DELETE on a busy table takes a long-lived lock and
    /// builds a transaction big enough to stall autovacuum. Chunking keeps each
    /// statement short and lets ingestion continue between them.
    /// </summary>
    public int DeleteBatchSize { get; set; } = 10_000;
}

/// <summary>
/// Keeps the raw log window bounded.
///
/// Without this the table grows forever, and it is the only table in the schema
/// that grows with traffic rather than with the number of distinct errors - the
/// exact thing ADR 0007 avoided by sampling. The window is what makes storing
/// every line affordable, so the sweep is part of the design rather than
/// housekeeping bolted on afterwards.
/// </summary>
public sealed class RawLogRetentionService(
    IServiceScopeFactory scopeFactory,
    IOptions<RawLogRetentionOptions> options,
    TimeProvider timeProvider,
    ILogger<RawLogRetentionService> logger) : BackgroundService
{
    private readonly RawLogRetentionOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.SweepIntervalMinutes));

        logger.LogInformation(
            "Raw log retention: keeping {Hours}h, sweeping every {Interval}.",
            _options.RetentionHours, interval);

        // A first sweep on startup, so a container that was down over a long
        // weekend does not wait out the interval before trimming.
        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                // Migrations are applied by the API, so on a fresh environment
                // this worker can reach its first sweep before the table exists.
                // That is startup ordering, not a fault, and logging a stack
                // trace for it trains people to ignore this service's errors.
                logger.LogInformation(
                    "Raw log retention is waiting for the raw_log_events table to be created.");
            }
            catch (Exception ex)
            {
                // Anything else: retention failing is not worth stopping the
                // worker over. The table grows, which is a disk problem rather
                // than a correctness one, and the next sweep may well succeed.
                logger.LogError(ex, "Raw log retention sweep failed; will retry next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow().AddHours(-Math.Max(1, _options.RetentionHours));

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IncidentIQDbContext>();

        var total = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            // Deliberately not tenant-scoped: retention is an operational
            // property of the table, not a per-organization query, and the
            // global filter would otherwise silently limit it to nothing when
            // no tenant is on the ambient context.
            var deleted = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 DELETE FROM raw_log_events
                 WHERE id IN (
                     SELECT id FROM raw_log_events
                     WHERE occurred_at < {cutoff}
                     LIMIT {_options.DeleteBatchSize}
                 )
                 """,
                cancellationToken);

            total += deleted;

            if (deleted < _options.DeleteBatchSize)
            {
                break;
            }
        }

        if (total > 0)
        {
            logger.LogInformation(
                "Raw log retention removed {Count} row(s) older than {Cutoff:o}.", total, cutoff);
        }
    }
}
