using IncidentIQ.Domain.Abstractions;

namespace IncidentIQ.Domain.Entities;

/// <summary>
/// Occurrences of one pattern in one minute.
///
/// Detection rules ask questions about a *window* - "how many in the last five
/// minutes", "is that far above the last hour" - and
/// <see cref="LogPattern.OccurrenceCount"/> cannot answer either: it is a
/// lifetime total, so a pattern that fired 100,000 times last year looks
/// identical to one firing 100,000 times right now.
///
/// Minute buckets rather than a row per event: at ingestion volume the raw
/// events are millions of rows, while the buckets are one per pattern per
/// minute regardless of how loud the pattern is. Summing forty buckets is a
/// cheap query; counting four million rows is not.
///
/// These are also exactly what the incident sparkline needs later.
/// </summary>
public class LogPatternMetric : ITenantScoped
{
    public Guid OrganizationId { get; set; }

    public Guid LogPatternId { get; set; }

    /// <summary>Start of the minute, truncated. Part of the primary key.</summary>
    public DateTimeOffset BucketStart { get; set; }

    public long Count { get; set; }

    public LogPattern LogPattern { get; set; } = null!;
}
