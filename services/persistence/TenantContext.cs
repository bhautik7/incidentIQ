namespace IncidentIQ.Persistence;

/// <summary>
/// The organization whose data the current unit of work may see.
/// </summary>
public interface ITenantContext
{
    /// <summary>
    /// Null when no organization has been established. The global query filters
    /// then match nothing, so an unauthenticated or misrouted request returns an
    /// empty result rather than another tenant's rows - the failure mode is a
    /// missing answer, never a leaked one.
    /// </summary>
    Guid? OrganizationId { get; }
}

/// <summary>
/// Mutable tenant context, set once per request by authentication middleware or
/// per message by a consumer.
///
/// Background services that legitimately work across all organizations - the
/// event processor, the outbox publisher - should set this per message rather
/// than reaching for <c>IgnoreQueryFilters()</c>, so the filter stays the
/// default and bypassing it stays a visible, deliberate act.
/// </summary>
public sealed class AmbientTenantContext : ITenantContext
{
    public Guid? OrganizationId { get; private set; }

    public void SetOrganization(Guid organizationId) => OrganizationId = organizationId;

    public void Clear() => OrganizationId = null;
}

/// <summary>
/// Fixed tenant context, used by tests and by tools that operate on one
/// organization for their whole lifetime.
/// </summary>
public sealed class StaticTenantContext(Guid? organizationId) : ITenantContext
{
    public Guid? OrganizationId { get; } = organizationId;
}
