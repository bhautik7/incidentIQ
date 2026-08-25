using IncidentIQ.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace IncidentIQ.EventProcessor.Processing;

/// <summary>A log event after normalisation, ready to be written.</summary>
public sealed record ProcessedLogEvent
{
    public required Guid LogEventId { get; init; }
    public required Guid OrganizationId { get; init; }
    public required Guid MonitoredServiceId { get; init; }
    public required Guid EnvironmentId { get; init; }
    public required string Fingerprint { get; init; }
    public required string Severity { get; init; }
    public required string Message { get; init; }
    public required string NormalizedMessage { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }
    public string? ExceptionType { get; init; }
    public string? StackTrace { get; init; }
    public string? TopStackFrames { get; init; }
    public string? TraceId { get; init; }
    public string? SpanId { get; init; }
    public string? Host { get; init; }
    public string? PropertiesJson { get; init; }
}

public sealed record BatchWriteResult(
    int Submitted,
    int AlreadyProcessed,
    int PatternsTouched,
    int SamplesInserted);

/// <summary>
/// Writes a whole batch of normalised events in a fixed number of statements,
/// regardless of how many events the batch contains.
///
/// A batch of 500 events sharing 3 fingerprints costs four round trips - a
/// dedup read, one pattern upsert, one sample insert, one processed-event
/// insert - not 2,000. Every statement uses PostgreSQL's <c>unnest</c> to turn
/// parallel arrays into rows, which is what keeps the cost constant.
///
/// Raw SQL rather than EF change tracking, deliberately. Each of these
/// statements depends on <c>ON CONFLICT</c> semantics that EF cannot express,
/// and those conflict clauses are the idempotency guarantee, not an
/// optimisation.
/// </summary>
public sealed class LogBatchWriter(IncidentIQDbContext dbContext, ILogger<LogBatchWriter> logger)
{
    /// <summary>
    /// Sampled occurrences retained per pattern.
    ///
    /// log_events is a sample, not an archive: the authoritative count lives on
    /// log_patterns.occurrence_count. Storing all 4,200 lines of one incident
    /// would be roughly 1.2 GB/day at production volume to answer a question a
    /// dozen examples already answer.
    /// </summary>
    public const int SamplesPerPattern = 20;

    public async Task<BatchWriteResult> WriteAsync(
        string consumerGroup,
        IReadOnlyList<ProcessedLogEvent> events,
        TimeSpan processedEventRetention,
        CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return new BatchWriteResult(0, 0, 0, 0);
        }

        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // 1. Which of these have we already handled? One read for the whole batch.
        var alreadyProcessed = await ReadProcessedAsync(connection, transaction, consumerGroup, events, cancellationToken);

        var fresh = events.Where(e => !alreadyProcessed.Contains(e.LogEventId)).ToList();

        if (fresh.Count == 0)
        {
            // Every event in this batch is a redelivery. Nothing to write, and
            // critically no counters to increment.
            await transaction.CommitAsync(cancellationToken);
            return new BatchWriteResult(events.Count, alreadyProcessed.Count, 0, 0);
        }

        // The same logical event can appear twice inside one batch - a client
        // that retried into the same poll window. Collapse it here so the
        // counter increment below counts it once.
        fresh = fresh
            .GroupBy(e => e.LogEventId)
            .Select(g => g.First())
            .ToList();

        // 2. One upsert per distinct fingerprint, not per event.
        var patterns = await UpsertPatternsAsync(connection, transaction, fresh, cancellationToken);

        // 3. Sampled occurrences only, capped per pattern.
        var samplesInserted = await InsertSamplesAsync(connection, transaction, fresh, patterns, cancellationToken);

        // 4. Record what was handled, so a redelivery short-circuits at step 1.
        await RecordProcessedAsync(connection, transaction, consumerGroup, fresh, processedEventRetention, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        logger.LogDebug(
            "Batch written. submitted={Submitted} duplicates={Duplicates} patterns={Patterns} samples={Samples}",
            events.Count, alreadyProcessed.Count, patterns.Count, samplesInserted);

        return new BatchWriteResult(events.Count, alreadyProcessed.Count, patterns.Count, samplesInserted);
    }

