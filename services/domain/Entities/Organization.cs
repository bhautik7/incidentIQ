using IncidentIQ.Domain.Abstractions;
using IncidentIQ.Domain.Enums;

namespace IncidentIQ.Domain.Entities;

/// <summary>
/// The tenant, and the root of the isolation boundary.
///
/// Every other table in the system carries an <c>OrganizationId</c> pointing
/// here. This is the one entity that does not, because it *is* the tenant.
/// </summary>
public class Organization : IAuditable
{
    public Guid Id { get; set; }

    /// <summary>Human-facing name, e.g. "Acme Corp".</summary>
    public string Name { get; set; } = null!;

    /// <summary>URL-safe identifier, e.g. "acme". Unique across the platform.</summary>
    public string Slug { get; set; } = null!;

    public OrganizationStatus Status { get; set; } = OrganizationStatus.Active;

    /// <summary>How long sampled log events are kept for this organization.</summary>
    public int LogRetentionDays { get; set; } = 90;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<User> Users { get; set; } = [];
    public ICollection<MonitoredService> MonitoredServices { get; set; } = [];
    public ICollection<Environment> Environments { get; set; } = [];
}
