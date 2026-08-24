using IncidentIQ.Domain.Abstractions;
using IncidentIQ.Domain.Enums;

namespace IncidentIQ.Domain.Entities;

/// <summary>
/// One entry in an incident's timeline: created, escalated, acknowledged,
/// commented on, resolved.
///
/// Append-only and never updated. Where <see cref="Incident"/> answers "what is
/// true now?", this answers "how did it get here?" - which is what an
/// engineer joining a live incident actually needs to read.
///
/// Distinct from <see cref="AuditLog"/>: this is a product feature scoped to
/// one incident and shown in the UI, whereas AuditLog is a security and
/// compliance record covering every entity in the system.
/// </summary>
public class IncidentEvent : ITenantScoped
{
    /// <summary>bigint: reached only through its parent incident, never referenced externally.</summary>
    public long Id { get; set; }

    public Guid OrganizationId { get; set; }
    public Guid IncidentId { get; set; }

    public IncidentEventType Type { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public ActorType ActorType { get; set; } = ActorType.System;

    /// <summary>Null when the pipeline acted rather than a person.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>Human-readable line for the timeline, e.g. a comment body.</summary>
    public string? Message { get; set; }

    /// <summary>Type-specific detail as jsonb, e.g. {"from":"Medium","to":"Critical"}.</summary>
    public string? Data { get; set; }

    public Incident Incident { get; set; } = null!;
    public User? ActorUser { get; set; }
}
