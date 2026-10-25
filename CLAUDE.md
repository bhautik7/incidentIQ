# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

IncidentIQ groups repeated application errors into incidents and explains them. Pipeline:

```
HTTP -> Kafka (logs.raw) -> normalise + fingerprint -> detection rules -> incident
     -> outbox -> Python worker -> embedding -> pgvector -> LLM -> API -> React
```

Polyglot monorepo: .NET 10 services (`services/`), a Python 3.12 AI worker (`workers/ai-analysis`), and a React 19 + Vite + Tailwind dashboard (`apps/web`). Design rationale lives in `docs/adr/` (17 ADRs) - read the relevant one before changing fingerprinting (0012), outbox (0014), detection (0015) or the LLM path (0016).

## Commands

```bash
make up                 # build + start whole stack in Docker (copies .env.example -> .env), then creates topics/bucket
make health             # curl every service's health endpoints
make verify             # dotnet build + dotnet test + web build (no Docker needed except Testcontainers tests)
make help               # all targets

dotnet build IncidentIQ.slnx
dotnet test IncidentIQ.slnx
dotnet test tests/IncidentIQ.Api.Tests/IncidentIQ.Api.Tests.csproj --filter "FullyQualifiedName~SomeTest"   # single test

cd workers/ai-analysis
python3 -m venv .venv && .venv/bin/pip install -r requirements-dev.txt
.venv/bin/ruff check app tests && .venv/bin/python -m pytest -q          # make ai-test
.venv/bin/python -m pytest -q tests/test_contracts.py::test_name         # single test

cd apps/web
npm ci && npm run dev        # :5173
npx tsc -b --noEmit && npm run lint   # tsc and oxlint gate the frontend
```

Database (EF Core, `services/persistence`): `make db-add-migration NAME=X`, `make db-migrate`, `make db-script`, `make db-reset`.
Contracts: `make contracts-samples` regenerates `contracts/samples/*.json`; `make contracts-test` runs both languages' tests.

Integration tests use Testcontainers (real PostgreSQL and Kafka), so Docker must be running for them. Test helper scripts: `scripts/send-logs.sh`, `scripts/upload-log.py`, `scripts/show-analysis.sh`, `scripts/record-deployment.sh`.

After a frontend change, rebuild the web image (`docker compose -f infrastructure/docker/docker-compose.yml up -d --build web`) or port 3000 serves the old build.

## Architecture

- **ingestion** validates a batch, writes to Kafka, returns 202 without touching the DB (partial batches accepted, ADR 0010).
- **event-processor** consumes `logs.raw`, masks variable parts of messages, hashes to a fingerprint, counts per fingerprint per minute, applies four deterministic rules to open incidents. Batch handling is idempotent (ADR 0013).
- **incidents** holds lifecycle rules shared by the API and the detector. Opening an incident writes an **outbox** row in the same transaction; **outbox** publisher announces it to Kafka later.
- **workers/ai-analysis** consumes the incident event, embeds the pattern, searches pgvector for similar incidents, then asks Claude for a probable cause. A redaction scanner runs before every LLM call and aborts it on a hit - raw log lines must never go to the model.
- **api** serves queries/actions and the SignalR hub; the web app's nginx proxies API, hub and ingestion so the browser talks only to :3000 and holds no key.
- Shared libraries: `domain` (entities, no EF), `persistence` (DbContext, configs, migrations), `contracts` (Kafka wire types), `messaging` (producer/consumer abstractions), `shared` (logging, health, metrics, API-key auth).
- **Cross-language contract**: `contracts/samples/*.json` are test fixtures. C# must serialise to exactly these bytes and Python Pydantic models must parse them. Wire format is camelCase with a common envelope (`eventId`, `eventType`, `eventVersion`, `occurredAt`, `tenantId`, `correlationId`, `payload`). Additive change keeps the version; breaking change bumps `eventVersion`. Change a contract on both sides and regenerate samples.
- **Multi-tenancy**: tenant isolation is enforced in the schema and EF query filters (ADR 0008); every event carries `tenantId`. Auth is one shared API key (`X-Api-Key`), no login.

## Conventions

- Config via environment variables only; `.env` is git-ignored, `.env.example` has placeholders.
- .NET builds treat warnings as errors (`Directory.Build.props`); version is shared across services and the Python worker.
- Liveness never touches dependencies; readiness checks only dependencies that service uses.
- Dead letters go to `logs.failed` / `incidents.failed` and are never replayed automatically.
- Redis and MinIO run but no application code uses them (ADR 0006).
- Dashboard pages Services, Deployments, Analytics, Settings, Team, Alert Rules are routed but not built (placeholders, not in navigation).
