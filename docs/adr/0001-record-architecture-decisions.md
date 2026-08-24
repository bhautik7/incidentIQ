# 1. Record architecture decisions

Date: 2026-08-24
Status: Accepted

## Context

IncidentIQ's architecture has several choices that look arbitrary from the code
alone - three .NET services rather than one, a partition key of
`tenant:environment:service`, an outbox table nobody reads yet. Six months from
now the reasoning will be gone, and someone will "simplify" a decision that was
load-bearing.

## Decision

We record every significant architectural decision as a numbered Markdown file
in `docs/adr/`, following Michael Nygard's format: context, decision,
consequences.

Records are immutable. A decision that changes gets a new record that marks the
old one superseded.

## Consequences

- A reviewer can see *why*, not just *what*.
- Reversing a decision requires articulating what changed, which filters out
  churn.
- It costs about ten minutes per decision, and only decisions worth arguing
  about get a record.
