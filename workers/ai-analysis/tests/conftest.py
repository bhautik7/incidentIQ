"""Shared fixtures.

The split here is deliberate. Rules, ranking and embedding helpers are pure and
tested with no infrastructure at all. The pipeline is mostly SQL, so testing it
against anything other than real PostgreSQL with the real schema would be
testing a fake.

Integration tests therefore use the migrated development database, and skip
cleanly when it is not running rather than failing.
"""

import os

import pytest


def pytest_configure(config):
    config.addinivalue_line(
        "markers", "integration: needs a migrated PostgreSQL (the dev stack)."
    )


@pytest.fixture(scope="session")
def postgres_dsn() -> str:
    dsn = os.environ.get(
        "TEST_POSTGRES_DSN",
        "postgresql://incidentiq:dev_only_change_me@localhost:5433/incidentiq",
    )

    try:
        import psycopg

        with psycopg.connect(dsn, connect_timeout=3) as connection, connection.cursor() as cursor:
            # The schema, not merely the server: a database without the
            # migrations applied would fail in confusing ways.
            cursor.execute("SELECT to_regclass('public.ai_analyses')")
            if cursor.fetchone()[0] is None:
                pytest.skip("PostgreSQL is running but migrations have not been applied.")
    except Exception as error:  # noqa: BLE001
        pytest.skip(f"PostgreSQL is not reachable for integration tests: {error}")

    return dsn
