# 6. Provision Redis and MinIO but do not use them yet

Date: 2026-08-24
Status: Accepted

## Context

The Phase 1 design identified real future roles for Redis (caching the
hot-fingerprint lookup, ingest rate limiting, a SignalR backplane) and for
object storage (cold log archive beyond Kafka's 7-day retention). Neither is
needed at Phase 2 volumes.

## Decision

Both run in the local Docker Compose stack. No application service is configured
to use either one.

## Consequences

- Developers discover the intended topology on day one, and adding the first
  cache or the first archiver consumer needs no infrastructure work.
- **PostgreSQL remains the single source of truth.** Introducing a cache before
  there is a measured bottleneck buys a class of cache-invalidation bugs for no
  gain.
- Kafka's 7-day retention plus sampled occurrences in PostgreSQL genuinely cover
  Phase 2 and Phase 3.
- **Cost:** two containers running in local development that nothing talks to,
  and two sets of credentials in `.env` that are currently unused.
- The trigger for Redis is the fingerprint lookup in the event processor: during
  a burst it runs once per log line against the same row. The trigger for object
  storage is the first request for data older than seven days.
