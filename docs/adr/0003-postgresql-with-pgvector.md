# 3. PostgreSQL with pgvector as the only datastore

Date: 2026-08-24
Status: Accepted

## Context

IncidentIQ needs three things from storage: relational integrity for the
incident model, a semi-structured column for arbitrary log properties, and
vector similarity search to answer "has this incident happened before?".

The obvious alternative is three systems - PostgreSQL, a document store, and a
dedicated vector database such as Pinecone or Qdrant.

## Decision

One PostgreSQL instance, with the `vector` extension for embeddings, `jsonb` for
semi-structured fields, and `pg_trgm` for fuzzy matching.

## Consequences

- **Transactions across the whole model.** The incident row and its outbox row
  commit together (see ADR 0002's at-least-once consequence). This is not
  possible across two systems.
- **Idempotency is a unique constraint.** `INSERT ... ON CONFLICT DO NOTHING`
  makes duplicate delivery a no-op, enforced by the database rather than by
  application logic that races between replicas.
- **Vectors join to relational data in one query.** "Similar incidents, same
  environment, resolved, last 12 months" is one `SELECT`. With an external
  vector store it is two round trips and a manual join.
- **Volume suits it.** We embed *incidents*, not log lines - thousands per year,
  not millions. Dedicated vector databases earn their keep above roughly 10M
  vectors.
- **Cost:** PostgreSQL is the one component that does not scale by adding
  instances. Mitigations, in order: time-partitioning the occurrence table, read
  replicas for the query API, connection pooling, then vertical scaling.
- **Cost:** raw logs must *not* live here. Only aggregates and sampled
  occurrences are stored; the full stream stays in Kafka and later in object
  storage.
