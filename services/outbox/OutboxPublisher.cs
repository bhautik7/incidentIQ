using System.Text;
using System.Text.Json;
using IncidentIQ.Contracts;
using IncidentIQ.Messaging;
using IncidentIQ.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace IncidentIQ.Outbox;

public sealed record OutboxDrainResult(int Claimed, int Published, int Failed, int DeadLettered);

/// <summary>
/// Drains the outbox: claim pending rows, publish them, mark them published.
///
/// <b>Concurrency.</b> Rows are claimed with <c>FOR UPDATE SKIP LOCKED</c>, so
/// any number of replicas can drain the table at once without leader election,
/// a distributed lock, or a coordination service. A replica simply steps over
/// rows another replica is already holding.
///
/// <b>Ordering.</b> Claims are ordered by id, which is creation order. With one
/// publisher that gives per-aggregate ordering for free. With several, two
/// events for the same aggregate can be claimed by different replicas and reach
/// Kafka out of order - see the limitations in the class remarks below.
///
/// <b>Why the publish happens inside the transaction.</b> The alternative is to
/// claim and commit, then publish, then mark - which trades a held lock for a
/// window where a crash leaves a row claimed but unpublished and invisible.
/// Holding the transaction keeps the state machine to two states instead of
/// three; the cost is a lock held for the duration of a batch publish, bounded
/// by BatchSize and by the producer's message timeout.
/// </summary>
public sealed class OutboxDrainer(
    IncidentIQDbContext dbContext,
    IEventProducer producer,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxDrainer> logger)
{
    private readonly OutboxOptions _options = options.Value;

    public async Task<OutboxDrainResult> DrainOnceAsync(CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var claimed = await ClaimAsync(connection, transaction, cancellationToken);

        if (claimed.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return new OutboxDrainResult(0, 0, 0, 0);
        }

        var published = new List<long>();
        var failures = new List<(long Id, int Attempt, string Error)>();

        foreach (var row in claimed)
        {
            try
            {
                var result = await producer.PublishRawAsync(
                    row.Topic,
                    row.PartitionKey,
                    Encoding.UTF8.GetBytes(row.Payload),
                    BuildHeaders(row),
                    cancellationToken);

                published.Add(row.Id);

                logger.LogDebug(
                    "Outbox {Id} -> {Topic}[{Partition}]@{Offset} eventId={EventId}",
                    row.Id, result.Topic, result.Partition, result.Offset, row.EventId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutting down. Roll back so every unpublished row stays
                // claimable; anything already published this pass is
                // republished later and deduplicated by its stable event id.
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                failures.Add((row.Id, row.AttemptCount + 1, Describe(ex)));

                logger.LogWarning(ex,
                    "Outbox {Id} publish failed on attempt {Attempt} to {Topic}.",
                    row.Id, row.AttemptCount + 1, row.Topic);
            }
        }

        if (published.Count > 0)
        {
            await MarkPublishedAsync(connection, transaction, published, cancellationToken);
        }

        var deadLettered = 0;

        if (failures.Count > 0)
        {
            deadLettered = await RecordFailuresAsync(connection, transaction, failures, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new OutboxDrainResult(claimed.Count, published.Count, failures.Count, deadLettered);
    }

    private sealed record ClaimedRow(
        long Id, Guid EventId, Guid CorrelationId, Guid OrganizationId,
        string EventType, int EventVersion, string Topic, string PartitionKey,
        string Payload, string? Headers, int AttemptCount);

    /// <summary>
    /// Takes the next batch of due, pending rows and locks them for this
    /// transaction only.
    ///
    /// SKIP LOCKED is what makes multiple publishers safe. Without it, a second
    /// replica would block on the first replica's locks and the two would
    /// serialise; with it, the second replica moves straight past to rows
    /// nobody is holding.
    /// </summary>
    private async Task<List<ClaimedRow>> ClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, event_id, correlation_id, organization_id,
                   event_type, event_version, topic, partition_key,
                   payload::text, headers::text, attempt_count
            FROM outbox_messages
            WHERE published_at IS NULL
              AND dead_lettered_at IS NULL
              AND (next_attempt_at IS NULL OR next_attempt_at <= @now)
            ORDER BY id
            LIMIT @batch
            FOR UPDATE SKIP LOCKED;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("now", timeProvider.GetUtcNow());
        command.Parameters.AddWithValue("batch", _options.BatchSize);

        var rows = new List<ClaimedRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ClaimedRow(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetGuid(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetInt32(10)));
        }

        return rows;
    }

    private static async Task MarkPublishedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        List<long> ids,
        CancellationToken cancellationToken)
    {
        // published_at is set in the same transaction that claimed the rows, so
        // a row is either still pending or definitively done - never a third
        // state that a crash could strand.
        const string sql = """
            UPDATE outbox_messages
            SET published_at = now(), last_error = NULL
            WHERE id = ANY(@ids);
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = ids.ToArray() });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Records an attempt, schedules the retry, and gives up once the limit is
    /// reached. Returns how many rows were dead-lettered.
    /// </summary>
    private async Task<int> RecordFailuresAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        List<(long Id, int Attempt, string Error)> failures,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var ids = new long[failures.Count];
        var attempts = new int[failures.Count];
        var errors = new string[failures.Count];
        var nextAttempts = new DateTimeOffset[failures.Count];
        var deadLettered = new object[failures.Count];
        var count = 0;

        for (var i = 0; i < failures.Count; i++)
        {
            var (id, attempt, error) = failures[i];

            ids[i] = id;
            attempts[i] = attempt;
            errors[i] = error;
            nextAttempts[i] = now.Add(BackoffFor(attempt));

            if (attempt >= _options.MaxAttempts)
            {
                deadLettered[i] = now;
                count++;
            }
            else
            {
                deadLettered[i] = DBNull.Value;
            }
        }

        const string sql = """
            UPDATE outbox_messages AS o
            SET attempt_count    = t.attempt,
                last_error       = t.error,
                next_attempt_at  = t.next_attempt,
                dead_lettered_at = t.dead_lettered
            FROM unnest(@ids, @attempts, @errors, @next_attempts, @dead_lettered)
                 AS t(id, attempt, error, next_attempt, dead_lettered)
            WHERE o.id = t.id;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = ids });
        command.Parameters.Add(new NpgsqlParameter("attempts", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = attempts });
        command.Parameters.Add(new NpgsqlParameter("errors", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = errors });
        command.Parameters.Add(new NpgsqlParameter("next_attempts", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = nextAttempts });
        command.Parameters.Add(new NpgsqlParameter("dead_lettered", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = deadLettered });

        await command.ExecuteNonQueryAsync(cancellationToken);

        return count;
    }

    /// <summary>Exponential backoff with jitter, capped.</summary>
    internal TimeSpan BackoffFor(int attempt)
    {
        var exponential = _options.RetryBaseDelayMs * Math.Pow(2, Math.Min(attempt - 1, 20));
        var capped = Math.Min(exponential, _options.RetryMaxDelaySeconds * 1000d);

        // Jitter stops several replicas recovering from one broker outage from
        // retrying in lockstep and knocking it over again.
        return TimeSpan.FromMilliseconds(capped + Random.Shared.Next(0, 250));
    }

    /// <summary>
    /// Headers are rebuilt from the row's own columns rather than parsed out of
    /// the payload, so a consumer routing on headers sees the same values the
    /// publisher used to route.
    /// </summary>
    private static Dictionary<string, string> BuildHeaders(ClaimedRow row)
    {
        var headers = new Dictionary<string, string>
        {
            [EventHeaders.EventId] = row.EventId.ToString(),
            [EventHeaders.EventType] = row.EventType,
            [EventHeaders.EventVersion] = row.EventVersion.ToString(),
            [EventHeaders.TenantId] = row.OrganizationId.ToString(),
            [EventHeaders.CorrelationId] = row.CorrelationId.ToString()
        };

        if (string.IsNullOrWhiteSpace(row.Headers))
        {
            return headers;
        }

        try
        {
            var extra = JsonSerializer.Deserialize<Dictionary<string, string>>(row.Headers);

            if (extra is not null)
            {
                foreach (var (name, value) in extra)
                {
                    headers[name] = value;
                }
            }
        }
        catch (JsonException)
        {
            // Malformed custom headers must not stop the event being delivered.
        }

        return headers;
    }

    private static string Describe(Exception ex)
    {
        var text = $"{ex.GetType().Name}: {ex.Message}";
        return text.Length > 2000 ? text[..2000] : text;
    }
}
