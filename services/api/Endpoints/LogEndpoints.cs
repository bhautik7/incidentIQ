using System.Buffers.Text;
using System.Text;
using IncidentIQ.Api.Contracts;
using IncidentIQ.Domain.Entities;
using IncidentIQ.Domain.Enums;
using IncidentIQ.Persistence;
using IncidentIQ.Shared.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IncidentIQ.Api.Endpoints;

/// <summary>
/// Log search over the retention window.
///
/// Reads <c>raw_log_events</c>, not <c>log_events</c>. The latter is a capped
/// sample - twenty rows per pattern, kept forever - which is exactly right for
/// showing real lines behind a months-old incident and exactly wrong for
/// search: once a pattern hits its cap it stops recording, so a search would
/// return the same twenty rows however many million events had since gone past.
///
/// Paging is a keyset, not an offset. A log stream grows while it is being read,
/// and OFFSET on a moving stream shifts rows between pages - a line that arrives
/// mid-read pushes one off the end of page one and it is never seen. A cursor
/// pinned to <c>(occurred_at, id)</c> keeps every page anchored to the row it
/// actually followed.
/// </summary>
public static class LogEndpoints
{
    private const int DefaultPageSize = 100;
    private const int MaxPageSize = 500;

    public static IEndpointRouteBuilder MapLogEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/v1/logs", SearchAsync).WithName("SearchLogs").WithTags("logs");

