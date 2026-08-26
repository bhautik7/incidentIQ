using IncidentIQ.Domain.Enums;
using IncidentIQ.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace IncidentIQ.EventProcessor.Detection;

public sealed record PatternSnapshot(
    Guid Id,
    string Fingerprint,
    Guid MonitoredServiceId,
    Guid EnvironmentId,
    string MessageTemplate,
    string SampleMessage,
    string? ExceptionType,
    DateTimeOffset FirstSeenAt,
    bool IsMuted,
    int? HttpStatusCode);

public sealed record ActiveIncidentRow(Guid Id, IncidentStatus Status, IncidentSeverity Severity);

public sealed record RecentDeployment(Guid Id, string Version, DateTimeOffset DeployedAt);

/// <summary>
/// Every database operation detection needs, kept out of the rules so the rules
/// stay pure and the SQL stays in one reviewable place.
///
/// All statements run on the caller's transaction. Detection has to open an
/// incident and enqueue its outbox message atomically, so nothing here is
/// allowed to commit on its own behalf.
/// </summary>
public sealed class IncidentDetectionStore(IncidentIQDbContext dbContext)
{
    private (NpgsqlConnection Connection, NpgsqlTransaction? Transaction) Current()
    {
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        var transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        return (connection, transaction);
    }

    private NpgsqlCommand Command(string sql)
    {
        var (connection, transaction) = Current();
        return new NpgsqlCommand(sql, connection, transaction);
    }

