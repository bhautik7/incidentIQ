# 13. At-least-once delivery with idempotent batch processing

Date: 2026-08-25
Status: Accepted

## Context

Kafka delivers at least once. Duplicates are not an edge case - they are the
normal consequence of a rebalance, a crash between the database commit and the
offset commit, or a dead-letter replay.

Exactly-once delivery is available through Kafka transactions, at a cost in
throughput and operational complexity, and it still does not make a database
write idempotent on its own.

## Decision

Accept at-least-once delivery and make the processing idempotent. Three layers,
each covering what the others cannot.

1. **`processed_events`**, keyed by `(consumer_group, event_id)`. One read per
   batch filters out redeliveries *before* any work is done.
2. **In-batch deduplication** by logical event id, because a client that
   retried into the same poll window puts the same event in one batch twice.
3. **`UNIQUE (organization_id, event_id)` on `log_events`** with
   `ON CONFLICT DO NOTHING` - the durable backstop that survives
   `processed_events` being pruned or a consumer group being renamed.

## Consequences

- **The counter is the sharp end.** `log_patterns.occurrence_count` is
  incremented by the number of *new* events, computed after layers 1 and 2. A
  replayed batch adds nothing. An incident claiming 8,400 occurrences when
  there were 4,200 destroys confidence in everything else on the page.
- **Deduplication must not swallow real occurrences.** Distinct events sharing
  a fingerprint all count; only a repeat of the same logical event is dropped.
  Both directions are tested.
- **The idempotency key is the client's log event id**, not the Kafka message
  id. That covers both a Kafka redelivery and a client's own HTTP retry, which
  arrive as different messages carrying the same logical event.
- **`processed_events` expires** after the Kafka retention window, since once
  redelivery is impossible the record is dead weight - and this table would
  otherwise grow at the rate of the log stream.
- **`logs.normalized` is published for every valid event, including
  redeliveries.** Publishing a duplicate costs a downstream consumer one
  idempotent no-op, which it must handle anyway; not publishing loses an event
  downstream permanently. The asymmetry decides it.
- **Cost:** one extra read and one extra write per batch. At batch granularity
  that is two round trips regardless of batch size.
- **Cost:** a client that omits its event id gets a server-generated one and
  forfeits idempotency across its own HTTP retries. The API accepts this rather
  than rejecting the batch, and the trade is documented.
