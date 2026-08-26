"""PostgreSQL access: one pool for the process, with pgvector registered.

The worker reads far more than it writes - an analysis is a handful of queries
and one insert - so a small pool is plenty. Opening a connection per message
would cost more than the queries themselves.
"""

from collections.abc import AsyncIterator
from contextlib import asynccontextmanager

import structlog
from pgvector.psycopg import register_vector_async
from psycopg import AsyncConnection
from psycopg_pool import AsyncConnectionPool

from app.config import Settings

logger = structlog.get_logger(__name__)


async def _configure(connection: AsyncConnection) -> None:
    """Teach each connection about the vector type.

    Without this, psycopg returns embeddings as strings and sends them as
    strings, and every similarity query fails on a type mismatch that reads
    like a syntax error.
    """
    await register_vector_async(connection)


class Database:
    def __init__(self, settings: Settings) -> None:
        if not settings.postgres_dsn:
            raise RuntimeError("POSTGRES_DSN is required by the analysis worker.")

        self._pool = AsyncConnectionPool(
            conninfo=settings.postgres_dsn,
            min_size=1,
            max_size=5,
            configure=_configure,
            # The worker starts before PostgreSQL is guaranteed to be up; let
            # readiness report that rather than crashing the process.
            open=False,
        )

    async def start(self) -> None:
        await self._pool.open(wait=True, timeout=30)
        logger.info("database_pool_ready")

    async def stop(self) -> None:
        await self._pool.close()

    @asynccontextmanager
    async def connection(self) -> AsyncIterator[AsyncConnection]:
        async with self._pool.connection() as connection:
            yield connection
