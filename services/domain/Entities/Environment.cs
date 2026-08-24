using IncidentIQ.Domain.Abstractions;

namespace IncidentIQ.Domain.Entities;

/// <summary>
/// A deployment target: production, staging, development.
///
/// Modelled at the organization level rather than under each service, because
/// "production" means the same thing across every service an organization runs.
/// That makes "show me everything currently broken in production" a single
/// predicate instead of a join across per-service environment rows.
///
/// A <see cref="MonitoredService"/> is *what* runs; an Environment is *where*;
/// a <see cref="Deployment"/> is a specific version of a what, in a where, at a
/// point in time.
/// </summary>
public class Environment : ITenantScoped, IAuditable
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>Machine name as it appears in log payloads, e.g. "production".</summary>
    public string Key { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    /// <summary>
    /// Production incidents outrank staging ones by default. Kept as a number
    /// so an organization can add "canary" between staging and production
    /// without a schema change.
    /// </summary>
    public int Rank { get; set; }

    public bool IsProduction { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Organization Organization { get; set; } = null!;
}
