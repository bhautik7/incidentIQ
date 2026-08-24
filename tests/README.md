# Tests

| Project | Scope |
|---|---|
| `IncidentIQ.Api.Tests` | Boots `IncidentIQ.Api` in-memory via `WebApplicationFactory` and asserts the health/identity/metrics contract that every other component depends on. |

Python tests live next to the worker they cover, in `workers/ai-analysis/tests`,
because they run under that service's own virtual environment.

```bash
dotnet test IncidentIQ.slnx                                   # .NET
cd workers/ai-analysis && .venv/bin/python -m pytest -q       # Python
```

Phase 2 covers the foundation only: the host starts, liveness never touches a
dependency, readiness names the dependency it cannot reach, and `/metrics` is
exposed. Pipeline tests (normalisation, fingerprinting, correlation,
idempotency) arrive with the code they cover in Phase 3.
