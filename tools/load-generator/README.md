# Load generator

k6 scripts for exercising the platform.

Phase 2 has no ingestion endpoint yet, so `smoke.js` only verifies that the
health endpoints stay responsive under concurrency - enough to catch a
thread-pool or connection-pool misconfiguration in the foundation.

The real log-ingestion load profile is added in Phase 3, alongside the endpoint
it targets.

```bash
brew install k6
k6 run tools/load-generator/smoke.js
k6 run -e BASE_URL=http://localhost:5081 tools/load-generator/smoke.js
```
