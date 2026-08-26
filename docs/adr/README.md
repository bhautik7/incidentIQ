# Architecture Decision Records

One file per decision, numbered and immutable. When a decision changes, add a
new record that supersedes the old one rather than editing history.

| # | Decision | Status |
|---|---|---|
| [0001](0001-record-architecture-decisions.md) | Record architecture decisions | Accepted |
| [0002](0002-kafka-as-the-event-backbone.md) | Kafka as the event backbone | Accepted |
| [0003](0003-postgresql-with-pgvector.md) | PostgreSQL with pgvector as the only datastore | Accepted |
| [0004](0004-python-service-for-ai-workload.md) | A separate Python service for the AI workload | Accepted |
| [0005](0005-split-api-and-ingestion.md) | Split the API and ingestion into two services | Accepted |
| [0006](0006-defer-redis-and-object-storage.md) | Provision Redis and MinIO but do not use them yet | Accepted |
| [0007](0007-log-events-key-and-partitioning.md) | LogEvents: bigint key, sampled rows, no partitioning yet | Accepted |
| [0008](0008-tenant-isolation-strategy.md) | Tenant isolation through composite foreign keys | Accepted |
| [0009](0009-event-envelope-and-partition-keys.md) | A uniform event envelope, and keys that decide ordering | Accepted |
| [0010](0010-ingestion-accepts-partial-batches.md) | Ingestion accepts partial batches | Accepted |
| [0011](0011-ingestion-tenant-resolution.md) | Ingestion resolves tenants without a database | Accepted |
| [0012](0012-log-fingerprinting.md) | Log fingerprinting: normalise, then hash | Accepted |
| [0013](0013-idempotent-batch-processing.md) | At-least-once delivery with idempotent batch processing | Accepted |
| [0014](0014-transactional-outbox.md) | Transactional outbox for integration events | Accepted |
| [0015](0015-deterministic-incident-detection.md) | Deterministic rules before anomaly detection | Accepted |