    /// <summary>Resolves fingerprints to patterns in one round trip.</summary>
    public async Task<Dictionary<string, PatternSnapshot>> GetPatternsAsync(
        Guid organizationId, IReadOnlyCollection<string> fingerprints, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, fingerprint, monitored_service_id, environment_id,
                   message_template, sample_message, exception_type,
                   first_seen_at, is_muted, http_status_code
            FROM log_patterns
            WHERE organization_id = @org AND fingerprint = ANY(@fingerprints);
            """;

        await using var command = Command(sql);
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.Add(new NpgsqlParameter("fingerprints", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = fingerprints.ToArray()
        });

        var result = new Dictionary<string, PatternSnapshot>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var snapshot = new PatternSnapshot(
                reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetGuid(3),
                reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7), reader.GetBoolean(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9));

            result[snapshot.Fingerprint] = snapshot;
        }

        return result;
    }

    /// <summary>
    /// Adds occurrences to minute buckets, one statement for the whole batch.
    ///
    /// Buckets rather than rows-per-event: the window and baseline queries then
    /// scan tens of rows instead of millions, and the same buckets become the
    /// incident sparkline later.
    /// </summary>
    public async Task RecordOccurrencesAsync(
        Guid organizationId,
        IReadOnlyList<(Guid PatternId, DateTimeOffset Bucket, long Count)> buckets,
        CancellationToken cancellationToken)
    {
        if (buckets.Count == 0)
        {
            return;
        }

        const string sql = """
            INSERT INTO log_pattern_metrics (organization_id, log_pattern_id, bucket_start, count)
            SELECT @org, t.pattern, t.bucket, t.count
            FROM unnest(@patterns, @buckets, @counts) AS t(pattern, bucket, count)
            ON CONFLICT (log_pattern_id, bucket_start)
            DO UPDATE SET count = log_pattern_metrics.count + EXCLUDED.count;
            """;

        await using var command = Command(sql);
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.Add(new NpgsqlParameter("patterns", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        { Value = buckets.Select(b => b.PatternId).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("buckets", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
        { Value = buckets.Select(b => b.Bucket).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("counts", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
        { Value = buckets.Select(b => b.Count).ToArray() });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Occurrences of one pattern between two instants.</summary>
    public async Task<long> GetCountAsync(
        Guid patternId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COALESCE(SUM(count), 0)::bigint
            FROM log_pattern_metrics
            WHERE log_pattern_id = @pattern AND bucket_start >= @from AND bucket_start < @to;
            """;

        await using var command = Command(sql);
        command.Parameters.AddWithValue("pattern", patternId);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);

        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// 5xx occurrences for a service and environment in the window, summed
    /// across every fingerprint.
    ///
    /// This is the rule no per-pattern threshold can express: an outage that
    /// shows up as fifty different errors at once, none of them individually
    /// past its own threshold.
    /// </summary>
    public async Task<long> GetServerErrorCountAsync(
        Guid organizationId, Guid serviceId, Guid environmentId,
        DateTimeOffset from, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COALESCE(SUM(m.count), 0)::bigint
            FROM log_pattern_metrics m
            JOIN log_patterns p ON p.id = m.log_pattern_id
            WHERE p.organization_id = @org
              AND p.monitored_service_id = @service
              AND p.environment_id = @environment
              AND p.http_status_code BETWEEN 500 AND 599
              AND m.bucket_start >= @from;
            """;

        await using var command = Command(sql);
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("service", serviceId);
        command.Parameters.AddWithValue("environment", environmentId);
        command.Parameters.AddWithValue("from", from);

        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>The most recent release of this service that could explain an incident starting now.</summary>
    public async Task<RecentDeployment?> GetRecentDeploymentAsync(
        Guid organizationId, Guid serviceId, Guid environmentId,
        DateTimeOffset notBefore, DateTimeOffset notAfter, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, version, deployed_at
            FROM deployments
            WHERE organization_id = @org
              AND monitored_service_id = @service
              AND environment_id = @environment
              AND deployed_at >= @notBefore
              AND deployed_at <= @notAfter
            ORDER BY deployed_at DESC
            LIMIT 1;
            """;

        await using var command = Command(sql);
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("service", serviceId);
        command.Parameters.AddWithValue("environment", environmentId);
        command.Parameters.AddWithValue("notBefore", notBefore);
        command.Parameters.AddWithValue("notAfter", notAfter);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken)
            ? new RecentDeployment(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2))
            : null;
    }

    /// <summary>
    /// Folds new occurrences into an already-active incident.
    ///
    /// Returns the incident when one was updated, which is the signal that no
    /// new incident - and no new IncidentDetected event - should be created.
    /// This is where "hundreds of duplicates" are actually prevented in the
    /// common case; the unique index below is the backstop for the race.
    /// </summary>
    public async Task<ActiveIncidentRow?> TryUpdateActiveIncidentAsync(
        Guid organizationId, string dedupeKey, long additionalOccurrences,
        DateTimeOffset lastSeenAt, IncidentSeverity severity, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE incidents
            SET occurrence_count = occurrence_count + @count,
                last_seen_at     = GREATEST(last_seen_at, @lastSeen),
                -- Severity only ever climbs while an incident is open. A quiet
                -- minute must not downgrade something that already escalated.
                severity = CASE
                    WHEN @severityRank > CASE severity
                        WHEN 'Critical' THEN 4 WHEN 'High' THEN 3 WHEN 'Medium' THEN 2 ELSE 1 END
                    THEN @severity ELSE severity END,
                updated_at = now()
            WHERE organization_id = @org
              AND dedupe_key = @dedupeKey
              AND status IN ('Detected', 'Investigating')
            RETURNING id, status, severity;
            """;

        await using var command = Command(sql);
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("dedupeKey", dedupeKey);
        command.Parameters.AddWithValue("count", additionalOccurrences);
        command.Parameters.AddWithValue("lastSeen", lastSeenAt);
        command.Parameters.AddWithValue("severity", severity.ToString());
        command.Parameters.AddWithValue("severityRank", SeverityRank(severity));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken)
            ? new ActiveIncidentRow(
                reader.GetGuid(0),
                Enum.Parse<IncidentStatus>(reader.GetString(1)),
                Enum.Parse<IncidentSeverity>(reader.GetString(2)))
            : null;
    }

    /// <summary>
    /// Brings a recently-resolved incident back rather than opening a new one.
    ///
    /// Without this, an error that flaps produces a fresh incident every few
    /// minutes and the list becomes exactly the wall of noise the product exists
    /// to remove - one level up.
    /// </summary>
    public async Task<Guid?> TryReopenAsync(
        Guid organizationId, string dedupeKey, DateTimeOffset resolvedSince,
        long additionalOccurrences, DateTimeOffset lastSeenAt, IncidentSeverity severity,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE incidents
            SET status           = 'Detected',
                resolved_at      = NULL,
                resolved_by_user_id = NULL,
                occurrence_count = occurrence_count + @count,
                last_seen_at     = GREATEST(last_seen_at, @lastSeen),
                severity         = @severity,
                updated_at       = now()
            WHERE id = (
                SELECT id FROM incidents
                WHERE organization_id = @org
                  AND dedupe_key = @dedupeKey
                  AND status = 'Resolved'
                  AND resolved_at >= @resolvedSince
                ORDER BY resolved_at DESC
                LIMIT 1
            )
            RETURNING id;
            """;

        await using var command = Command(sql);
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("dedupeKey", dedupeKey);
        command.Parameters.AddWithValue("resolvedSince", resolvedSince);
        command.Parameters.AddWithValue("count", additionalOccurrences);
        command.Parameters.AddWithValue("lastSeen", lastSeenAt);
        command.Parameters.AddWithValue("severity", severity.ToString());

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is Guid id ? id : null;
    }

    /// <summary>
    /// Opens a new incident, or returns null if another replica won the race.
    ///
    /// ON CONFLICT names the partial unique index's predicate so PostgreSQL can
    /// infer it. The losing replica gets null and folds its occurrences into the
    /// winner's incident instead - which is why two detectors processing the
    /// same burst cannot produce two incidents.
    /// </summary>
    public async Task<Guid?> TryInsertIncidentAsync(NewIncident incident, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO incidents (
                id, organization_id, monitored_service_id, environment_id, log_pattern_id,
                dedupe_key, detection_rule, title, status, severity, occurrence_count,
                first_seen_at, last_seen_at, suspected_deployment_id, created_at, updated_at)
            VALUES (
                @id, @org, @service, @environment, @pattern,
                @dedupeKey, @rule, @title, 'Detected', @severity, @count,
                @firstSeen, @lastSeen, @deployment, now(), now())
            ON CONFLICT (organization_id, dedupe_key)
                WHERE status IN ('Detected', 'Investigating')
            DO NOTHING
            RETURNING id;
            """;

        await using var command = Command(sql);
        command.Parameters.AddWithValue("id", incident.Id);
        command.Parameters.AddWithValue("org", incident.OrganizationId);
        command.Parameters.AddWithValue("service", incident.MonitoredServiceId);
        command.Parameters.AddWithValue("environment", incident.EnvironmentId);
        command.Parameters.AddWithValue("pattern", (object?)incident.LogPatternId ?? DBNull.Value);
        command.Parameters.AddWithValue("dedupeKey", incident.DedupeKey);
        command.Parameters.AddWithValue("rule", incident.Rule.ToString());
        command.Parameters.AddWithValue("title", incident.Title);
        command.Parameters.AddWithValue("severity", incident.Severity.ToString());
        command.Parameters.AddWithValue("count", incident.OccurrenceCount);
        command.Parameters.AddWithValue("firstSeen", incident.FirstSeenAt);
        command.Parameters.AddWithValue("lastSeen", incident.LastSeenAt);
        command.Parameters.AddWithValue("deployment", (object?)incident.SuspectedDeploymentId ?? DBNull.Value);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is Guid id ? id : null;
    }

    public async Task AddIncidentEventAsync(
        Guid organizationId, Guid incidentId, IncidentEventType type,
        DateTimeOffset occurredAt, string message, string? dataJson,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO incident_events
                (organization_id, incident_id, type, occurred_at, actor_type, message, data)
            VALUES (@org, @incident, @type, @occurredAt, 'System', @message, @data::jsonb);
            """;

        await using var command = Command(sql);
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("incident", incidentId);
        command.Parameters.AddWithValue("type", type.ToString());
        command.Parameters.AddWithValue("occurredAt", occurredAt);
        command.Parameters.AddWithValue("message", message);
        command.Parameters.AddWithValue("data", (object?)dataJson ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// The next analysis version for an incident.
    ///
    /// A reopened incident is a different problem from the one that was
    /// analysed before it was resolved, so it gets its own version rather than
    /// silently colliding with the previous analysis's unique key.
    /// </summary>
    public async Task<int> NextAnalysisVersionAsync(Guid incidentId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COALESCE(MAX(analysis_version), 0) + 1
            FROM ai_analyses
            WHERE incident_id = @incident;
            """;

        await using var command = Command(sql);
        command.Parameters.AddWithValue("incident", incidentId);

        return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static int SeverityRank(IncidentSeverity severity) => severity switch
    {
        IncidentSeverity.Critical => 4,
        IncidentSeverity.High => 3,
        IncidentSeverity.Medium => 2,
        _ => 1
    };
}

public sealed record NewIncident
{
    public required Guid Id { get; init; }
    public required Guid OrganizationId { get; init; }
    public required Guid MonitoredServiceId { get; init; }
    public required Guid EnvironmentId { get; init; }
    public Guid? LogPatternId { get; init; }
    public required string DedupeKey { get; init; }
    public required DetectionRule Rule { get; init; }
    public required string Title { get; init; }
    public required IncidentSeverity Severity { get; init; }
    public required long OccurrenceCount { get; init; }
    public required DateTimeOffset FirstSeenAt { get; init; }
    public required DateTimeOffset LastSeenAt { get; init; }
    public Guid? SuspectedDeploymentId { get; init; }
}
