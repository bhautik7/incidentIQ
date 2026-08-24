using IncidentIQ.Domain.Abstractions;
using IncidentIQ.Domain.Enums;

namespace IncidentIQ.Domain.Entities;

/// <summary>
/// One log line, as emitted by a monitored application.
///
/// The highest-volume table in the system, and the one whose design decisions
/// are driven entirely by that fact:
///
/// <list type="bullet">
/// <item><b>bigint key, not UUID.</b> This is the only entity whose identifier
/// never appears in a URL, an API response or a Kafka message, so it gains
/// nothing from a UUID and pays 8 bytes per row plus 8 bytes in every index.</item>
/// <item><b>Append-only.</b> No UpdatedAt, and rows are never modified after the
/// classification step, which keeps PostgreSQL from rewriting rows and keeps
/// table bloat down.</item>
/// <item><b>A capped sample, not the full stream.</b> Storing all 4,200 lines of
/// an incident here would be roughly 1.2 GB/day at production volume. Instead
/// the pipeline keeps a bounded number per pattern - first seen, last seen and
/// a few in between - and the authoritative count lives on
/// <see cref="LogPattern.OccurrenceCount"/>. The full stream stays in Kafka
/// and, later, in object storage.</item>
/// </list>
/// </summary>
public class LogEvent : ITenantScoped
{
    /// <summary>Surrogate key. Monotonic, so inserts stay at the end of the index.</summary>
    public long Id { get; set; }

    public Guid OrganizationId { get; set; }

    /// <summary>
    /// Idempotency key, generated once by the producing client and reused across
    /// its own HTTP retries. Unique per organization, which turns duplicate
    /// delivery from Kafka into a no-op enforced by the database rather than by
    /// application logic that races between consumer replicas.
    /// </summary>
    public Guid EventId { get; set; }

    public Guid MonitoredServiceId { get; set; }
    public Guid EnvironmentId { get; set; }

    /// <summary>Null until the event has been normalised and fingerprinted.</summary>
    public Guid? LogPatternId { get; set; }

    /// <summary>Set when this sample was attached to an incident.</summary>
    public Guid? IncidentId { get; set; }

    /// <summary>When the application logged it. Drives the business timeline and retention.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>When IncidentIQ accepted it. Differs from OccurredAt by client buffering and clock skew; the gap is the pipeline lag metric.</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    public LogEventLevel Level { get; set; }

    public string Message { get; set; } = null!;
    public string? ExceptionType { get; set; }
    public string? StackTrace { get; set; }

    /// <summary>Distributed trace identifiers, when the emitting service provides them.</summary>
    public string? TraceId { get; set; }
    public string? SpanId { get; set; }

    /// <summary>Emitting host or pod, e.g. "payments-api-7d9f-x4k2".</summary>
    public string? Host { get; set; }

    /// <summary>
    /// Arbitrary structured properties from the log call, stored as jsonb.
    /// This is the deliberate escape hatch for fields we cannot know in advance;
    /// anything we query on regularly should be promoted to a real column.
    /// </summary>
    public string? Properties { get; set; }

    public MonitoredService MonitoredService { get; set; } = null!;
    public Environment Environment { get; set; } = null!;
    public LogPattern? LogPattern { get; set; }
    public Incident? Incident { get; set; }
}
