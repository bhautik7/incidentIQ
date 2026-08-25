# 12. Log fingerprinting: normalise, then hash

Date: 2026-08-25
Status: Accepted

## Context

A bad deploy produces thousands of error lines that are identical in every way
that matters and different in every way that does not - a user id here, a
timeout value there, a different pod name on each. The product exists to turn
those into one incident, so it needs a stable answer to "are these the same
failure?"

## Decision

Two steps, deliberately separate.

**Normalise** the message by replacing values with type-named placeholders:
`{UUID}`, `{IP}`, `{EMAIL}`, `{URL}`, `{PATH}`, `{HEX}`, `{TIMESTAMP}`, `{NUM}`.

**Fingerprint** by hashing `organizationId | environment | service |
exceptionType | normalisedMessage | topStackFrames` with SHA-256, joined by
ASCII unit separators.

## Consequences

- **Determinism is the contract.** Same failure, same fingerprint, on every
  replica, after every restart, after every deploy. Anything that varies
  between occurrences - timestamps, hosts, trace ids, the raw message - is
  excluded by construction.
- **The two failure modes are not symmetrical, and both are bad.** Masking too
  little splits one incident into thousands and the product stops working.
  Masking too much merges unrelated failures and the product lies. Every rule
  matches a shape that is unambiguously a value, never a word.
- **Placeholders name what was recognised, not what it means.** `{NUM}`, not
  `{USER_ID}`: deciding that 18273 is a user id means guessing intent from
  surrounding words, and a wrong guess splits a pattern.
- **Rule order is load-bearing.** Every broad rule would also match a fragment
  of a narrow one - a UUID contains digits, an IP is four numbers - so broad
  rules run last.
- **Line numbers are excluded from stack frames.** Leaving them in means a
  one-line edit above the throw site produces a new fingerprint, a new
  incident, and a lost history for a failure that never changed.
- **Three frames, not one and not all.** Deep enough to separate two callers of
  the same helper, shallow enough that an unrelated change further down does not
  fork the pattern.
- **Scoped by organization and environment**, so two tenants never share a
  pattern and a staging failure never merges into the production incident that
  woke someone up.
- **Cost:** the rules are heuristics, and a message whose variable part is a
  bare word - `Unknown state: PENDING` versus `Unknown state: FAILED` - is not
  collapsed. Adding a rule changes fingerprints, so it forks existing patterns;
  that is a migration, not a config change.
