# 8. Tenant isolation through composite foreign keys

Date: 2026-08-24
Status: Accepted

## Context

IncidentIQ is multi-tenant on a shared schema. The usual approach is a
discriminator column plus discipline: every table carries `organization_id`, and
every query is expected to filter on it.

Discipline fails. One missing `WHERE` in one query handler, and one customer
reads another's production incidents.

## Decision

Two independent mechanisms, neither of which relies on a developer remembering.

**1. Composite foreign keys.** Every parent table exposes a unique key on
`(organization_id, id)`. Every child references the *pair*:

```sql
FOREIGN KEY (organization_id, monitored_service_id)
    REFERENCES monitored_services (organization_id, id)
```

An Acme incident that points at a Globex service has no matching pair, so
PostgreSQL rejects the insert. Cross-tenant references become unrepresentable
rather than merely discouraged.

**2. EF Core global query filters.** Every entity implementing `ITenantScoped`
gets `WHERE organization_id = @current` automatically. The comparison is against
a `Guid?`, so a context with no organization produces `organization_id = NULL`,
which matches nothing.

## Consequences

- **Fail closed.** An unauthenticated or misrouted request returns an empty
  result. The failure mode is a missing answer, never a leaked one.
- **The two mechanisms fail differently, which is the point.** A query filter can
  be bypassed with `IgnoreQueryFilters()`; a foreign key cannot be bypassed at
  all. Neither one alone would be enough.
- **Cost: one extra unique index per parent table.** EF creates these
  automatically as alternate keys (`ak_*`) when a composite principal key is
  declared - they are not free, but they are cheap and they also serve the
  lookups that filter by organization.
- **Cost: denormalised `organization_id` on child tables** that could derive it
  from a parent. That redundancy is what the composite key checks against, and
  it lets any query filter without a join.
- **Two deliberate exceptions.** `roles` is platform-wide reference data with no
  organization at all; tenant scoping lives on `user_roles`, where the
  assignment happens. `processed_events` has a nullable organization, because a
  message may be deduplicated before its owner is known.
- Cross-tenant background work - the event processor, the outbox publisher -
  sets the tenant per message rather than bypassing the filter, so bypassing
  stays a visible, deliberate act.
