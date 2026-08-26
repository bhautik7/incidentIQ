# 14. Transactional outbox for integration events

Date: 2026-08-26
Status: Accepted

## Context

Several operations must change PostgreSQL *and* announce that change on Kafka -
opening an incident, recording a deployment. There is no way to make a database
commit and a broker publish atomic, so doing both in sequence has two failure
modes, and they are mirror images:

**Commit then publish.** The row is committed, the process dies before the
publish. The incident exists and no consumer will ever hear about it: no AI
analysis, no notification, and nothing in the system knows anything is missing.
Silent, permanent, invisible.

**Publish then commit.** The event goes out, the transaction rolls back. Now an
event announces an incident that does not exist. Consumers act on it, look up
the id, find nothing, and either crash or dead-letter. Loud but wrong.

## Decision

The publish becomes part of the transaction. The domain row and an
`outbox_messages` row commit together; a background publisher forwards the row
to Kafka afterwards and marks it published.

Claiming uses `SELECT ... FOR UPDATE SKIP LOCKED`, and the publish happens
inside the claiming transaction.

## Consequences

- **Atomicity is restored** because there is only one transactional resource.
  Either both rows exist or neither does.
- **`SKIP LOCKED` means any number of replicas can drain concurrently** with no
  leader election and no distributed lock. A replica steps over rows another is
  holding rather than blocking on them.
- **The event id is fixed at write time, not publish time.** A publish that
  succeeded but crashed before the row was marked will be retried, and the
  consumer has to recognise the second copy as the same event. Generating the id
  at publish time would produce two events nothing could tell apart.
- **Payload is `text`, not `jsonb`.** jsonb parses and re-serialises on write, so
  the stored value would not be the bytes that were committed. Nothing is lost:
  every field worth querying is already a column.
- **Publishing inside the claiming transaction** keeps the state machine to two
  states. Claiming, committing, then publishing would add a third - claimed but
  unpublished - which a crash can strand invisibly. The cost is a lock held for
  the length of a batch publish, bounded by `BatchSize` and the producer's
  message timeout.
- **Cost: delivery is at-least-once, not exactly-once.** A crash between a
  successful publish and the `published_at` update republishes. Consumers must
  be idempotent, which they already are (ADR 0013).
- **Cost: added latency** equal to the poll interval, 500 ms by default. Change
  data capture removes it, at the cost of Kafka Connect and logical replication.
  Not worth it at incident volume.
- **Cost: ordering is only guaranteed with a single publisher.** Rows are
  claimed in id order, but two replicas can claim adjacent rows for the same
  aggregate and reach Kafka out of order. Run one publisher replica, or shard
  claims by partition key, if per-aggregate ordering matters.
- **Retries are bounded.** `next_attempt_at` gives exponential backoff with
  jitter; `dead_lettered_at` stops retrying after `MaxAttempts` and turns the
  row into an alert rather than an infinite loop.

## Note on EnableRetryOnFailure

EF refuses user-initiated transactions under a retrying execution strategy. The
outbox is built on user-initiated transactions, so every such caller must go
through `Database.CreateExecutionStrategy()`. `ExecuteInTransactionAsync` wraps
this, because forgetting it is a runtime failure rather than a compile error.
