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
    /// The pattern this incident is about, when it is about one.
    ///
    /// Nullable because not every detection rule is pattern-scoped: a spike of
    /// 5xx responses across a service spans many fingerprints and belongs to
    /// none of them.
    /// </summary>
    public Guid? LogPatternId { get; set; }

    /// <summary>
    /// What "the same active problem" means for this incident, and the reason
    /// a burst of 4,200 errors produces one incident rather than 4,200.
    ///
    /// A partial unique index on (organization_id, dedupe_key) over active
    /// statuses makes a second incident for the same key impossible to insert,
    /// so duplicate suppression is enforced by PostgreSQL rather than by a
    /// check-then-insert that two replicas can both pass.
    ///
    /// The key is rule-shaped: "fp:{fingerprint}" for pattern rules,
    /// "svc5xx:{service}:{environment}" for the server-error spike.
    /// </summary>
    public string DedupeKey { get; set; } = null!;

    /// <summary>Which rule opened this, so a noisy rule can be found and tuned.</summary>
    public DetectionRule DetectionRule { get; set; } = DetectionRule.CountThreshold;

    /// <summary>Starts as the normalised message; replaced by the AI-written title.</summary>
    public string Title { get; set; } = null!;

    public IncidentStatus Status { get; set; } = IncidentStatus.Detected;
    public IncidentSeverity Severity { get; set; } = IncidentSeverity.Medium;

    /// <summary>Occurrences attributed to this incident, not rows in LogEvents.</summary>
    public long OccurrenceCount { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>When someone took it: the Detected -> Investigating transition.</summary>
    public DateTimeOffset? InvestigationStartedAt { get; set; }

    public Guid? InvestigatingUserId { get; set; }

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
    public LogPattern? LogPattern { get; set; }
    public Deployment? SuspectedDeployment { get; set; }
    public ICollection<IncidentEvent> Events { get; set; } = [];
    public ICollection<AiAnalysis> Analyses { get; set; } = [];
}
