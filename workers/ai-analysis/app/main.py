"""FastAPI host for the IncidentIQ AI analysis worker.

Phase 2 exposes identity, health and metrics only. Embedding generation,
pgvector similarity search and LLM enrichment arrive in Phase 3.
"""

import time
from contextlib import asynccontextmanager

import structlog
from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse
from prometheus_fastapi_instrumentator import Instrumentator

from app import __version__
from app.config import Settings, get_settings
from app.health import run_readiness_checks
from app.logging_config import configure_logging

settings: Settings = get_settings()
configure_logging(settings)
log = structlog.get_logger()


@asynccontextmanager
async def lifespan(app: FastAPI):
    log.info("ai_analysis.starting", port=settings.port)
    yield
    log.info("ai_analysis.stopping")


app = FastAPI(
    title="IncidentIQ AI Analysis Worker",
    version=__version__,
    lifespan=lifespan,
    docs_url="/docs",
    openapi_url="/openapi.json",
)

# Only enabled when CORS_ALLOWED_ORIGINS is set; no configured origins means
# no CORS headers at all, which is the safe default.
if settings.cors_origins:
    app.add_middleware(
        CORSMiddleware,
        allow_origins=settings.cors_origins,
        allow_methods=["*"],
        allow_headers=["*"],
    )

Instrumentator(excluded_handlers=["/health.*", "/metrics"]).instrument(app).expose(
    app, endpoint="/metrics", include_in_schema=False
)


@app.get("/", tags=["meta"])
async def root() -> dict[str, str]:
    return {
        "service": settings.service_name,
        "version": __version__,
        "environment": settings.environment,
        "status": "running",
    }


@app.get("/health/live", tags=["health"])
async def live() -> dict[str, object]:
    """Liveness. Deliberately dependency-free: a database blip must not
    cause the orchestrator to restart this container."""
    return {
        "status": "Healthy",
        "service": settings.service_name,
        "version": __version__,
        "environment": settings.environment,
        "checks": [{"name": "self", "status": "Healthy", "description": "Process is running."}],
    }


@app.get("/health/ready", tags=["health"])
async def ready() -> JSONResponse:
    started = time.perf_counter()
    results = await run_readiness_checks(settings)
    healthy = all(result.healthy for result in results)

    payload = {
        "status": "Healthy" if healthy else "Unhealthy",
        "service": settings.service_name,
        "version": __version__,
        "environment": settings.environment,
        "totalDurationMs": round((time.perf_counter() - started) * 1000, 1),
        "checks": [
            {
                "name": result.name,
                "status": result.status,
                "description": result.description,
                "error": result.error,
                "durationMs": result.duration_ms,
            }
            for result in results
        ],
    }

    return JSONResponse(status_code=200 if healthy else 503, content=payload)


@app.get("/health", tags=["health"])
async def health() -> JSONResponse:
    return await ready()
