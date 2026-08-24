namespace IncidentIQ.Domain.Abstractions;

/// <summary>
/// Every row that belongs to exactly one organization.
///
/// This is the tenant-isolation contract. Persistence applies a global query
/// filter to every entity implementing it, so a query that forgets to mention
/// the organization returns nothing rather than another tenant's data.
/// </summary>
public interface ITenantScoped
{
    Guid OrganizationId { get; }
}

/// <summary>
/// Rows whose creation and last modification times are maintained by the
/// persistence layer rather than by callers, so they cannot be forgotten
/// or back-dated by accident.
/// </summary>
public interface IAuditable
{
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Append-only rows: written once, never updated. They carry a creation time
/// but no <c>UpdatedAt</c>, which is what lets PostgreSQL avoid row rewrites
/// and keeps table bloat low on the highest-volume tables.
/// </summary>
public interface ICreatedAt
{
    DateTimeOffset CreatedAt { get; set; }
}
