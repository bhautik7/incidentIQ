using IncidentIQ.Api.Contracts;
using IncidentIQ.Domain.Enums;
using IncidentIQ.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace IncidentIQ.Api.Endpoints;

/// <summary>
/// Aggregations behind the overview dashboard.
///
/// Raw SQL rather than LINQ: these are bucketed time-series queries using
/// <c>date_bin</c> and generated series, which EF cannot express, and the whole
/// page is three round trips because of it.
///
/// Tenant scoping is explicit here. The rest of the API relies on EF's global
/// query filters, but these statements bypass EF entirely - so every one of
/// them names organization_id, and the tests check that they do.
/// </summary>
public static class OverviewEndpoints
{
    public static IEndpointRouteBuilder MapOverviewEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1").WithTags("overview");

        group.MapGet("/overview", GetOverviewAsync).WithName("GetOverview");
        group.MapGet("/services/health", GetServiceHealthAsync).WithName("GetServiceHealth");

        return routes;
    }

    /// <summary>
    /// Bucket width for a window, chosen to keep the chart near 60-100 points.
    ///
    /// A fixed width fails at both ends: one-minute buckets over 30 days is
    /// 43,200 points nothing can render, and one-hour buckets over 15 minutes
    /// is a single bar.
    /// </summary>
    private static int BucketMinutesFor(int windowMinutes) => windowMinutes switch
    {
        <= 60 => 1,
        <= 360 => 5,
        <= 1440 => 15,
        <= 10080 => 60,
        _ => 360,
    };

    private static async Task<IResult> GetOverviewAsync(
        [FromServices] IncidentIQDbContext db,
        [FromServices] ITenantContext tenant,
        [FromServices] TimeProvider timeProvider,
        HttpContext http,
        [FromQuery] int windowMinutes = 1440,
        [FromQuery] string? environment = null,
        CancellationToken cancellationToken = default)
    {
        if (tenant.OrganizationId is not { } organizationId)
        {
            return Results.Unauthorized();
        }

        windowMinutes = Math.Clamp(windowMinutes, 15, 43_200);

        var bucketMinutes = BucketMinutesFor(windowMinutes);
        var now = timeProvider.GetUtcNow();
        var windowStart = now.AddMinutes(-windowMinutes);
        // The equivalent window immediately before this one, for the deltas.
        var previousStart = windowStart.AddMinutes(-windowMinutes);

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        var rows = await ReadTimelineAsync(
            connection, organizationId, environment, windowStart, now, bucketMinutes, cancellationToken);

        var timeline = rows.Select(row => row.Point).ToList();

        var counts = await ReadCountsAsync(
            connection, organizationId, environment, windowStart, previousStart, now, cancellationToken);

        var markers = await ReadMarkersAsync(
            connection, organizationId, environment, windowStart, now, cancellationToken);

        return Results.Ok(new OverviewResponse
        {
            WindowStart = windowStart,
            WindowEnd = now,
            BucketMinutes = bucketMinutes,
            TotalServices = counts.TotalServices,
            ActiveIncidents = Summarise(counts.ActiveIncidents, counts.PreviousActiveIncidents,
                rows.Select(row => (double)row.IncidentsOpened).ToList()),
            ErrorEvents = Summarise(counts.ErrorEvents, counts.PreviousErrorEvents,
                rows.Select(row => (double)row.Point.ErrorEvents).ToList()),
            ServicesAffected = Summarise(counts.ServicesAffected, counts.PreviousServicesAffected,
                rows.Select(row => (double)row.ServicesAffected).ToList()),
            // No series for MTTR: a rolling average over sparse resolutions is
            // noise shaped like a trend, and the card hides the sparkline
            // rather than drawing a misleading one.
            MeanTimeToResolutionMinutes = Summarise(counts.Mttr, counts.PreviousMttr, []),
            AiInvestigations = Summarise(counts.AiAnalyses, counts.PreviousAiAnalyses,
                rows.Select(row => (double)row.AnalysesCompleted).ToList()),
            Timeline = timeline,
            Markers = markers,
        });
    }

    private static MetricSummary Summarise(double value, double previous, IReadOnlyList<double> series) => new()
    {
        Value = value,
        PreviousValue = previous,
        // A change from zero has no percentage. Reporting one would be a lie
        // dressed as precision, so the UI is given null and says "new" instead.
        ChangePercent = previous > 0 ? Math.Round((value - previous) / previous * 100, 1) : null,
        Series = series,
    };

    /// <summary>
    /// Error and warning counts per bucket.
    ///
    /// generate_series produces every bucket in the window, so quiet periods
    /// are zeros rather than gaps - a line chart that skips empty buckets
    /// silently compresses time and makes a burst look longer than it was.
    /// </summary>
    private sealed record TimelineRow(TimelinePoint Point, long IncidentsOpened, long ServicesAffected, long AnalysesCompleted);

    private static async Task<List<TimelineRow>> ReadTimelineAsync(
        NpgsqlConnection connection, Guid organizationId, string? environment,
        DateTimeOffset from, DateTimeOffset to, int bucketMinutes, CancellationToken cancellationToken)
    {
        const string sql = """
            WITH buckets AS (
                SELECT generate_series(
                    date_bin(make_interval(mins => @bucketMinutes), @from, TIMESTAMPTZ '2000-01-01'),
                    @to,
                    make_interval(mins => @bucketMinutes)
                ) AS bucket_start
            ),
            counted AS (
                SELECT date_bin(make_interval(mins => @bucketMinutes), m.bucket_start, TIMESTAMPTZ '2000-01-01') AS bucket_start,
                       SUM(m.count) FILTER (WHERE p.level IN ('Error', 'Fatal'))::bigint AS errors,
                       SUM(m.count) FILTER (WHERE p.level = 'Warning')::bigint AS warnings
                FROM log_pattern_metrics m
                JOIN log_patterns p ON p.id = m.log_pattern_id
                JOIN environments e ON e.id = p.environment_id
                WHERE m.organization_id = @org
                  AND m.bucket_start >= @from AND m.bucket_start < @to
                  AND (@environment IS NULL OR e.key = @environment)
                GROUP BY 1
            ),
            opened AS (
                SELECT date_bin(make_interval(mins => @bucketMinutes), i.first_seen_at, TIMESTAMPTZ '2000-01-01') AS bucket_start,
                       count(*)::bigint AS incidents,
                       count(DISTINCT i.monitored_service_id)::bigint AS services
                FROM incidents i
                JOIN environments e ON e.id = i.environment_id
                WHERE i.organization_id = @org
                  AND i.first_seen_at >= @from AND i.first_seen_at < @to
                  AND (@environment IS NULL OR e.key = @environment)
                GROUP BY 1
            ),
            analysed AS (
                SELECT date_bin(make_interval(mins => @bucketMinutes), a.completed_at, TIMESTAMPTZ '2000-01-01') AS bucket_start,
                       count(*)::bigint AS analyses
                FROM ai_analyses a
                JOIN incidents i ON i.id = a.incident_id
                JOIN environments e ON e.id = i.environment_id
                WHERE a.organization_id = @org AND a.status = 'Completed'
                  AND a.completed_at >= @from AND a.completed_at < @to
                  AND (@environment IS NULL OR e.key = @environment)
                GROUP BY 1
            )
            SELECT b.bucket_start,
                   COALESCE(c.errors, 0) AS errors,
                   COALESCE(c.warnings, 0) AS warnings,
                   COALESCE(o.incidents, 0) AS incidents_opened,
                   COALESCE(o.services, 0) AS services_affected,
                   COALESCE(an.analyses, 0) AS analyses_completed
            FROM buckets b
            LEFT JOIN counted c ON c.bucket_start = b.bucket_start
            LEFT JOIN opened o ON o.bucket_start = b.bucket_start
            LEFT JOIN analysed an ON an.bucket_start = b.bucket_start
            ORDER BY b.bucket_start;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        command.Parameters.AddWithValue("bucketMinutes", bucketMinutes);
        command.Parameters.Add(new NpgsqlParameter("environment", NpgsqlDbType.Text)
        { Value = (object?)environment ?? DBNull.Value });

        var rows = new List<TimelineRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TimelineRow(
                new TimelinePoint
                {
                    BucketStart = reader.GetFieldValue<DateTimeOffset>(0),
                    ErrorEvents = reader.GetInt64(1),
                    WarningEvents = reader.GetInt64(2),
                },
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5)));
        }

        return rows;
    }

    private sealed record OverviewCounts(
        double ActiveIncidents, double PreviousActiveIncidents,
        double ErrorEvents, double PreviousErrorEvents,
        double ServicesAffected, double PreviousServicesAffected,
        double Mttr, double PreviousMttr,
        double AiAnalyses, double PreviousAiAnalyses,
        int TotalServices);

    /// <summary>
    /// All ten KPI numbers plus the service total, in a single round trip.
    ///
    /// Eleven separate queries on a page that refreshes every fifteen seconds
    /// would be eleven times the latency and eleven times the connection churn
    /// for numbers that all describe the same window.
    /// </summary>
    private static async Task<OverviewCounts> ReadCountsAsync(
        NpgsqlConnection connection, Guid organizationId, string? environment,
        DateTimeOffset windowStart, DateTimeOffset previousStart, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH scoped_incidents AS (
                SELECT i.*, s.key AS service_key
                FROM incidents i
                JOIN environments e ON e.id = i.environment_id
                JOIN monitored_services s ON s.id = i.monitored_service_id
                WHERE i.organization_id = @org
                  AND (@environment IS NULL OR e.key = @environment)
            ),
            events AS (
                SELECT m.bucket_start, m.count
                FROM log_pattern_metrics m
                JOIN log_patterns p ON p.id = m.log_pattern_id
                JOIN environments e ON e.id = p.environment_id
                WHERE m.organization_id = @org
                  AND p.level IN ('Error', 'Fatal')
                  AND (@environment IS NULL OR e.key = @environment)
            )
            SELECT
              (SELECT count(*) FROM scoped_incidents
                 WHERE status IN ('Detected','Investigating')),
              -- "Active as of the start of this window": open then, and not
              -- resolved before then. Comparing against a plain count of
              -- incidents created earlier would compare different things.
              (SELECT count(*) FROM scoped_incidents
                 WHERE first_seen_at < @windowStart
                   AND (resolved_at IS NULL OR resolved_at >= @windowStart)),

              (SELECT COALESCE(SUM(count), 0)::bigint FROM events
                 WHERE bucket_start >= @windowStart AND bucket_start < @now),
              (SELECT COALESCE(SUM(count), 0)::bigint FROM events
                 WHERE bucket_start >= @previousStart AND bucket_start < @windowStart),

              (SELECT count(DISTINCT service_key) FROM scoped_incidents
                 WHERE status IN ('Detected','Investigating')),
              (SELECT count(DISTINCT service_key) FROM scoped_incidents
                 WHERE first_seen_at < @windowStart
                   AND (resolved_at IS NULL OR resolved_at >= @windowStart)),

              (SELECT COALESCE(AVG(EXTRACT(EPOCH FROM (resolved_at - first_seen_at)) / 60), 0)
                 FROM scoped_incidents
                 WHERE resolved_at >= @windowStart AND resolved_at < @now),
              (SELECT COALESCE(AVG(EXTRACT(EPOCH FROM (resolved_at - first_seen_at)) / 60), 0)
                 FROM scoped_incidents
                 WHERE resolved_at >= @previousStart AND resolved_at < @windowStart),

              (SELECT count(*) FROM ai_analyses a
                 JOIN scoped_incidents si ON si.id = a.incident_id
                 WHERE a.status = 'Completed' AND a.completed_at >= @windowStart AND a.completed_at < @now),
              (SELECT count(*) FROM ai_analyses a
                 JOIN scoped_incidents si ON si.id = a.incident_id
                 WHERE a.status = 'Completed' AND a.completed_at >= @previousStart AND a.completed_at < @windowStart),

              (SELECT count(*) FROM monitored_services WHERE organization_id = @org AND is_active);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("windowStart", windowStart);
        command.Parameters.AddWithValue("previousStart", previousStart);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.Add(new NpgsqlParameter("environment", NpgsqlDbType.Text)
        { Value = (object?)environment ?? DBNull.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        double Number(int ordinal) => reader.IsDBNull(ordinal) ? 0 : Convert.ToDouble(reader.GetValue(ordinal));

        return new OverviewCounts(
            Number(0), Number(1), Number(2), Number(3), Number(4),
            Number(5), Math.Round(Number(6), 1), Math.Round(Number(7), 1),
            Number(8), Number(9), (int)Number(10));
    }

    /// <summary>
    /// Deployments and incident openings inside the window.
    ///
    /// Capped, because a chart with four hundred vertical markers communicates
    /// nothing. The cap keeps the most recent, which is what an incident
    /// investigation is looking at.
    /// </summary>
    private static async Task<List<TimelineMarker>> ReadMarkersAsync(
        NpgsqlConnection connection, Guid organizationId, string? environment,
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        const string sql = """
            (SELECT 'deployment' AS kind, d.deployed_at AS at, d.version AS label,
                    s.key AS service, NULL::uuid AS incident_id, NULL::text AS severity
             FROM deployments d
             JOIN monitored_services s ON s.id = d.monitored_service_id
             JOIN environments e ON e.id = d.environment_id
             WHERE d.organization_id = @org
               AND d.deployed_at >= @from AND d.deployed_at <= @to
               AND (@environment IS NULL OR e.key = @environment)
             ORDER BY d.deployed_at DESC
             LIMIT 40)
            UNION ALL
            (SELECT 'incident', i.first_seen_at, left(i.title, 60),
                    s.key, i.id, i.severity
             FROM incidents i
             JOIN monitored_services s ON s.id = i.monitored_service_id
             JOIN environments e ON e.id = i.environment_id
             WHERE i.organization_id = @org
               AND i.first_seen_at >= @from AND i.first_seen_at <= @to
               AND (@environment IS NULL OR e.key = @environment)
             ORDER BY i.first_seen_at DESC
             LIMIT 40)
            ORDER BY at;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        command.Parameters.Add(new NpgsqlParameter("environment", NpgsqlDbType.Text)
        { Value = (object?)environment ?? DBNull.Value });

        var markers = new List<TimelineMarker>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            markers.Add(new TimelineMarker
            {
                Kind = reader.GetString(0),
                At = reader.GetFieldValue<DateTimeOffset>(1),
                Label = reader.GetString(2),
                Service = reader.GetString(3),
                IncidentId = reader.IsDBNull(4) ? null : reader.GetGuid(4),
                Severity = reader.IsDBNull(5) ? null : reader.GetString(5),
            });
        }

        return markers;
    }

    private static async Task<IResult> GetServiceHealthAsync(
        [FromServices] IncidentIQDbContext db,
        [FromServices] ITenantContext tenant,
        [FromServices] TimeProvider timeProvider,
        [FromQuery] int windowMinutes = 1440,
        [FromQuery] string? environment = null,
        CancellationToken cancellationToken = default)
    {
        if (tenant.OrganizationId is not { } organizationId)
        {
            return Results.Unauthorized();
        }

        windowMinutes = Math.Clamp(windowMinutes, 15, 43_200);

        var now = timeProvider.GetUtcNow();
        var from = now.AddMinutes(-windowMinutes);

        const string sql = """
            SELECT s.key, s.display_name, s.owner_team,
                   COALESCE(inc.active_count, 0)::int AS active_incidents,
                   COALESCE(inc.worst_severity, 'none') AS worst_severity,
                   COALESCE(ev.error_events, 0)::bigint AS error_events,
                   COALESCE(ev.pattern_count, 0)::int AS pattern_count,
                   inc.last_incident_at
            FROM monitored_services s
            LEFT JOIN LATERAL (
                SELECT count(*) FILTER (WHERE i.status IN ('Detected','Investigating')) AS active_count,
                       max(i.last_seen_at) AS last_incident_at,
                       -- Worst active severity drives the health badge; an
                       -- average would let one critical hide behind three lows.
                       min(CASE i.severity WHEN 'Critical' THEN 1 WHEN 'High' THEN 2
                                           WHEN 'Medium' THEN 3 ELSE 4 END)
                           FILTER (WHERE i.status IN ('Detected','Investigating'))::text AS worst_severity
                FROM incidents i
                JOIN environments e ON e.id = i.environment_id
                WHERE i.monitored_service_id = s.id
                  AND (@environment IS NULL OR e.key = @environment)
            ) inc ON TRUE
            LEFT JOIN LATERAL (
                SELECT SUM(m.count)::bigint AS error_events,
                       count(DISTINCT p.id) AS pattern_count
                FROM log_patterns p
                JOIN log_pattern_metrics m ON m.log_pattern_id = p.id
                JOIN environments e ON e.id = p.environment_id
                WHERE p.monitored_service_id = s.id
                  AND p.level IN ('Error','Fatal')
                  AND m.bucket_start >= @from AND m.bucket_start < @now
                  AND (@environment IS NULL OR e.key = @environment)
            ) ev ON TRUE
            WHERE s.organization_id = @org AND s.is_active
            ORDER BY COALESCE(inc.active_count, 0) DESC, COALESCE(ev.error_events, 0) DESC, s.key;
            """;

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.Add(new NpgsqlParameter("environment", NpgsqlDbType.Text)
        { Value = (object?)environment ?? DBNull.Value });

        var services = new List<ServiceHealth>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var activeIncidents = reader.GetInt32(3);
            var worstSeverity = reader.GetString(4);

            services.Add(new ServiceHealth
            {
                Key = reader.GetString(0),
                DisplayName = reader.GetString(1),
                OwnerTeam = reader.IsDBNull(2) ? null : reader.GetString(2),
                Health = HealthFor(activeIncidents, worstSeverity),
                ActiveIncidents = activeIncidents,
                ErrorEvents = reader.GetInt64(5),
                DistinctErrorPatterns = reader.GetInt32(6),
                LastIncidentAt = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
            });
        }

        return Results.Ok(services);
    }

    /// <summary>
    /// Health from the worst active incident.
    ///
    /// Derived rather than stored: a status column would need something to keep
    /// it in step with the incidents that determine it, and that job is exactly
    /// the kind that silently stops running.
    /// </summary>
    private static string HealthFor(int activeIncidents, string worstSeverityRank) => activeIncidents switch
    {
        0 => nameof(Health.Healthy),
        _ when worstSeverityRank == "1" => "Critical",
        _ => "Degraded",
    };

    private enum Health { Healthy }
}
