using IncidentIQ.Domain.Abstractions;
using IncidentIQ.Domain.Enums;

namespace IncidentIQ.Domain.Entities;

/// <summary>
/// Who did what, to which record, when.
///
/// Distinct from <see cref="IncidentEvent"/>. IncidentEvent is a product
/// feature: one incident's timeline, rendered in the UI, covering only
/// incidents. AuditLog is a security and compliance record: every entity,
/// including logins, role grants, service configuration and deletions - the
/// things nobody looks at until they have to.
///
/// Append-only, and never exposed for editing.
/// </summary>
public class AuditLog : ITenantScoped
{
    public long Id { get; set; }

    public Guid OrganizationId { get; set; }

    public ActorType ActorType { get; set; } = ActorType.User;

    /// <summary>Null for system actions.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>Dotted verb, e.g. "incident.resolved" or "user.role_granted".</summary>
    public string Action { get; set; } = null!;

    /// <summary>e.g. "Incident".</summary>
    public string EntityType { get; set; } = null!;

    /// <summary>Text, because the entities audited here use both UUID and bigint keys.</summary>
    public string EntityId { get; set; } = null!;

    /// <summary>jsonb before/after diff, limited to the fields that changed.</summary>
    public string? Changes { get; set; }

    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public User? ActorUser { get; set; }
}
