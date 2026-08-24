using IncidentIQ.Domain.Abstractions;

namespace IncidentIQ.Domain.Entities;

/// <summary>
/// Assignment of a <see cref="Role"/> to a <see cref="User"/>.
///
/// Carries <c>OrganizationId</c> even though it is derivable from the user.
/// That denormalisation is what lets a composite foreign key
/// (OrganizationId, UserId) make it structurally impossible to grant a role to
/// a user in a different organization.
/// </summary>
public class UserRole : ITenantScoped
{
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }

    public DateTimeOffset AssignedAt { get; set; }

    /// <summary>Null when the assignment was made by the system (e.g. seeding).</summary>
    public Guid? AssignedByUserId { get; set; }

    public User User { get; set; } = null!;
    public Role Role { get; set; } = null!;
}
