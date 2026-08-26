# 15. Deterministic rules before anomaly detection

Date: 2026-08-26
Status: Accepted

## Context

Incidents have to be opened automatically. The tempting approach is a model
that learns what "normal" looks like and flags departures from it.

## Decision

Start with four deterministic rules, evaluated in order of specificity:
count threshold, server-error spike, rate spike against the pattern's own
baseline, and a new error shortly after a deployment. No model.

## Consequences

- **The reason is arguable.** An engineer woken at 03:00 is told "412
  occurrences in 5 minutes, threshold is 25" and can say the threshold is
  wrong. "The model scored it 0.87" is not something anyone can act on or
  correct, and an alert nobody trusts is worse than no alert.
- **Rules can be tuned the moment they are wrong**, by changing configuration.
  A model needs labelled data this system does not have, because nobody has
  resolved any incidents in it yet. Anomaly detection is not a better first
  step; it is a step that cannot be taken yet.
- **Rules are testable with no infrastructure**, so every boundary is pinned by
  a test rather than hoped for.
- **The recorded rule makes noise diagnosable.** `detection_rule` on each
  incident means "which rule is paging us too often" is a query.
- **Cost: rules miss what they were not written for.** A gradual degradation
  that never crosses a threshold and never spikes goes unnoticed. That is the
  gap anomaly detection fills, on top of these rules rather than instead of
  them, once there is history to learn from.
- **Cost: thresholds are wrong until tuned**, and the right values differ per
  service. Per-service overrides are the obvious next step.

## The four vocabulary levels

The rules only make sense against a clear separation:

- **Raw error** - one log line. An observation. Millions per day, individually
  meaningless.
- **Repeated error pattern** - a fingerprint. The recurring shape behind many
  raw errors, with a count and a first/last seen. Still not a problem: plenty
  of patterns fire constantly and always have.
- **Anomaly** - a pattern behaving unlike itself. A statement about *change*,
  which needs a baseline to compare against.
- **Incident** - something worth a human's attention, with a lifecycle, an
  owner and a resolution. The only one of the four that is a claim about the
  world rather than about the data.

Detection is the step that turns the middle two into the last one, and the
rules are the explicit statement of when that promotion is justified.

## Duplicate suppression

A partial unique index on `(organization_id, dedupe_key)` over active statuses
makes a second incident for the same problem impossible to insert. Two detector
replicas processing the same burst cannot both open one: the loser folds its
occurrences into the winner's incident.

`dedupe_key` rather than `log_pattern_id`, because not every rule is
pattern-scoped - a server-error spike spans fingerprints and belongs to none of
them.

A recurrence within the cooldown reopens the resolved incident rather than
opening a new one. Without that, a flapping error produces a fresh incident
every few minutes and the incident list becomes exactly the wall of noise the
product exists to remove, one level up.
