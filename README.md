# IncidentIQ

Turns a flood of application logs into a small number of explained incidents.

A bad deploy that produces 4,200 near-identical error lines becomes **one**
incident card with an occurrence count, the affected services, a link to the
similar incident from four months ago, and an LLM-written probable cause.

> **Status: Phase 2 - project foundation.**
> Every service exists, builds, runs in Docker and reports health and metrics.
> No business features are implemented yet: there is no ingestion endpoint, no
> Kafka consumer, and no AI enrichment. Those arrive in Phase 3.

## Quick start

```bash
git clone <repo> && cd IncidentIQ
cp infrastructure/docker/.env.example infrastructure/docker/.env
make up
```

Then:

```bash
make health
```

| Surface | URL | Credentials |
|---|---|---|
| Web dashboard | http://localhost:3000 | - |
| Query API | http://localhost:5080 | - |
| Ingestion | http://localhost:5081 | - |
| Event processor | http://localhost:5082 | - |
| AI analysis (+ `/docs`) | http://localhost:5083 | - |
| Prometheus | http://localhost:9090 | - |
| Grafana | http://localhost:3001 | from `.env` |
| MinIO console | http://localhost:9001 | from `.env` |
| PostgreSQL | `localhost:5433` | from `.env` |
| Kafka | `localhost:29092` | none (PLAINTEXT, local only) |
| Redis | `localhost:6380` | from `.env` |

`make help` lists every target.

## Repository layout

```
IncidentIQ/
├── apps/
│   └── web/                    React 19 + TypeScript (Vite), served by nginx
├── services/
│   ├── api/                    ASP.NET Core - query/dashboard API
│   ├── ingestion/              ASP.NET Core - log intake, Kafka producer
│   ├── event-processor/        ASP.NET Core worker - Kafka consumer
│   └── shared/                 Class library: logging, health, metrics bootstrap
├── workers/
│   └── ai-analysis/            Python FastAPI - embeddings, pgvector, LLM
├── infrastructure/
│   ├── docker/                 docker-compose.yml, .env.example
│   ├── kafka/                  topic creation script
│   ├── postgres/               extension bootstrap SQL
│   └── monitoring/             Prometheus config, Grafana provisioning
├── tests/                      .NET test projects
├── tools/
│   └── load-generator/         k6 scripts
├── docs/
│   ├── architecture/           system design, runtime topology
│   └── adr/                    numbered decision records
├── scripts/                    check-health.sh
├── Directory.Build.props       shared .NET properties (TFM, version, warnings)
├── IncidentIQ.slnx
└── Makefile
```

## Architecture in one paragraph

Applications POST log batches to **ingestion**, which validates them and writes
to **Kafka**, returning `202` in single-digit milliseconds without touching a
database. The **event processor** consumes `logs.raw`, normalises and
fingerprints each message, and collapses thousands of identical errors into one
incident row in **PostgreSQL**, writing an outbox row in the same transaction.
An outbox publisher forwards that to `incidents.created`, where the **Python AI
worker** picks it up, generates an embedding, finds similar past incidents with
**pgvector**, asks an LLM for an explanation, and writes the enrichment back.
The **query API** serves it all to the **React dashboard**.

Full detail: [docs/architecture/system-design.md](docs/architecture/system-design.md).
Decisions and their reasoning: [docs/adr/](docs/adr/).

## Development without Docker

```bash
make verify          # dotnet build + dotnet test + web build

dotnet run --project services/api            # :5000-ish, see console output
cd apps/web && npm run dev                   # :5173
cd workers/ai-analysis && .venv/bin/python -m app   # :8000
```

Services started this way report readiness as *unhealthy* until the relevant
environment variables are set - the response names the missing variable. Point
them at the Docker infrastructure:

```bash
export ConnectionStrings__Postgres="Host=localhost;Port=5433;Database=incidentiq;Username=incidentiq;Password=<from .env>"
export Kafka__BootstrapServers="localhost:29092"
```

Python setup:

```bash
cd workers/ai-analysis
python3 -m venv .venv
.venv/bin/pip install -r requirements-dev.txt
```

## Conventions

- **Configuration is environment variables.** No credential is committed, and no
  connection string is hardcoded. `.env` is git-ignored; `.env.example` holds
  placeholders only.
- **Logging is structured JSON** in containers (Serilog on .NET, structlog on
  Python), plain text in local development. Every line carries `service`,
  `version` and `environment`.
- **Liveness never touches a dependency; readiness only touches the
  dependencies that service actually uses.**
- **Warnings are errors** in the .NET build; `ruff` gates the Python worker;
  `tsc -b` gates the frontend.
