# Runtime topology (Phase 2)

## Processes

| Process | Stack | Source | Image | Container port | Host port |
|---|---|---|---|---|---|
| Query API | ASP.NET Core (net10.0) | `services/api` | `incidentiq/api:dev` | 8080 | 5080 |
| Ingestion | ASP.NET Core (net10.0) | `services/ingestion` | `incidentiq/ingestion:dev` | 8080 | 5081 |
| Event processor | ASP.NET Core + `BackgroundService` | `services/event-processor` | `incidentiq/event-processor:dev` | 8080 | 5082 |
| AI analysis | Python 3.12 + FastAPI | `workers/ai-analysis` | `incidentiq/ai-analysis:dev` | 8000 | 5083 |
| Web | React 19 + TypeScript, served by nginx | `apps/web` | `incidentiq/web:dev` | 80 | 3000 |

`services/shared` is a class library, not a process. It holds the host bootstrap
every .NET service uses: Serilog configuration, health endpoints, Prometheus
metrics, and the PostgreSQL/Kafka readiness probes.

The event processor is a worker hosted as a web application. That is deliberate:
it means Docker, Prometheus and (later) Kubernetes reach it through exactly the
same `/health` and `/metrics` endpoints as every other service.

## Infrastructure

| Container | Image | Container port | Host port |
|---|---|---|---|
| postgres | `pgvector/pgvector:pg16` | 5432 | 5433 |
| kafka | `confluentinc/cp-kafka:7.8.0` (KRaft) | 9092 internal / 29092 external | 29092 |
| redis | `redis:7.4-alpine` | 6379 | 6380 |
| minio | `minio/minio` | 9000, 9001 | 9000, 9001 |
| prometheus | `prom/prometheus:v3.1.0` | 9090 | 9090 |
| grafana | `grafana/grafana:11.4.0` | 3000 | 3001 |

PostgreSQL and Redis are published on non-default host ports so the stack does
not collide with a locally installed PostgreSQL or Redis.

## Networking

Every container joins one bridge network, `incidentiq`, and addresses the others
by **compose service name on the container port**:

```
api, event-processor, ai-analysis  ->  postgres:5432
ingestion, event-processor, ai-analysis -> kafka:9092
prometheus -> api:8080, ingestion:8080, event-processor:8080, ai-analysis:8000
grafana    -> prometheus:9090
minio-init -> minio:9000
```

Host-published ports exist only for the developer's browser and tools. Two
consequences worth internalising:

**Kafka advertises two listeners.** `PLAINTEXT://kafka:9092` for containers and
`EXTERNAL://localhost:29092` for the host. A client is told which address to use
based on the listener it connected through, so both must be correct or the
client will connect once and then fail on the second hop.

**The browser is not on the Docker network.** The web container serves a bundle
that runs on the *host*, so its API endpoints must be `http://localhost:5080`,
not `http://api:8080` - the host cannot resolve compose service names. This is
why the web container's environment variables are host URLs while every other
service's are container URLs.

## Configuration

All configuration is environment variables; nothing is baked into an image.

| Service | Key variables |
|---|---|
| api | `ConnectionStrings__Postgres`, `Cors__AllowedOrigins__0..n`, `IncidentIQ__LogFormat` |
| ingestion | `Kafka__BootstrapServers`, `Cors__AllowedOrigins__0..n` (no database, by design) |
| event-processor | `ConnectionStrings__Postgres`, `Kafka__BootstrapServers`, `Kafka__ConsumerGroupId` |
| ai-analysis | `POSTGRES_DSN`, `KAFKA_BOOTSTRAP_SERVERS`, `CORS_ALLOWED_ORIGINS`, `LOG_FORMAT`, `LOG_LEVEL` |
| web | `WEB_API_BASE_URL`, `WEB_INGESTION_BASE_URL`, `WEB_EVENT_PROCESSOR_BASE_URL`, `WEB_AI_ANALYSIS_BASE_URL` |

The web app is a static bundle, so it cannot read environment variables at
runtime. Instead the nginx entrypoint regenerates `/config.js` from `WEB_*`
variables on every container start, and the app reads
`window.__INCIDENTIQ_CONFIG__`. One immutable image is therefore promotable
across environments.

Credentials come from `infrastructure/docker/.env`, which is git-ignored.
`.env.example` is the template and contains only obvious placeholders.

## Health endpoints

Every service exposes the same three:

| Endpoint | Meaning |
|---|---|
| `/health/live` | Is the process wedged? **Never touches a dependency** - otherwise a database blip restarts every container. This is what the Docker `HEALTHCHECK` calls. |
| `/health/ready` | Can this service do its job? Checks only the infrastructure it actually uses, and returns 503 with the failing dependency named. |
| `/metrics` | Prometheus exposition. |

A service that is missing a setting reports readiness as unhealthy with the name
of the environment variable to set, rather than crashing at startup.

The web container's equivalent is `/healthz`, served by nginx.
