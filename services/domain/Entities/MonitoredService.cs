using IncidentIQ.Domain.Abstractions;

namespace IncidentIQ.Domain.Entities;

/// <summary>
/// An application being watched, e.g. "payments-api".
///
/// This is the *logical* service, independent of where it runs. The same
/// payments-api exists in production and in staging; that is one
/// MonitoredService and two <see cref="Environment"/>s, not two services.
/// Keeping them separate is what makes "this error only happens in production"
/// expressible.
/// </summary>
public class MonitoredService : ITenantScoped, IAuditable
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>
    /// Machine name as it appears in log payloads, e.g. "payments-api".
    /// Unique per organization; this is what ingestion looks up.
    /// </summary>
    public string Key { get; set; } = null!;

    public string DisplayName { get; set; } = null!;
    public string? Description { get; set; }

    /// <summary>Owning team, used for routing and filtering.</summary>
    public string? OwnerTeam { get; set; }

    /// <summary>Decommissioned services stop producing incidents but keep their history.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Organization Organization { get; set; } = null!;
    public ICollection<Deployment> Deployments { get; set; } = [];
    public ICollection<LogPattern> LogPatterns { get; set; } = [];
}
