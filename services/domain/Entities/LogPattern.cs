using IncidentIQ.Domain.Abstractions;
using IncidentIQ.Domain.Enums;

namespace IncidentIQ.Domain.Entities;

/// <summary>
/// A *class* of log events - the thing 4,200 near-identical error lines
/// collapse into.
///
/// The distinction from <see cref="LogEvent"/> is the core of the product.
/// A LogEvent is one observation. A LogPattern is the recurring shape behind
/// many observations, identified by a <see cref="Fingerprint"/> computed from
/// the *normalised* message - the one with GUIDs, numbers and IPs masked out.
///
///   "pool exhausted, MaxPoolSize (currently 100)"   -> LogEvent
///   "pool exhausted, MaxPoolSize (currently &lt;NUM&gt;)" -> LogPattern
///
/// This is also where the authoritative <see cref="OccurrenceCount"/> lives.
/// Counting rows in LogEvents would give the wrong answer, because LogEvents
/// only ever holds a capped *sample* of each pattern's occurrences.
/// </summary>
public class LogPattern : ITenantScoped, IAuditable
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid MonitoredServiceId { get; set; }
    public Guid EnvironmentId { get; set; }

    /// <summary>
    /// SHA-256 of (organization | environment | service | exception type |
    /// normalised message | top stack frames), as 64 hex characters.
    /// Deterministic across restarts and deployments - that stability is what
    /// makes deduplication work at all.
    /// </summary>
    public string Fingerprint { get; set; } = null!;

    public LogEventLevel Level { get; set; }

    public string? ExceptionType { get; set; }

    /// <summary>The normalised message, with variable parts masked.</summary>
    public string MessageTemplate { get; set; } = null!;

    /// <summary>One real, un-masked message, so the UI can show something concrete.</summary>
    public string SampleMessage { get; set; } = null!;

    public string? TopStackFrames { get; set; }

    /// <summary>
    /// Total occurrences ever seen. Incremented in the same transaction that
    /// records an occurrence, so a replayed Kafka batch cannot inflate it.
    /// </summary>
    public long OccurrenceCount { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>Muted patterns are still counted but never open an incident.</summary>
    public bool IsMuted { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public MonitoredService MonitoredService { get; set; } = null!;
    public Environment Environment { get; set; } = null!;
    public ICollection<Incident> Incidents { get; set; } = [];
}
