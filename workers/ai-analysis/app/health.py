"""Dependency probes for the readiness endpoint.

Mirrors the .NET contract: liveness never touches a dependency, readiness
checks only the infrastructure this worker actually uses.
"""

import asyncio
import time
from dataclasses import dataclass

from app.config import Settings

_TIMEOUT_SECONDS = 5.0


@dataclass(slots=True)
class CheckResult:
    name: str
    status: str
    description: str | None = None
    error: str | None = None
    duration_ms: float = 0.0

    @property
    def healthy(self) -> bool:
        return self.status == "Healthy"


def _timed(name: str, started: float, *, description: str | None = None, error: str | None = None) -> CheckResult:
    return CheckResult(
        name=name,
        status="Unhealthy" if error else "Healthy",
        description=description,
        error=error,
        duration_ms=round((time.perf_counter() - started) * 1000, 1),
    )


async def check_postgres(settings: Settings) -> CheckResult:
    started = time.perf_counter()

    if not settings.postgres_dsn:
        return _timed("postgres", started, error="Not configured: set the 'POSTGRES_DSN' environment variable.")

    def _probe() -> None:
        import psycopg

        with (
            psycopg.connect(settings.postgres_dsn, connect_timeout=int(_TIMEOUT_SECONDS)) as conn,
            conn.cursor() as cur,
        ):
            cur.execute("SELECT 1")
            cur.fetchone()

    try:
        await asyncio.wait_for(asyncio.to_thread(_probe), timeout=_TIMEOUT_SECONDS)
        return _timed("postgres", started, description="PostgreSQL reachable.")
    except Exception as exc:  # noqa: BLE001 - a failed probe must never raise
        return _timed("postgres", started, error=f"{type(exc).__name__}: {exc}")


async def check_kafka(settings: Settings) -> CheckResult:
    started = time.perf_counter()

    if not settings.kafka_bootstrap_servers:
        return _timed(
            "kafka", started, error="Not configured: set the 'KAFKA_BOOTSTRAP_SERVERS' environment variable."
        )

    def _probe() -> int:
        from confluent_kafka.admin import AdminClient

        admin = AdminClient(
            {
                "bootstrap.servers": settings.kafka_bootstrap_servers,
                "socket.timeout.ms": int(_TIMEOUT_SECONDS * 1000),
                "log.connection.close": False,
            }
        )
        return len(admin.list_topics(timeout=_TIMEOUT_SECONDS).brokers)

    try:
        broker_count = await asyncio.wait_for(asyncio.to_thread(_probe), timeout=_TIMEOUT_SECONDS + 1)
        return _timed("kafka", started, description=f"Kafka reachable ({broker_count} broker(s)).")
    except Exception as exc:  # noqa: BLE001
        return _timed("kafka", started, error=f"{type(exc).__name__}: {exc}")


async def run_readiness_checks(settings: Settings) -> list[CheckResult]:
    return list(await asyncio.gather(check_postgres(settings), check_kafka(settings)))
