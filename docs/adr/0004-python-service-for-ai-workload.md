# 4. A separate Python service for the AI workload

Date: 2026-08-24
Status: Accepted

## Context

Incident enrichment means generating an embedding, running a similarity search,
and calling an LLM. .NET can do all of this. The question is whether it should
run in the same process as event processing.

## Decision

The AI workload runs as a separate Python FastAPI service. It communicates with
the .NET side only through Kafka and PostgreSQL; neither imports the other.

## Consequences

The two workloads have incompatible operational profiles:

| | .NET event processor | Python AI worker |
|---|---|---|
| Latency per unit | 1-5 ms | 0.5-20 s |
| Throughput | 10k+ msgs/sec | 1-10 incidents/sec |
| Failure mode | DB timeout | rate limit, token limit, model outage |
| Scaling driver | log volume | incident count (~1000x smaller) |
| Deploy cadence | stable | frequent (prompts, models) |

- Sharing a process would let one 20-second LLM call stall a Kafka partition
  carrying thousands of log events.
- The embedding/LLM ecosystem is native to Python and lags in .NET.
- Prompt iteration is a daily activity; it must not require redeploying the
  ingestion-critical service.
- **Cost:** two toolchains, two dependency sets, two CI paths.
- **Cost:** shared contracts (event schemas, table shapes) are not compile-time
  checked across the boundary and need tests.
