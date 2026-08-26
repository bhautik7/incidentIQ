# 16. Gather evidence deterministically before involving an LLM

Date: 2026-08-26
Status: Accepted

## Context

The product promise is an explained incident. The reflex is to send a stack
trace to an LLM and ask what went wrong.

## Decision

Build the retrieval and correlation layers first. The analysis worker gathers
patterns, the correlated deployment, an anomaly score and similar past
incidents, ranks root-cause candidates by fixed arithmetic, and writes the
result. No LLM.

## Consequences

- **Most of the value is retrieval, not language.** "Release 2.31.0 shipped two
  minutes before the first occurrence" resolves a large share of real
  regressions, and it is a timestamp comparison. Asking a model to infer that
  from a stack trace is asking it to guess at something already in the database.
- **Embeddings are not an LLM.** A bi-encoder is a small deterministic function
  from text to a vector. It is what catches "the connection pool has been
  exhausted" and "could not acquire a database connection before the timeout
  elapsed" as the same failure - no shared fingerprint, almost no shared
  vocabulary, 74% cosine similarity. Fingerprinting misses that by design and
  full-text search misses it too.
- **Statistics separate a pattern from an anomaly.** 200 failures a minute is
  not a finding if it has always been 200 a minute. A robust z-score on median
  and MAD is used rather than mean and standard deviation, because the spike
  being measured drags its own mean upward and quietly makes large spikes look
  ordinary.
- **The confidence numbers are arguable.** Every weight is arithmetic someone
  can read, disagree with and change. That matters more than accuracy while
  there is no labelled data: nobody has resolved an incident in this system
  yet, so there is nothing to learn better weights from.
- **When an LLM does arrive, it summarises rather than investigates.** Those are
  different tasks with different failure rates. The evidence assembled here is
  its input, and the template summary is the baseline it has to beat.
- **Cost: a local model means a large image.** torch CPU plus baked-in weights
  is roughly 1.6GB. The alternative - an embedding API - adds a network
  dependency, a per-call cost and a data-egress question to the hot path of an
  incident tool.
- **Cost: templated summaries read as templated.** They are honest about what
  was measured, and deliberately so, but they are not prose.

## Model and dimensions

all-MiniLM-L6-v2, 384 dimensions. The `vector(N)` column, the model and the
configured dimension must agree exactly; a mismatch surfaces as an opaque
pgvector error at insert time, so the worker checks at startup and the
`/diagnostics/model` endpoint reports all three.
