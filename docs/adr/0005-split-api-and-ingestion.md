# 5. Split the API and ingestion into two services

Date: 2026-08-24
Status: Accepted

## Context

The Phase 1 design recommended hosting the query API and the log-ingestion
endpoint in a single ASP.NET Core process, on the grounds that splitting them
later is a small change and splitting early doubles the deployment surface.

Phase 2 splits them anyway.

## Decision

`services/api` and `services/ingestion` are separate deployable processes with
separate images.

## Consequences

- **They scale on different curves.** Ingestion traffic follows the monitored
  applications' log volume; API traffic follows the number of engineers looking
  at dashboards. These are unrelated, and a log burst must never make the
  dashboard unavailable.
- **They have different dependencies, and the health endpoints prove it.**
  Ingestion is configured with Kafka and deliberately has *no* database
  connection string, so it cannot be made slow by PostgreSQL. The API has no
  Kafka configuration. Each service's readiness endpoint therefore means "I can
  do my job", not "the platform is up".
- **Different availability requirements.** Dropping logs is worse than a
  temporarily unavailable dashboard.
- **Cost:** one more image, one more container, one more deployment. Accepted,
  because the shared bootstrap in `services/shared` means the duplicated code is
  roughly ten lines of `Program.cs`.
