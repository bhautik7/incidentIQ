namespace IncidentIQ.Domain.Entities;

/// <summary>
/// A named set of permissions.
///
/// Deliberately the one table with no <c>OrganizationId</c>. Roles are
/// platform-defined and identical for everyone, so per-tenant copies would be
/// duplication with no benefit and one more thing to keep in sync. Tenant
/// scoping lives on <see cref="UserRole"/>, where the *assignment* happens.
///
/// If organization-defined custom roles are ever needed, this table gains a
/// nullable OrganizationId and the seeded rows keep it null.
/// </summary>
public class Role
{
    public Guid Id { get; set; }

    /// <summary>Stable machine name, e.g. "Admin". Unique platform-wide.</summary>
    public string Name { get; set; } = null!;

    public string Description { get; set; } = null!;

    /// <summary>System roles are seeded and cannot be deleted through the API.</summary>
    public bool IsSystemRole { get; set; } = true;

    public ICollection<UserRole> UserRoles { get; set; } = [];
}
