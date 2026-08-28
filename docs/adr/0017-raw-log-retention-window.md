# 17. A raw log retention window beside the permanent sample

Date: 2026-08-28
Status: Accepted. Extends ADR 0007, which it does not contradict.

## Context

ADR 0007 decided that `log_events` stores a **capped sample** — twenty rows per
pattern — with the authoritative total kept on `log_patterns.occurrence_count`.
That decision was right and remains in force: it is what turns ~1M events/day
into ~15k rows and ~20MB instead of ~1.2GB, and it is why an LLM call per
incident is affordable.

Building the log explorer made its one blind spot concrete. Measured on the
running system:

| | |
|---|---|
| Rows in `log_events` | 60 (3 patterns × 20) |
| Authoritative event count | 262 |
| Rows carrying a `trace_id` | 0 |

The cap is per pattern and permanent, so **once a pattern reaches twenty samples
it never records another line**. A burst of 4,200 identical errors adds zero
rows. A log explorer built on this table would:

- return the same sixty lines however many million events had gone past,
- show an empty trace column on every row,
- and offer a live tail that emits nothing once patterns saturate.

Each of those is worse than having no log explorer, because each looks like it
is working.

The sample cannot simply be uncapped. Its value is that it is *permanent*:
opening a three-month-old incident and seeing the real lines behind it is what
makes the incident page worth reading, and a retention window applied to the
sample would empty exactly those pages.

## Decision

Add a second table, `raw_log_events`, holding **every line for a bounded
window** (default 48 hours), and keep `log_events` exactly as ADR 0007 defined
it.

The two tiers answer different questions and neither can do the other's job:

| | `log_events` | `raw_log_events` |
|---|---|---|
| Contents | 20 rows per pattern | every line |
| Lifetime | forever | rolling window |
| Grows with | distinct errors | traffic |
| Answers | "show me real lines behind this old incident" | "what happened during this outage" |

Both are written in the same transaction by `LogBatchWriter`, so a line is
either in both or in neither.

### Consequences accepted

**This is the only table that grows with traffic.** That is the cost ADR 0007
avoided, and it is why:

- the index set is the smallest that serves the explorer — one composite btree
  on `(organization_id, occurred_at desc, id desc)` and one partial index on
  `trace_id`;
- retention is a BRIN index on `occurred_at` plus a chunked delete, not a btree.
  The table is append-only and physically ordered by time, so a handful of page
  range summaries answer "older than X" for a rounding error per insert;
- there is no unique index on `event_id`, unlike the sample. Duplicate
  suppression already happened against `processed_events`, so enforcing it again
  on the highest-volume table would tax every insert to prevent something that
  cannot occur.

**Paging is a keyset, not an offset.** A log stream grows while it is read, and
`OFFSET` on a moving stream shifts rows between pages — a line arriving mid-read
pushes another off the end of page one, and it is never seen. The cursor is
`(occurred_at, id)`; the id is not decoration, because two lines can share a
timestamp to the microsecond and a time-only cursor drops one of them.

**No total count is returned.** Counting matching rows means scanning them, and
the answer is stale before it is rendered.

**The window is short, and the UI says so.** 48 hours answers "what happened
during this outage", which is a question about hours. Anything older is served
by patterns and incidents, which are small and kept indefinitely. The explorer
states the retention horizon and the oldest line actually held, so an empty
result reads as "not retained that far back" rather than "nothing happened".

## Alternatives rejected

**Uncap the sample.** Loses the permanent lines behind old incidents, which is
the sample's entire purpose.

**Build the explorer over the sample and label it.** Honest, and useless: it is
not log search, and live tail would be close to pointless.

**Reframe as a pattern explorer.** A good page, and one worth having, but it
answers a different question from the one an engineer has at 03:00.

**Partition the raw table now.** Retention is a `DELETE` by time against a BRIN
index; partitioning adds monthly maintenance and a composite key for a table
that is bounded by construction. Revisit if the window is ever widened to weeks.
