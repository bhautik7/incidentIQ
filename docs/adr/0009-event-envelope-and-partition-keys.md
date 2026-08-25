# 9. A uniform event envelope, and keys that decide ordering

Date: 2026-08-24
Status: Accepted

## Context

Seven topics, five processes, two languages. Two choices had to be made once,
because changing either later means a coordinated redeploy of everything.

## Decision

**One envelope for every message on every topic:** `eventId`, `eventType`,
`eventVersion`, `occurredAt`, `tenantId`, `correlationId`, `payload`. JSON,
camelCase on the wire, with the C# types in `services/contracts` and the
Pydantic models in `workers/ai-analysis/app/contracts.py` both tested against
the same fixtures in `contracts/samples/`.

**Partition keys chosen for ordering, not for balance:**

| Topics | Key |
|---|---|
| `logs.raw`, `logs.normalized`, `logs.failed`, `deployments.created` | `{tenantId}:{service}` |
| `incidents.detected`, `incidents.analysis.*` | `{tenantId}:{incidentId}` |

## Consequences

**The envelope earns its place by making cross-cutting work possible once.**
`eventId` is the idempotency key every consumer dedupes on. `correlationId`
turns five processes into one traceable request. `tenantId` on every message
lets a consumer set its tenant scope before touching the database, and makes a
mis-routed message detectable. `eventVersion` is what lets a consumer that does
not understand a payload dead-letter it instead of guessing.

**The service key is what makes incident correlation correct.** All log events
for one service reach one consumer instance, in order, so two processor replicas
can never race to open two incidents for the same fingerprint. Balance is a
secondary concern; correctness is not.

**Why the tenant is in the key even though a service id would be unique.**
Ingestion works with the client's *name* for a service - `payments-api` - and
names are only unique within an organization. Resolving a name to an id would
put a database lookup on the write path, which is precisely what ingestion is
designed to avoid. A key that reads `acme:payments-api` is also one a human can
act on when inspecting a lagging partition.

**Accepted risk: hot partitions.** One very noisy service pins one partition.
Kafka absorbs the burst and the symptom is recoverable lag, not loss. The escape
hatch is `PartitionKeys.ForShardedService`, which spreads a service over N
sub-keys while keeping each *fingerprint* on one of them - so all occurrences of
one error still meet on one partition. It is written, tested and deliberately
unused: reaching for it before a partition has been measured as the bottleneck
would trade a real guarantee for an imagined problem.

**Cost: JSON, not Avro or Protobuf.** Bigger on the wire and no registry
enforcing compatibility. In exchange, a message is readable with
`kafka-console-consumer` during an incident, and neither language needs a code
generation step. Compression recovers most of the size difference. If schema
drift becomes a real problem rather than a theoretical one, the envelope is
already versioned and a registry can be introduced per topic.
