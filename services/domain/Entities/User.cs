using IncidentIQ.Domain.Abstractions;
using IncidentIQ.Domain.Enums;

namespace IncidentIQ.Domain.Entities;

/// <summary>
/// A human who signs in to IncidentIQ.
///
/// Not to be confused with <see cref="MonitoredService"/>: a User is a person
/// who reads incidents, a MonitoredService is an application that produces the
/// logs those incidents are built from.
///
/// A user belongs to exactly one organization. Someone who works with two
/// organizations gets two user records - which keeps the tenant boundary a
/// simple foreign key rather than a membership graph.
/// </summary>
public class User : ITenantScoped, IAuditable
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>Stored already lower-cased; uniqueness is per organization.</summary>
    public string Email { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    /// <summary>Null for users who authenticate through an external provider.</summary>
    public string? PasswordHash { get; set; }

    public UserStatus Status { get; set; } = UserStatus.Invited;

    public DateTimeOffset? LastLoginAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Organization Organization { get; set; } = null!;
    public ICollection<UserRole> UserRoles { get; set; } = [];
}
