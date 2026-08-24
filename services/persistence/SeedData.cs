using IncidentIQ.Domain.Entities;
using IncidentIQ.Domain.Enums;
using Environment = IncidentIQ.Domain.Entities.Environment;

namespace IncidentIQ.Persistence;

/// <summary>
/// Fixed identifiers for the roles every installation has, and for the
/// development dataset.
///
/// The ids are constants rather than generated so that re-seeding is
/// idempotent and so integration tests can assert against known values.
/// </summary>
public static class SeedIds
{
    public static class Roles
    {
        public static readonly Guid Owner = new("00000000-0000-0000-0000-0000000000a1");
        public static readonly Guid Admin = new("00000000-0000-0000-0000-0000000000a2");
        public static readonly Guid Responder = new("00000000-0000-0000-0000-0000000000a3");
        public static readonly Guid Viewer = new("00000000-0000-0000-0000-0000000000a4");
    }

    /// <summary>Primary development organization.</summary>
    public static class Acme
    {
        public static readonly Guid OrganizationId = new("11111111-1111-1111-1111-111111111111");
        public static readonly Guid OwnerUserId = new("11111111-0000-0000-0000-000000000001");
        public static readonly Guid ResponderUserId = new("11111111-0000-0000-0000-000000000002");
        public static readonly Guid PaymentsApiId = new("11111111-0000-0000-0000-0000000000a1");
        public static readonly Guid OrdersApiId = new("11111111-0000-0000-0000-0000000000a2");
        public static readonly Guid ProductionId = new("11111111-0000-0000-0000-0000000000b1");
        public static readonly Guid StagingId = new("11111111-0000-0000-0000-0000000000b2");
        public static readonly Guid DeploymentId = new("11111111-0000-0000-0000-0000000000c1");
        public static readonly Guid PoolPatternId = new("11111111-0000-0000-0000-0000000000d1");
        public static readonly Guid TimeoutPatternId = new("11111111-0000-0000-0000-0000000000d2");
        public static readonly Guid IncidentId = new("11111111-0000-0000-0000-0000000000e1");
    }

    /// <summary>
    /// A second organization that exists purely so tenant isolation is
    /// observable in development: it has its own service, pattern and incident,
    /// and Acme must never see any of it.
    /// </summary>
    public static class Globex
    {
        public static readonly Guid OrganizationId = new("22222222-2222-2222-2222-222222222222");
        public static readonly Guid OwnerUserId = new("22222222-0000-0000-0000-000000000001");
        public static readonly Guid ShippingApiId = new("22222222-0000-0000-0000-0000000000a1");
        public static readonly Guid ProductionId = new("22222222-0000-0000-0000-0000000000b1");
        public static readonly Guid PatternId = new("22222222-0000-0000-0000-0000000000d1");
        public static readonly Guid IncidentId = new("22222222-0000-0000-0000-0000000000e1");
    }
}

public static class SeedData
{
    /// <summary>
    /// Platform roles. Applied in every environment including production -
    /// these are reference data, not sample data.
    /// </summary>
    public static IReadOnlyList<Role> SystemRoles() =>
    [
        new() { Id = SeedIds.Roles.Owner, Name = "Owner", Description = "Full control, including billing and deletion.", IsSystemRole = true },
        new() { Id = SeedIds.Roles.Admin, Name = "Admin", Description = "Manage services, environments and users.", IsSystemRole = true },
        new() { Id = SeedIds.Roles.Responder, Name = "Responder", Description = "Acknowledge and resolve incidents.", IsSystemRole = true },
        new() { Id = SeedIds.Roles.Viewer, Name = "Viewer", Description = "Read-only access to incidents and dashboards.", IsSystemRole = true }
    ];
}