    private static async Task<HashSet<Guid>> ReadProcessedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string consumerGroup,
        IReadOnlyList<ProcessedLogEvent> events,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT event_id
            FROM processed_events
            WHERE consumer_group = @group AND event_id = ANY(@ids);
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("group", consumerGroup);
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            Value = events.Select(e => e.LogEventId).Distinct().ToArray()
        });

        var seen = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            seen.Add(reader.GetGuid(0));
        }

        return seen;
    }

    private sealed record PatternRow(Guid Id, long PriorCount);

    /// <summary>
    /// Creates or updates one row per distinct fingerprint.
    ///
    /// The counter is incremented by the number of *new* events in this batch,
    /// which is exact rather than approximate: step 1 removed redeliveries
    /// before we got here, so a replayed Kafka batch adds nothing. That is what
    /// makes occurrence_count trustworthy under at-least-once delivery, and an
    /// incident that claims 8,400 occurrences when there were 4,200 destroys
    /// confidence in everything else on the page.
    /// </summary>
    private static async Task<Dictionary<string, PatternRow>> UpsertPatternsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<ProcessedLogEvent> events,
        CancellationToken cancellationToken)
    {
        var grouped = events
            .GroupBy(e => e.Fingerprint)
            .Select(g => new
            {
                Fingerprint = g.Key,
                Count = (long)g.Count(),
                First = g.MinBy(e => e.OccurredAt)!,
                FirstSeen = g.Min(e => e.OccurredAt),
                LastSeen = g.Max(e => e.OccurredAt)
            })
            .ToList();

        const string sql = """
            INSERT INTO log_patterns (
                id, organization_id, monitored_service_id, environment_id, fingerprint,
                level, exception_type, message_template, sample_message, top_stack_frames,
                occurrence_count, first_seen_at, last_seen_at, is_muted, created_at, updated_at)
            SELECT
                t.id, t.org, t.service, t.env, t.fingerprint,
                t.level, t.exception_type, t.template, t.sample, t.frames,
                t.count, t.first_seen, t.last_seen, false, now(), now()
            FROM unnest(
                @ids, @orgs, @services, @envs, @fingerprints,
                @levels, @exception_types, @templates, @samples, @frames,
                @counts, @first_seen, @last_seen
            ) AS t(
                id, org, service, env, fingerprint,
                level, exception_type, template, sample, frames,
                count, first_seen, last_seen)
            ON CONFLICT (organization_id, fingerprint) DO UPDATE SET
                occurrence_count = log_patterns.occurrence_count + EXCLUDED.occurrence_count,
                first_seen_at    = LEAST(log_patterns.first_seen_at, EXCLUDED.first_seen_at),
                last_seen_at     = GREATEST(log_patterns.last_seen_at, EXCLUDED.last_seen_at),
                updated_at       = now()
            RETURNING id, fingerprint, occurrence_count;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddArray(command, "ids", NpgsqlDbType.Uuid, grouped.Select(_ => Guid.CreateVersion7()).ToArray());
        AddArray(command, "orgs", NpgsqlDbType.Uuid, grouped.Select(g => g.First.OrganizationId).ToArray());
        AddArray(command, "services", NpgsqlDbType.Uuid, grouped.Select(g => g.First.MonitoredServiceId).ToArray());
        AddArray(command, "envs", NpgsqlDbType.Uuid, grouped.Select(g => g.First.EnvironmentId).ToArray());
        AddArray(command, "fingerprints", NpgsqlDbType.Text, grouped.Select(g => g.Fingerprint).ToArray());
        AddArray(command, "levels", NpgsqlDbType.Text, grouped.Select(g => g.First.Severity).ToArray());
        AddArray(command, "exception_types", NpgsqlDbType.Text, grouped.Select(g => (object?)g.First.ExceptionType ?? DBNull.Value).ToArray());
        AddArray(command, "templates", NpgsqlDbType.Text, grouped.Select(g => Truncate(g.First.NormalizedMessage, 4000)).ToArray());
        AddArray(command, "samples", NpgsqlDbType.Text, grouped.Select(g => Truncate(g.First.Message, 4000)).ToArray());
        AddArray(command, "frames", NpgsqlDbType.Text, grouped.Select(g => (object?)Truncate(g.First.TopStackFrames, 4000) ?? DBNull.Value).ToArray());
        AddArray(command, "counts", NpgsqlDbType.Bigint, grouped.Select(g => g.Count).ToArray());
        AddArray(command, "first_seen", NpgsqlDbType.TimestampTz, grouped.Select(g => g.FirstSeen).ToArray());
        AddArray(command, "last_seen", NpgsqlDbType.TimestampTz, grouped.Select(g => g.LastSeen).ToArray());

        var increments = grouped.ToDictionary(g => g.Fingerprint, g => g.Count);
        var result = new Dictionary<string, PatternRow>(StringComparer.Ordinal);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            var fingerprint = reader.GetString(1);
            var newCount = reader.GetInt64(2);

            // How many this pattern had before this batch - which is what
            // decides whether the sample quota is already full.
            result[fingerprint] = new PatternRow(id, newCount - increments[fingerprint]);
        }

        return result;
    }

    /// <summary>
    /// Inserts sampled occurrences, up to the per-pattern cap.
    ///
    /// ON CONFLICT DO NOTHING on (organization_id, event_id) is the durable
    /// backstop. processed_events can be pruned, or a deployment can change the
    /// consumer group name; the unique index cannot be bypassed by either.
    /// </summary>
    private static async Task<int> InsertSamplesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<ProcessedLogEvent> events,
        Dictionary<string, PatternRow> patterns,
        CancellationToken cancellationToken)
    {
        var remaining = patterns.ToDictionary(
            p => p.Key,
            p => (int)Math.Max(0, SamplesPerPattern - p.Value.PriorCount),
            StringComparer.Ordinal);

        var toInsert = new List<ProcessedLogEvent>();

        foreach (var candidate in events)
        {
            if (remaining.TryGetValue(candidate.Fingerprint, out var quota) && quota > 0)
            {
                toInsert.Add(candidate);
                remaining[candidate.Fingerprint] = quota - 1;
            }
        }

        if (toInsert.Count == 0)
        {
            return 0;
        }

        const string sql = """
            INSERT INTO log_events (
                organization_id, event_id, monitored_service_id, environment_id, log_pattern_id,
                occurred_at, received_at, level, message, exception_type, stack_trace,
                trace_id, span_id, host, properties)
            SELECT
                t.org, t.event_id, t.service, t.env, t.pattern,
                t.occurred_at, t.received_at, t.level, t.message, t.exception_type, t.stack_trace,
                t.trace_id, t.span_id, t.host, t.properties::jsonb
            FROM unnest(
                @orgs, @event_ids, @services, @envs, @patterns,
                @occurred_at, @received_at, @levels, @messages, @exception_types, @stack_traces,
                @trace_ids, @span_ids, @hosts, @properties
            ) AS t(
                org, event_id, service, env, pattern,
                occurred_at, received_at, level, message, exception_type, stack_trace,
                trace_id, span_id, host, properties)
            ON CONFLICT (organization_id, event_id) DO NOTHING;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddArray(command, "orgs", NpgsqlDbType.Uuid, toInsert.Select(e => e.OrganizationId).ToArray());
        AddArray(command, "event_ids", NpgsqlDbType.Uuid, toInsert.Select(e => e.LogEventId).ToArray());
        AddArray(command, "services", NpgsqlDbType.Uuid, toInsert.Select(e => e.MonitoredServiceId).ToArray());
        AddArray(command, "envs", NpgsqlDbType.Uuid, toInsert.Select(e => e.EnvironmentId).ToArray());
        AddArray(command, "patterns", NpgsqlDbType.Uuid, toInsert.Select(e => (object)patterns[e.Fingerprint].Id).ToArray());
        AddArray(command, "occurred_at", NpgsqlDbType.TimestampTz, toInsert.Select(e => e.OccurredAt).ToArray());
        AddArray(command, "received_at", NpgsqlDbType.TimestampTz, toInsert.Select(e => e.ReceivedAt).ToArray());
        AddArray(command, "levels", NpgsqlDbType.Text, toInsert.Select(e => e.Severity).ToArray());
        AddArray(command, "messages", NpgsqlDbType.Text, toInsert.Select(e => Truncate(e.Message, 8000)).ToArray());
        AddArray(command, "exception_types", NpgsqlDbType.Text, toInsert.Select(e => (object?)e.ExceptionType ?? DBNull.Value).ToArray());
        AddArray(command, "stack_traces", NpgsqlDbType.Text, toInsert.Select(e => (object?)e.StackTrace ?? DBNull.Value).ToArray());
        AddArray(command, "trace_ids", NpgsqlDbType.Text, toInsert.Select(e => (object?)e.TraceId ?? DBNull.Value).ToArray());
        AddArray(command, "span_ids", NpgsqlDbType.Text, toInsert.Select(e => (object?)e.SpanId ?? DBNull.Value).ToArray());
        AddArray(command, "hosts", NpgsqlDbType.Text, toInsert.Select(e => (object?)e.Host ?? DBNull.Value).ToArray());
        AddArray(command, "properties", NpgsqlDbType.Text, toInsert.Select(e => (object?)e.PropertiesJson ?? DBNull.Value).ToArray());

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Marks these events handled by this consumer group.
    ///
    /// Rows expire after the Kafka retention window: once redelivery is
    /// impossible the record is dead weight, and this table would otherwise
    /// grow forever at the rate of the log stream.
    /// </summary>
    private static async Task RecordProcessedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string consumerGroup,
        IReadOnlyList<ProcessedLogEvent> events,
        TimeSpan retention,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO processed_events (consumer_group, event_id, organization_id, processed_at, expires_at)
            SELECT @group, t.event_id, t.org, now(), @expires
            FROM unnest(@event_ids, @orgs) AS t(event_id, org)
            ON CONFLICT (consumer_group, event_id) DO NOTHING;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("group", consumerGroup);
        command.Parameters.AddWithValue("expires", DateTimeOffset.UtcNow.Add(retention));
        AddArray(command, "event_ids", NpgsqlDbType.Uuid, events.Select(e => e.LogEventId).ToArray());
        AddArray(command, "orgs", NpgsqlDbType.Uuid, events.Select(e => (object)e.OrganizationId).ToArray());

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddArray(NpgsqlCommand command, string name, NpgsqlDbType elementType, Array values) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Array | elementType) { Value = values });

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
