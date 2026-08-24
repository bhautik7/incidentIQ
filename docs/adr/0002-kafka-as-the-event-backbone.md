# 2. Kafka as the event backbone

Date: 2026-08-24
Status: Accepted

## Context

A single bad deploy produces thousands of near-identical log lines in seconds -
roughly 100x the steady-state rate. Ingestion must absorb that burst without
losing data and without slowing down the application being monitored.

Writing straight to PostgreSQL would make the database the bottleneck at exactly
the moment the platform is most needed, and would lose in-flight data on a
restart.

## Decision

All ingested events go to Kafka first. Every downstream stage is a consumer
group with its own offsets.

## Consequences

- **Burst absorption.** Producers write at burst speed; consumers drain at their
  own. A slow consumer causes lag, which is recoverable, rather than data loss.
- **Replay.** Fixing a fingerprinting bug means resetting a consumer group
  offset and reprocessing a week of data. This is the single strongest argument
  for Kafka over a plain queue, which cannot replay after acknowledgement.
- **Fan-out.** Adding the log archiver or a notifier later is a new consumer
  group, with no change to the producer and no cost to existing consumers.
- **Ordered parallelism.** Partitions give N-way parallelism while preserving
  per-key ordering, which a plain queue cannot do.
- **Cost:** Kafka is the one hard dependency of the ingestion path. If Kafka is
  down, ingestion stops. Clients are expected to buffer locally.
- **Cost:** at-least-once delivery, so every consumer must be idempotent. See
  ADR 0003 for how the database enforces that.
