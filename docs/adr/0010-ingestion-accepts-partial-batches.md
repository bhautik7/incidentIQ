# 10. Ingestion accepts partial batches

Date: 2026-08-25
Status: Accepted

## Context

`POST /api/v1/logs/batch` takes up to 500 events. Some of them will be
malformed - a missing service name, a severity the client invented, a timestamp
from a machine whose clock is a year out. The question is what to do with the
other 499.

All-or-nothing rejection is the simpler rule and the more common instinct.

## Decision

Validate every event independently. Publish the valid ones, return 202, and
report the rejected ones by index and field. Return 400 only when *every* event
in the batch failed.

## Consequences

- **One bad event does not cost a client 499 good ones.** With all-or-nothing,
  a client whose agent occasionally emits a 9,000-character message loses whole
  batches and usually never finds out why.
- **Retries do not amplify the problem.** A client that retries a wholly
  rejected batch retries it forever; a client told exactly which two events were
  bad can drop them and move on.
- **Errors are actionable.** The index locates the event in the array the client
  sent, and the field names what to fix. "400 Bad Request" locates nothing.
- **Cost:** a 202 no longer means "everything was accepted", so a client that
  ignores the response body will silently lose events. The `rejected` count is
  first-class in the response for exactly that reason, and clients are expected
  to alert on it.
- **Cost:** the endpoint has two success shapes to test rather than one.

## Related

The batch size limit is what makes this bounded: without it, one request could
produce an arbitrarily long error list. Validation runs before any publish, so a
wholly invalid batch leaves no trace on `logs.raw` - covered by a test against a
real broker.
