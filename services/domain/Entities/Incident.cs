using IncidentIQ.Domain.Abstractions;
using IncidentIQ.Domain.Enums;

namespace IncidentIQ.Domain.Entities;

/// <summary>
/// An active problem worth a human's attention - the unit the product exists
/// to produce.
///
/// An Incident is the *current state* of a problem: what it is, how bad, how
/// many times, still open or not. The story of how it reached that state lives
/// in <see cref="IncidentEvent"/>, and the machine-generated explanation lives
/// in <see cref="AiAnalysis"/>. Keeping the three apart means a mutable status
/// column, an append-only timeline, and a replaceable analysis can each change
/// on their own schedule.
///
/// A partial unique index enforces the central invariant: at most one *active*
/// incident per pattern. Two consumer replicas processing the same burst
/// cannot both open one.
/// </summary>
public class Incident : ITenantScoped, IAuditable
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid MonitoredServiceId { get; set; }
    public Guid EnvironmentId { get; set; }

    /// <summary>
    /// The pattern this incident is about. Phase 3 models one pattern per
    /// incident; grouping several related patterns into one incident is a join
    /// table added when the correlation rules that need it exist.
    /// </summary>
    public Guid LogPatternId { get; set; }

    /// <summary>Starts as the normalised message; replaced by the AI-written title.</summary>
    public string Title { get; set; } = null!;

    public IncidentStatus Status { get; set; } = IncidentStatus.Open;
    public IncidentSeverity Severity { get; set; } = IncidentSeverity.Medium;

    /// <summary>Occurrences attributed to this incident, not rows in LogEvents.</summary>
    public long OccurrenceCount { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    public DateTimeOffset? AcknowledgedAt { get; set; }
    public Guid? AcknowledgedByUserId { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }
    public Guid? ResolvedByUserId { get; set; }

    /// <summary>
    /// What actually fixed it. Deliberately free text, and deliberately fed
    /// into the similarity search: the value of finding a similar past incident
    /// is almost entirely in reading how it was resolved.
    /// </summary>
    public string? ResolutionNotes { get; set; }

    /// <summary>The release that most likely caused this, if one lines up in time.</summary>
    public Guid? SuspectedDeploymentId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public MonitoredService MonitoredService { get; set; } = null!;
    public Environment Environment { get; set; } = null!;
    public LogPattern LogPattern { get; set; } = null!;
    public Deployment? SuspectedDeployment { get; set; }
    public ICollection<IncidentEvent> Events { get; set; } = [];
    public ICollection<AiAnalysis> Analyses { get; set; } = [];
}
