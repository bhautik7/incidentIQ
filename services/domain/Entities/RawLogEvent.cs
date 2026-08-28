using IncidentIQ.Domain.Abstractions;
using IncidentIQ.Domain.Enums;

namespace IncidentIQ.Domain.Entities;

/// <summary>
/// Every log line, kept for a short window.
///
/// This is the second tier of a deliberate split, and the two tiers answer
/// different questions:
///
/// <list type="bullet">
/// <item><see cref="LogEvent"/> is a <b>permanent capped sample</b> - at most
/// twenty rows per pattern, kept forever. It exists so that opening a
/// three-month-old incident still shows real lines behind it.</item>
/// <item>This table is <b>everything, briefly</b>. It exists so that searching
/// logs during an outage returns what actually happened rather than the
/// twenty lines that happened to be sampled first.</item>
/// </list>
///
/// Neither can do the other's job. A retention window applied to the sample
/// would empty the incident pages that are the product's whole point; a cap
/// applied here would make a search return the same twenty rows no matter what
/// was asked. ADR 0007 chose the sample and was right about storage; it left
/// the raw stream unaddressed because nothing needed it until the log explorer.
///
/// The cost is real and bounded: this is the only table that grows with traffic
/// rather than with distinct errors, which is why it carries the smallest index
/// set in the schema and a retention job that keeps it to a fixed horizon.
/// </summary>
public class RawLogEvent : ITenantScoped
{
    /// <summary>Surrogate key. Monotonic, so inserts stay at the end of the index and paging can use it as a tiebreak.</summary>
    public long Id { get; set; }

    public Guid OrganizationId { get; set; }

    /// <summary>
    /// The producing client's idempotency key, carried for correlation with the
    /// sample and the processed-event ledger.
    ///
    /// Deliberately <b>not</b> uniquely indexed here, unlike on the sample.
    /// Duplicate suppression already happened upstream - only events that
    /// cleared the processed-event check are written - and a unique index on
    /// the highest-volume table in the system would tax every insert to enforce
    /// something that cannot occur.
    /// </summary>
    public Guid EventId { get; set; }

    public Guid MonitoredServiceId { get; set; }
    public Guid EnvironmentId { get; set; }

    /// <summary>The pattern this line was fingerprinted to, for "show me every line behind this error".</summary>
    public Guid? LogPatternId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }

    public LogEventLevel Level { get; set; }

    public string Message { get; set; } = null!;
    public string? ExceptionType { get; set; }
    public string? StackTrace { get; set; }

    public string? TraceId { get; set; }
    public string? SpanId { get; set; }
    public string? Host { get; set; }

    /// <summary>Arbitrary structured properties from the log call, as jsonb.</summary>
    public string? Properties { get; set; }

    public MonitoredService MonitoredService { get; set; } = null!;
    public Environment Environment { get; set; } = null!;
    public LogPattern? LogPattern { get; set; }
}
