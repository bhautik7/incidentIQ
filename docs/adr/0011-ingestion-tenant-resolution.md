# 11. Ingestion resolves tenants without a database

Date: 2026-08-25
Status: Accepted

## Context

Every ingestion request must be attributed to an organization before it is
published, and an unrecognised key must be rejected. The obvious implementation
is a lookup against the `api_keys` table.

ADR 0005 established that the ingestion service has no database connection, on
the grounds that a log burst must not be able to make PostgreSQL the bottleneck
on the write path - and that PostgreSQL being down must not stop log intake.

## Decision

Tenant resolution goes through an `IApiKeyResolver` interface. The current
implementation reads SHA-256 key hashes from configuration.

The interface is the decision; this implementation is a starting point.

## Consequences

- **The request path performs no I/O beyond Kafka.** Authentication is a
  dictionary lookup.
- **The invariant survives.** Ingestion still has no connection string, so it
  cannot be slowed by, or fail with, the database.
- **Keys are never stored in plaintext**, and the 401 response does not
  distinguish an unknown key from a disabled one - both would help someone
  probing for valid keys.
- **Cost:** adding or revoking a key requires a configuration change and a
  restart. Acceptable at current scale, not acceptable once customers
  self-serve.
- **The replacement is already understood:** load the `api_keys` table into an
  in-memory snapshot at startup and refresh it in the background. The request
  path still performs no query, and a database outage degrades to a stale
  snapshot rather than an outage. That change touches one class.
- **Trigger:** the first time a key needs revoking faster than a deploy, or the
  first self-service key.
