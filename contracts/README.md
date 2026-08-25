# Event contracts

The canonical wire format for every Kafka message in IncidentIQ, shared by the
.NET services and the Python worker.

`samples/` holds one JSON file per event type. They are **fixtures, not
documentation**: the .NET tests assert that the C# types serialise to exactly
these bytes, and the Python tests assert that the Pydantic models parse them.
A change that breaks cross-language compatibility fails a test in both
languages rather than in production.

## Envelope

Every message on every topic has the same shape:

| Field | Type | Purpose |
|---|---|---|
| `eventId` | uuid | Idempotency key. Generated once, preserved across retries and replays. |
| `eventType` | string | What happened, e.g. `log.received`. Routing without deserialising the payload. |
| `eventVersion` | int | Payload schema version, from 1. |
| `occurredAt` | RFC 3339 | When the thing happened - not when it was published or consumed. |
| `tenantId` | uuid | Owning organization. Present on every event. |
| `correlationId` | uuid | Ties together everything caused by one original action. |
| `payload` | object | The only part that varies by event type. |

Names are camelCase on the wire. C# uses PascalCase and Python uses snake_case;
neither dictates the format, and each maps at the boundary.

## Versioning

- **Additive change** (a new optional field): keep the version. Consumers ignore
  unknown fields, so producers and consumers deploy independently.
- **Breaking change** (removing a field, changing a type, changing a meaning):
  increment `eventVersion`. A consumer that does not understand a version must
  dead-letter the message rather than guess at it.
