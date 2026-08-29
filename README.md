# IncidentIQ

Groups repeated application errors into incidents and explains them.

Paste a log at http://localhost:3000 and you get back one incident with an
occurrence count, the error pattern behind it, any release that lines up in
time, similar past incidents, and a written probable cause. A burst of 4,200
near-identical lines produces one incident, not 4,200.

## Quick start

```bash
cp infrastructure/docker/.env.example infrastructure/docker/.env
make up
make health
```

Open http://localhost:3000 and paste a log into the box.

Or drive it from the shell:

```bash
./scripts/send-logs.sh --count 60                        # synthetic burst
./scripts/upload-log.py samples/payments-api.log --replay-as-now --watch
./scripts/show-analysis.sh --watch                       # patterns -> incidents -> analysis
./scripts/show-analysis.sh --reset                       # clear pipeline data, keep services
./scripts/record-deployment.sh -s payments-api -v 2.8.4  # tell it a release shipped
```

## Ports

The dashboard is the only one you need. It proxies the API, the realtime hub
and ingestion, so the browser talks to port 3000 and holds no credential. The
rest are published for the scripts and for debugging.

| | URL | Credentials |
|---|---|---|
| Dashboard | http://localhost:3000 | none |
| Query API | http://localhost:5080 | `X-Api-Key`, see `.env` |
| Ingestion | http://localhost:5081 | `X-Api-Key`, see `.env` |
| Event processor | http://localhost:5082 | health and metrics only |
| AI worker (`/docs`) | http://localhost:5083 | health and metrics only |
| Prometheus | http://localhost:9090 | none |
| Grafana | http://localhost:3001 | `.env` |
| MinIO console | http://localhost:9001 | `.env` |
| PostgreSQL | `localhost:5433` | `.env` |
| Kafka | `localhost:29092` | none, PLAINTEXT, local only |
| Redis | `localhost:6380` | `.env` |

Redis and MinIO run but no application code uses them yet.
[ADR 0006](docs/adr/0006-defer-redis-and-object-storage.md) records what would
have to be true before either is adopted.

`make help` lists every target.

## How it works

```
HTTP -> Kafka -> normalise + fingerprint -> detection rules -> incident
     -> outbox -> Python worker -> embedding -> pgvector -> LLM -> API -> React
```

Ingestion validates a batch, writes it to Kafka and returns 202 without
touching the database. The event processor consumes `logs.raw`, masks the
variable parts of each message (`user 18273` becomes `user {NUM}`), hashes the
result into a fingerprint, and counts occurrences per fingerprint per minute.
Four rules decide when that is worth an incident.

Opening an incident writes an outbox row in the same transaction, so a broker
that is down delays the announcement rather than losing it. The Python worker
picks it up, embeds the pattern, searches pgvector for similar past incidents,
and asks Claude for a probable cause. Raw log lines are never sent to the
model; a redaction scanner runs before every call and aborts it on a hit.

Decisions and their reasoning live in [docs/adr/](docs/adr/) - 17 records, and
the real documentation. Start with
[0012](docs/adr/0012-log-fingerprinting.md) on fingerprinting and
[0016](docs/adr/0016-evidence-before-llm.md) on why retrieval does the work and
the model only writes the paragraph.

## Layout

```
apps/web/                React 19, TypeScript, Vite, Tailwind. nginx in the container.
services/
  api/                   Query API, incident actions, SignalR hub
  ingestion/             POST /api/v1/logs/batch -> Kafka
  event-processor/       Normalise, fingerprint, detect
  incidents/             Lifecycle rules, shared by the API and the detector
  outbox/                Transactional outbox publisher
  domain/                Entities and enums, no EF dependency
  persistence/           DbContext, EF configs, migrations
  contracts/             Kafka wire types
  messaging/             Producer and consumer abstractions
  shared/                Logging, health, metrics, API key auth
workers/ai-analysis/     Python 3.12, FastAPI, sentence-transformers, Anthropic
infrastructure/docker/   docker-compose.yml, .env.example
tests/                   6 .NET test projects
tools/load-generator/    k6 scripts
```

## Tests

```bash
make verify                                          # build + test + web build
dotnet test IncidentIQ.slnx
cd workers/ai-analysis && .venv/bin/python -m pytest -q
cd apps/web && npx tsc -b --noEmit && npm run lint
```

Integration tests use Testcontainers against real PostgreSQL and Kafka rather
than mocks. The API suite starts a container per test, which makes it slow.

## Running without Docker

```bash
export ConnectionStrings__Postgres="Host=localhost;Port=5433;Database=incidentiq;Username=incidentiq;Password=<from .env>"
export Kafka__BootstrapServers="localhost:29092"

dotnet run --project services/api
cd apps/web && npm run dev          # :5173, proxies to the services above
cd workers/ai-analysis && .venv/bin/python -m app
```

Readiness reports unhealthy until the variables a service actually needs are
set, and the response names the missing one.

Python setup:

```bash
cd workers/ai-analysis
python3 -m venv .venv
.venv/bin/pip install -r requirements-dev.txt
```

## Conventions

Configuration comes from environment variables. `.env` is git-ignored and
`.env.example` holds placeholders, so no credential is committed.

Logs are JSON in containers (Serilog, structlog) and plain text locally. Every
line carries `service`, `version` and `environment`.

Liveness never touches a dependency. Readiness touches only the dependencies
that service actually uses, so a Kafka outage does not take the read API out of
rotation.

Warnings are errors in the .NET build. `ruff` gates Python, `tsc -b` gates the
frontend.

## Known limits

Rebuild the web image after a frontend change or port 3000 keeps serving the
previous build:

```bash
docker compose -f infrastructure/docker/docker-compose.yml up -d --build web
```

There is no login. The dashboard authenticates with a single API key held by
nginx, so everyone who opens it shares one organization's data. Tenant
isolation is enforced in the schema and in EF's query filters, but there is no
sign-up flow in front of it.

Deployments are recorded by `POST /api/v1/deployments`, or by
`./scripts/record-deployment.sh` as the last step of a deploy job. Nothing
calls it automatically, so a release that is not reported cannot be
correlated - and correlation is worth more than anything else the analysis
has. Recording one moved a test incident from 40% confidence and a symptom to
68% and "version 3.1.0 shipped 7.2 minutes before the first occurrence".

Kafka consumers have twice dropped out of their group and not rejoined.
Readiness now catches it and names the group, but the cause is not understood.

Dead letters go to `logs.failed` for the log path and `incidents.failed` for
the incident path. Nothing replays them automatically - a dead letter is read
by a person, because the reason it died usually matters more than the message.

Several dashboard pages (Services, Deployments, Analytics, Settings, Team,
Alert Rules) are routed but not built. They are not linked from the navigation.
