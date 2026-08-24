# IncidentIQ - system design

> Phase 1 design document. Phase 2 implements the foundation; the pipeline
> described here is built in Phase 3.

## What it does

IncidentIQ turns a flood of application logs into a small number of *explained
incidents*.

A worked example. At 02:14 `payments-api` is deployed; a `DbContext` lifetime
change exhausts the connection pool. Within three minutes six pods emit ~4,200
error lines, and `orders-api` starts logging 502s downstream.

IncidentIQ:

1. Normalises each message, masking GUIDs, numbers, IPs and other variable parts.
2. Fingerprints it: `SHA256(tenant | environment | service | exceptionType | normalisedMessage | topStackFrames)`.
3. Collapses all 4,200 lines into **one incident** with `occurrenceCount = 4200`,
   `firstSeen`/`lastSeen`, two affected services and a handful of sampled payloads.
4. Embeds the incident signature and finds a 91%-similar incident from four
   months ago, already resolved.
5. Asks an LLM for a summary, probable cause and suggested checks.
6. Shows one card on the dashboard instead of 4,200 log lines.

It is not a log search engine, an APM, or a pager.

## Event journey

```
application log
  -> HTTP POST /api/v1/logs/batch      (ingestion; returns 202 in ~5 ms)
  -> Kafka topic logs.raw              (key: tenant:environment:service)
  -> event processor                   (normalise -> fingerprint -> correlate)
  -> PostgreSQL                        (incident + outbox row, one transaction)
  -> Kafka topic incidents.created     (published by the outbox publisher)
  -> AI worker                         (embed -> pgvector kNN -> LLM)
  -> PostgreSQL                        (incident_enrichment)
  -> query API -> dashboard
```

End to end: 5-15 seconds, dominated by the LLM call.

## Kafka topology

| Topic | Partitions | Retention | Key |
|---|---|---|---|
| `logs.raw` | 3 | 7 days | `tenant:environment:service` |
| `incidents.created` | 3 | 7 days | `incidentId` |
| `logs.raw.dlq` | 1 | 30 days | preserved from the original |
| `incidents.created.dlq` | 1 | 30 days | preserved from the original |

**Partition keys.** Keying `logs.raw` by service means every log line for one
service lands on one partition and therefore one consumer instance. That is what
makes correlation correct: two processor replicas can never race to create two
incidents for the same fingerprint. Keying `incidents.created` by `incidentId`
keeps one incident's lifecycle events ordered.

Three partitions, not one, because raising the partition count later rehashes
keys and breaks per-key ordering for data already in the topic.

**Consumer groups.** One per logical job, never per instance:
`incident-processor` on `logs.raw`, `ai-enricher` on `incidents.created`, and
later `log-archiver` and `notifier`. Each group has independent offsets, so
adding one costs existing consumers nothing.

## Reliability

**Retry.** Classify first. Transient failures (DB timeout, deadlock, LLM 429)
get bounded exponential backoff with jitter - 3 attempts in the processor, 4 in
the AI worker - retrying the individual message, not the whole batch. Permanent
failures (malformed JSON, unknown schema) go straight to the DLQ; retrying them
burns the partition forever.

Infrastructure-wide failures are the exception: if PostgreSQL is unreachable,
the consumer pauses and keeps retrying. Dead-lettering a million messages during
a ten-minute outage turns a small outage into a large one.

**Dead-letter topics.** `{topic}.dlq`, carrying the original bytes unmodified so
replay is byte-identical, with diagnostics in headers (original topic, partition,
offset, failure reason, attempt count, app version). Never auto-replayed - replay
is a deliberate operator action after the bug is fixed. Any message in a DLQ
should raise an alert; in a healthy system these topics are empty.

**Idempotency.** Kafka is at-least-once, so every consumer sees duplicates.
Four layers:

1. The client generates `eventId` once and reuses it across its own retries.
2. `UNIQUE (tenant_id, event_id)` with `ON CONFLICT DO NOTHING` - the database,
   not application logic, decides what is a duplicate.
3. Commit the database transaction *first*, then Kafka offsets. A crash between
   the two replays the batch, which layer 2 makes a no-op.
4. `occurrence_count` is incremented only inside the transaction that
   successfully inserted an occurrence, so a replay cannot inflate counts.

Producers set `enable.idempotence=true` and `acks=all`.

**Transactional outbox.** A database commit and a Kafka publish cannot be made
atomic, so the publish becomes part of the transaction: the incident row and an
`outbox_message` row commit together. A background publisher polls with
`FOR UPDATE SKIP LOCKED` - which lets several processor replicas run the
publisher safely without leader election - publishes, and marks the row sent. A
crash between publish and mark republishes, which consumer idempotency absorbs.

## Data model (Phase 3)

`tenant`, `api_key`, `service`, `app_user`, `incident`, `incident_occurrence`
(sampled, time-partitioned), `incident_metric`, `incident_enrichment`
(`vector(1536)`, HNSW index), `outbox_message`.

Note what is absent: there is no table holding all 4,200 raw log lines. Raw logs
live in Kafka (7 days) and later object storage; PostgreSQL holds incidents,
counters and a capped sample of occurrences. That single decision is what keeps
PostgreSQL viable at 1M+ events per day - roughly 15,000 rows a day instead of
1,000,000, and a few hundred LLM calls instead of an impossible number.

## Failure behaviour

| Down | Effect | Data loss |
|---|---|---|
| Ingestion instance | Load balancer routes elsewhere; clients buffer and retry | None |
| Event processor | `logs.raw` lag grows; dashboard goes stale | None |
| AI worker | Incidents appear without summaries - degraded, not broken | None |
| PostgreSQL | Consumers pause and retry; ingestion keeps accepting | None |
| Query API | Dashboard down; ingestion and processing continue | None |
| Kafka | **Ingestion stops** - the one hard dependency | None if clients buffer |

The pattern: everything downstream of Kafka degrades into "delayed", not "lost".

## Scaling

Every service is stateless; all shared state is in Kafka (partitioned) or
PostgreSQL (transactional). Scaling is `replicas: N`, bounded by partition count
for the consumers. PostgreSQL is the exception and is handled by partitioning,
read replicas, pooling and vertical scaling, in that order.

Throughput comes from aggregating early: a batch of 1,000 messages sharing a
fingerprint collapses into one `UPDATE`, not 1,000 inserts.

## Not built yet

Retry topics, log search, alerting integrations, real-time push, Kafka Streams,
CDC, auto-remediation, custom ML models, multi-region, Kubernetes, CQRS/event
sourcing, GraphQL.

Every one of these is *additive* - a new consumer group, a cache in front of an
existing read, a new index. None requires re-architecting the foundation. That
is the test of whether an architecture is correctly sized.