        return routes;
    }

    private static async Task<IResult> SearchAsync(
        [FromServices] IncidentIQDbContext db,
        [FromServices] TimeProvider timeProvider,
        HttpContext http,
        [FromQuery] string? service,
        [FromQuery] string? environment,
        [FromQuery] string? level,
        [FromQuery] string? search,
        [FromQuery] string? traceId,
        [FromQuery] string? fingerprint,
        [FromQuery] int? windowMinutes,
        [FromQuery] string? cursor,
        [FromQuery] int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = db.RawLogEvents.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(service))
        {
            query = query.Where(e => e.MonitoredService.Key == service);
        }

        if (!string.IsNullOrWhiteSpace(environment))
        {
            query = query.Where(e => e.Environment.Key == environment);
        }

        if (!string.IsNullOrWhiteSpace(level))
        {
            if (!Enum.TryParse<LogEventLevel>(level, ignoreCase: true, out var parsedLevel))
            {
                return Problem(http, StatusCodes.Status400BadRequest, "Unknown level",
                    $"'{level}' is not a log level. Expected one of: {string.Join(", ", Enum.GetNames<LogEventLevel>())}.");
            }

            // At or above the requested level: asking for warnings and being
            // shown warnings with the errors filtered out is never what anyone
            // means.
            //
            // Expanded to a set rather than written as `Level >= parsedLevel`,
            // because the column stores the enum as a string. A relational
            // comparison would be evaluated alphabetically by PostgreSQL, and
            // alphabetically Error and Fatal sort *before* Warning - so
            // "warnings and above" would silently hide every error, which is
            // the precise opposite of what was asked for.
            var included = Enum.GetValues<LogEventLevel>()
                .Where(value => value >= parsedLevel)
                .ToArray();

            query = query.Where(e => included.Contains(e.Level));
        }

        if (!string.IsNullOrWhiteSpace(traceId))
        {
            query = query.Where(e => e.TraceId == traceId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(fingerprint))
        {
            query = query.Where(e => e.LogPattern != null && e.LogPattern.Fingerprint == fingerprint.Trim());
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(e => EF.Functions.ILike(e.Message, term));
        }

        if (windowMinutes is > 0)
        {
            var since = timeProvider.GetUtcNow().AddMinutes(-windowMinutes.Value);
            query = query.Where(e => e.OccurredAt >= since);
        }

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!TryDecodeCursor(cursor, out var at, out var id))
            {
                return Problem(http, StatusCodes.Status400BadRequest, "Malformed cursor",
                    "The cursor could not be read. Drop it to start from the newest line.");
            }

            // The keyset. The Id comparison is what makes this correct rather
            // than merely close: two lines can share a timestamp to the
            // microsecond, and without the tiebreak one of them is skipped.
            query = query.Where(e => e.OccurredAt < at || (e.OccurredAt == at && e.Id < id));
        }

        // One extra row, purely to learn whether another page exists without
        // running a second count query against a table this size.
        var rows = await query
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Take(pageSize + 1)
            .Select(e => new
            {
                e.Id,
                e.OccurredAt,
                e.ReceivedAt,
                e.Level,
                Service = e.MonitoredService.Key,
                Environment = e.Environment.Key,
                e.Message,
                e.ExceptionType,
                e.StackTrace,
                e.TraceId,
                e.SpanId,
                e.Host,
                e.Properties,
                Fingerprint = e.LogPattern != null ? e.LogPattern.Fingerprint : null,
                // The incident currently open for this line's pattern, if any,
                // so a row can offer "open INC-…" without a second round trip.
                IncidentId = db.Incidents
                    .Where(i => i.LogPatternId == e.LogPatternId
                        && (i.Status == IncidentStatus.Detected || i.Status == IncidentStatus.Investigating))
                    .Select(i => (Guid?)i.Id)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > pageSize;
        var page = hasMore ? rows.Take(pageSize).ToList() : rows;

        var items = page.Select(row => new LogEntry
        {
            Id = row.Id,
            OccurredAt = row.OccurredAt,
            ReceivedAt = row.ReceivedAt,
            Level = row.Level.ToString(),
            Service = row.Service,
            Environment = row.Environment,
            Message = row.Message,
            ExceptionType = row.ExceptionType,
            StackTrace = row.StackTrace,
            TraceId = row.TraceId,
            SpanId = row.SpanId,
            Host = row.Host,
            Properties = row.Properties,
            Fingerprint = row.Fingerprint,
            IncidentId = row.IncidentId
        }).ToList();

        var last = page.Count > 0 ? page[^1] : null;

        // The oldest line actually held, not the configured horizon: after a
        // quiet weekend those differ, and the honest answer is what is there.
        var oldest = await db.RawLogEvents.AsNoTracking()
            .OrderBy(e => e.OccurredAt)
            .Select(e => (DateTimeOffset?)e.OccurredAt)
            .FirstOrDefaultAsync(cancellationToken);

        return Results.Ok(new LogSearchResult
        {
            Page = new CursorPage<LogEntry>
            {
                Items = items,
                NextCursor = hasMore && last is not null ? EncodeCursor(last.OccurredAt, last.Id) : null
            },
            Window = new LogWindow
            {
                RetentionHours = RawLogRetentionHours,
                OldestAvailableAt = oldest
            }
        });
    }

    /// <summary>
    /// Mirrors the event-processor's retention default.
    ///
    /// Duplicated rather than shared because the API has no reason to depend on
    /// the worker, and it is reported to the UI as information rather than
    /// enforced here. If the two drift, the explorer's stated horizon is wrong
    /// while its actual results stay correct - the oldest-available timestamp
    /// beside it is the value that is always true.
    /// </summary>
    private const int RawLogRetentionHours = 48;

    /// <summary>
    /// The keyset, base64url so it survives a query string and reads as opaque.
    ///
    /// Opaque on purpose: a caller that starts constructing these is depending
    /// on the ordering being exactly what it is today, and the format is not a
    /// promise.
    /// </summary>
    private static string EncodeCursor(DateTimeOffset at, long id) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{at.UtcTicks}:{id}"));

    private static bool TryDecodeCursor(string cursor, out DateTimeOffset at, out long id)
    {
        at = default;
        id = default;

        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor)).Split(':');

            if (parts.Length != 2
                || !long.TryParse(parts[0], out var ticks)
                || !long.TryParse(parts[1], out id))
            {
                return false;
            }

            at = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static IResult Problem(HttpContext http, int statusCode, string title, string detail) =>
        Results.Problem(
            title: title,
            detail: detail,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?> { ["correlationId"] = http.GetCorrelationId() });
}
