"""FastAPI host for the IncidentIQ AI analysis worker.

The HTTP surface is deliberately small: identity, health, metrics, and
diagnostics that answer questions an operator actually asks - "is the model
loaded", "what would this incident analyse to", "why did that match". The work
itself arrives over Kafka, not over HTTP.

The analysis endpoint exists because the alternative - publishing a Kafka
message and reading logs to find out what happened - is a miserable way to
debug a ranking function.
"""

import asyncio
import time
from contextlib import asynccontextmanager

import structlog
from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse
from prometheus_fastapi_instrumentator import Instrumentator

from app import __version__
from app.analysis.pipeline import AnalysisPipeline, IncidentNotFoundError
from app.analysis.repository import AnalysisRepository
from app.config import Settings, get_settings
from app.db import Database
from app.embeddings import Embedder, SentenceTransformerEmbedder, build_incident_signature
from app.health import run_readiness_checks
from app.llm.client import IncidentNarrator
from app.logging_config import configure_logging
from app.messaging.kafka import EventProducer
from app.worker import AnalysisWorker

settings: Settings = get_settings()
configure_logging(settings)
log = structlog.get_logger()


class Runtime:
    """Process-wide singletons, built once at startup.

    The embedding model takes seconds to load and holds its weights in memory,
    so it is created here rather than per message.
    """

    database: Database | None = None
    embedder: Embedder | None = None
    pipeline: AnalysisPipeline | None = None
    producer: EventProducer | None = None
    worker: AnalysisWorker | None = None
    task: asyncio.Task | None = None
    model_ready: bool = False
    model_error: str | None = None


runtime = Runtime()


@asynccontextmanager
async def lifespan(app: FastAPI):
    log.info("ai_analysis.starting", port=settings.port)

    if settings.analysis_enabled and settings.postgres_dsn and settings.kafka_bootstrap_servers:
        try:
            runtime.database = Database(settings)
            await runtime.database.start()

            runtime.embedder = SentenceTransformerEmbedder(
                settings.embedding_model, settings.embedding_dimensions
            )
            runtime.model_ready = True

            # The narrator is built only when a key is configured. Without
            # one the worker runs the full deterministic pipeline and writes
            # template summaries, which is a complete product rather than a
            # degraded one.
            narrator = None
            if settings.llm_enabled and settings.anthropic_api_key:
                narrator = IncidentNarrator(settings)
                log.info("llm_narration_enabled", model=settings.llm_model, effort=settings.llm_effort)
            else:
                log.info(
                    "llm_narration_disabled",
                    reason="no ANTHROPIC_API_KEY" if settings.llm_enabled else "LLM_ENABLED=false",
                )

            runtime.pipeline = AnalysisPipeline(settings, runtime.embedder, narrator)
            runtime.producer = EventProducer(settings)
            runtime.worker = AnalysisWorker(
                settings, runtime.database, runtime.pipeline, runtime.producer
            )

            runtime.task = asyncio.create_task(runtime.worker.run())
            log.info("ai_analysis.worker_started")
        except Exception as error:  # noqa: BLE001
            # A worker that cannot start must not take the process with it: the
            # health endpoints are how anyone finds out why.
            runtime.model_error = str(error)
            log.error("ai_analysis.worker_start_failed", error=str(error), exc_info=True)
    else:
        log.warning(
            "ai_analysis.worker_disabled",
            analysis_enabled=settings.analysis_enabled,
            has_postgres=bool(settings.postgres_dsn),
            has_kafka=bool(settings.kafka_bootstrap_servers),
        )

    yield

    log.info("ai_analysis.stopping")

    if runtime.worker is not None:
        runtime.worker.stop()

    if runtime.task is not None:
        try:
            await asyncio.wait_for(runtime.task, timeout=20)
        except (TimeoutError, asyncio.CancelledError):
            log.warning("ai_analysis.worker_shutdown_timeout")

    if runtime.producer is not None:
        remaining = runtime.producer.flush(10.0)
        if remaining:
            log.error("ai_analysis.producer_flush_incomplete", remaining=remaining)

    if runtime.database is not None:
        await runtime.database.stop()


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


# ---------------------------------------------------------------------------
# Diagnostics
# ---------------------------------------------------------------------------


@app.get("/diagnostics/model", tags=["diagnostics"])
async def model_info() -> dict[str, object]:
    """Whether the embedding model loaded, and what shape it emits.

    The first thing to check when every analysis is failing: a dimension
    mismatch between the model and the vector(N) column produces an opaque
    database error at insert time, and this endpoint names it directly.
    """
    return {
        "model": settings.embedding_model,
        "expectedDimensions": settings.embedding_dimensions,
        "actualDimensions": runtime.embedder.dimensions if runtime.embedder else None,
        "loaded": runtime.model_ready,
        "error": runtime.model_error,
        "workerRunning": runtime.task is not None and not runtime.task.done(),
    }


@app.post("/diagnostics/embed", tags=["diagnostics"])
async def embed(payload: dict) -> dict[str, object]:
    """Embeds arbitrary text, so similarity behaviour can be probed by hand.

    Useful for the question "why did these two incidents match?", which is
    otherwise answerable only by reading vectors out of the database.
    """
    if runtime.embedder is None:
        raise HTTPException(status_code=503, detail="The embedding model is not loaded.")

    text = payload.get("text")

    if not isinstance(text, str) or not text.strip():
        raise HTTPException(status_code=400, detail="Provide a non-empty 'text' field.")

    vector = runtime.embedder.encode([text])[0]

    return {
        "text": text,
        "dimensions": len(vector),
        # The first few components only: 384 floats in a response body help
        # nobody, and the norm is the part worth checking.
        "preview": [round(float(v), 6) for v in vector[:8]],
        "norm": round(float((vector**2).sum() ** 0.5), 6),
    }


@app.post("/diagnostics/analyze/{organization_id}/{incident_id}", tags=["diagnostics"])
async def analyze_now(organization_id: str, incident_id: str, version: int = 999) -> dict:
    """Runs the pipeline against a real incident and returns the full evidence.

    Publishing a Kafka message and reading container logs is a miserable way to
    debug a ranking function. The default version is deliberately high so a
    diagnostic run cannot collide with a real analysis's unique key.
    """
    if runtime.pipeline is None or runtime.database is None:
        raise HTTPException(status_code=503, detail="The analysis pipeline is not running.")

    async with runtime.database.connection() as connection:
        repository = AnalysisRepository(connection)

        try:
            result = await runtime.pipeline.run(
                repository,
                organization_id=organization_id,
                incident_id=incident_id,
                analysis_version=version,
            )
        except IncidentNotFoundError as error:
            raise HTTPException(status_code=404, detail=str(error)) from error

        await connection.commit()

    return result.model_dump(mode="json", by_alias=True)


@app.post("/diagnostics/signature", tags=["diagnostics"])
async def signature(payload: dict) -> dict[str, str]:
    """Shows the exact text that would be embedded for a given incident shape.

    The signature is where most similarity surprises originate - usually a raw
    message leaking in where the normalised template belongs.
    """
    return {
        "signature": build_incident_signature(
            title=payload.get("title", ""),
            service=payload.get("service", ""),
            environment=payload.get("environment", ""),
            exception_type=payload.get("exceptionType"),
            message_template=payload.get("messageTemplate"),
        )
    }
