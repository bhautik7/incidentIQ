"""The pipeline against real PostgreSQL with the real migrated schema.

Uses the deterministic HashingEmbedder rather than the real model: this suite
is exercising the SQL, the ranking and the tenant scoping, and a 90MB download
would make it slow without making it stricter. The real model is exercised
end to end separately.
"""

import uuid
from datetime import UTC, datetime, timedelta

import pytest
import pytest_asyncio
from pgvector.psycopg import register_vector_async
from psycopg import AsyncConnection

from app.analysis.pipeline import AnalysisPipeline, IncidentNotFoundError
from app.analysis.repository import AnalysisRepository
from app.config import Settings
from app.embeddings import HashingEmbedder

pytestmark = pytest.mark.integration

NOW = datetime.now(UTC).replace(microsecond=0)
DIMENSIONS = 384


@pytest.fixture
def settings() -> Settings:
    return Settings(
        POSTGRES_DSN="unused-in-these-tests",
        EMBEDDING_DIMENSIONS=DIMENSIONS,
        SIMILARITY_TOP_K=5,
        SIMILARITY_MIN_SCORE=0.10,
        ANOMALY_BASELINE_MINUTES=180,
        ANOMALY_WINDOW_MINUTES=5,
        DEPLOYMENT_CORRELATION_MINUTES=60,
    )


@pytest.fixture
def embedder() -> HashingEmbedder:
    return HashingEmbedder(dimensions=DIMENSIONS)


@pytest_asyncio.fixture
async def connection(postgres_dsn: str):
    async with await AsyncConnection.connect(postgres_dsn) as connection:
        await register_vector_async(connection)
        yield connection
        await connection.rollback()


class World:
    """A self-contained tenant, so tests never collide with each other or with
    whatever the dev stack happens to have in it."""

    def __init__(self) -> None:
        self.org = uuid.uuid4()
        self.service = uuid.uuid4()
        self.environment = uuid.uuid4()
        self.pattern = uuid.uuid4()
        self.incident = uuid.uuid4()


async def seed_world(connection: AsyncConnection, embedder: HashingEmbedder, **options) -> World:
    world = World()
    service_key = options.get("service_key", "payments-api")

    async with connection.cursor() as cursor:
        await cursor.execute(
            "INSERT INTO organizations (id, name, slug, status, log_retention_days, created_at, updated_at)"
            " VALUES (%s, %s, %s, 'Active', 90, now(), now())",
            (world.org, f"org-{world.org}", f"slug-{world.org}"),
        )
        await cursor.execute(
            "INSERT INTO monitored_services (id, organization_id, key, display_name, is_active, created_at, updated_at)"
            " VALUES (%s, %s, %s, %s, true, now(), now())",
            (world.service, world.org, service_key, service_key),
        )
        await cursor.execute(
            "INSERT INTO environments (id, organization_id, key, display_name, rank,"
            " is_production, created_at, updated_at)"
            " VALUES (%s, %s, 'production', 'Production', 100, true, now(), now())",
            (world.environment, world.org),
        )
        await cursor.execute(
            "INSERT INTO log_patterns (id, organization_id, monitored_service_id, environment_id,"
            " fingerprint, level, exception_type, message_template, sample_message,"
            " occurrence_count, first_seen_at, last_seen_at, is_muted, http_status_code, created_at, updated_at)"
            " VALUES (%s, %s, %s, %s, %s, 'Error', %s, %s, %s, %s, %s, %s, false, %s, now(), now())",
            (
                world.pattern, world.org, world.service, world.environment,
                uuid.uuid4().hex + uuid.uuid4().hex,
                options.get("exception_type", "System.TimeoutException"),
                options.get("template", "Connection timeout for user {NUM}"),
                "Connection timeout for user 18273",
                options.get("occurrences", 400),
                NOW - timedelta(minutes=options.get("pattern_age_minutes", 5)),
                NOW,
                options.get("http_status"),
            ),
        )
        await cursor.execute(
            "INSERT INTO incidents (id, organization_id, monitored_service_id, environment_id,"
            " log_pattern_id, dedupe_key, detection_rule, title, status, severity, occurrence_count,"
            " first_seen_at, last_seen_at, suspected_deployment_id, created_at, updated_at)"
            " VALUES (%s, %s, %s, %s, %s, %s, %s, %s, 'Detected', 'Critical', %s, %s, %s, NULL, now(), now())",
            (
                world.incident, world.org, world.service, world.environment,
                world.pattern, f"fp:{uuid.uuid4()}",
                options.get("detection_rule", "CountThreshold"),
                options.get("title", "TimeoutException: Connection timeout for user {NUM}"),
                options.get("occurrences", 400),
                NOW - timedelta(minutes=options.get("pattern_age_minutes", 5)),
                NOW,
            ),
        )

    return world


async def add_buckets(connection, world: World, *, baseline_per_minute: int, window_per_minute: int):
    """Minute buckets: three hours of baseline, then the current window."""
    async with connection.cursor() as cursor:
        for minute in range(180, 5, -1):
            await cursor.execute(
                "INSERT INTO log_pattern_metrics (organization_id, log_pattern_id, bucket_start, count)"
                " VALUES (%s, %s, %s, %s) ON CONFLICT DO NOTHING",
                (world.org, world.pattern, NOW - timedelta(minutes=minute), baseline_per_minute),
            )
        for minute in range(5, -1, -1):
            await cursor.execute(
                "INSERT INTO log_pattern_metrics (organization_id, log_pattern_id, bucket_start, count)"
                " VALUES (%s, %s, %s, %s) ON CONFLICT DO NOTHING",
                (world.org, world.pattern, NOW - timedelta(minutes=minute), window_per_minute),
            )


async def add_deployment(connection, world: World, *, minutes_ago: int, version="2.31.0", suspected=False):
    deployment_id = uuid.uuid4()

    async with connection.cursor() as cursor:
        await cursor.execute(
            "INSERT INTO deployments (id, organization_id, monitored_service_id, environment_id,"
            " version, commit_sha, deployed_at, status, created_at)"
            " VALUES (%s, %s, %s, %s, %s, %s, %s, 'Succeeded', now())",
            (deployment_id, world.org, world.service, world.environment, version,
             "9f4c2ab7d31e05b6c8a1f2e3d4b5a6c7d8e9f001", NOW - timedelta(minutes=minutes_ago)),
        )
        if suspected:
            await cursor.execute(
                "UPDATE incidents SET suspected_deployment_id = %s WHERE id = %s",
                (deployment_id, world.incident),
            )

    return deployment_id


async def add_resolved_incident(
    connection, world: World, embedder: HashingEmbedder, *, title: str, notes: str, signature: str
):
    """A past, resolved incident with an embedding - the corpus similarity searches."""
    incident_id = uuid.uuid4()
    embedding = embedder.encode([signature])[0]

    async with connection.cursor() as cursor:
        await cursor.execute(
            "INSERT INTO incidents (id, organization_id, monitored_service_id, environment_id,"
            " log_pattern_id, dedupe_key, detection_rule, title, status, severity, occurrence_count,"
            " first_seen_at, last_seen_at, resolved_at, resolution_notes, created_at, updated_at)"
            " VALUES (%s, %s, %s, %s, NULL, %s, 'CountThreshold', %s, 'Resolved', 'High', 100,"
            " %s, %s, %s, %s, now(), now())",
            (incident_id, world.org, world.service, world.environment, f"fp:{uuid.uuid4()}",
             title, NOW - timedelta(days=120), NOW - timedelta(days=120), NOW - timedelta(days=119), notes),
        )
        await cursor.execute(
            "INSERT INTO ai_analyses (id, organization_id, incident_id, analysis_version, status,"
            " embedding, embedding_model, model_provider, created_at, completed_at)"
            " VALUES (%s, %s, %s, 1, 'Completed', %s, 'hashing', 'deterministic', now(), now())",
            (uuid.uuid4(), world.org, incident_id, embedding),
        )

    return incident_id


# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_the_pipeline_gathers_patterns_and_persists_an_analysis(connection, settings, embedder):
    world = await seed_world(connection, embedder)
    await add_buckets(connection, world, baseline_per_minute=2, window_per_minute=40)

    result = await AnalysisPipeline(settings, embedder).run(
        AnalysisRepository(connection),
        organization_id=str(world.org),
        incident_id=str(world.incident),
        analysis_version=1,
    )

    assert len(result.patterns) == 1
    assert result.patterns[0].message_template == "Connection timeout for user {NUM}"
    assert result.summary
    assert result.latency_ms >= 0

    async with connection.cursor() as cursor:
        await cursor.execute(
            "SELECT status, embedding IS NOT NULL FROM ai_analyses WHERE incident_id = %s AND analysis_version = 1",
            (world.incident,),
        )
        stored = await cursor.fetchone()

    assert stored == ("Completed", True)


@pytest.mark.asyncio
async def test_the_anomaly_magnitude_is_measured_against_the_patterns_own_baseline(
    connection, settings, embedder
):
    world = await seed_world(connection, embedder)
    await add_buckets(connection, world, baseline_per_minute=2, window_per_minute=40)

    result = await AnalysisPipeline(settings, embedder).run(
        AnalysisRepository(connection),
        organization_id=str(world.org), incident_id=str(world.incident), analysis_version=1,
    )

    assert result.anomaly is not None
    assert result.anomaly.magnitude >= 15
    assert result.anomaly.baseline_sample_count > 100


@pytest.mark.asyncio
async def test_a_recent_deployment_becomes_evidence_and_the_leading_candidate(
    connection, settings, embedder
):
    world = await seed_world(connection, embedder, detection_rule="NewErrorAfterDeployment")
    await add_buckets(connection, world, baseline_per_minute=1, window_per_minute=30)
    await add_deployment(connection, world, minutes_ago=8, suspected=True)

    result = await AnalysisPipeline(settings, embedder).run(
        AnalysisRepository(connection),
        organization_id=str(world.org), incident_id=str(world.incident), analysis_version=1,
    )

    assert result.deployment is not None
    assert result.deployment.version == "2.31.0"
    assert result.root_cause_candidates[0].kind == "recent_deployment"
    assert "2.31.0" in (result.probable_cause or "")


@pytest.mark.asyncio
async def test_similarity_search_finds_the_matching_past_incident_and_returns_its_fix(
    connection, settings, embedder
):
    world = await seed_world(connection, embedder)
    await add_buckets(connection, world, baseline_per_minute=2, window_per_minute=40)

    # The signature the pipeline will build for the incident under analysis.
    signature = (
        "TimeoutException: Connection timeout for user {NUM} | service: payments-api "
        "| environment: production | exception: System.TimeoutException "
        "| Connection timeout for user {NUM}"
    )

    await add_resolved_incident(
        connection, world, embedder,
        title="Pool exhaustion after DI refactor",
        notes="Reverted the DbContext lifetime change in 2.30.1.",
        signature=signature,
    )
    await add_resolved_incident(
        connection, world, embedder,
        title="Disk full on the log volume",
        notes="Expanded the volume and added a rotation policy.",
        signature="disk full log volume storage capacity nothing to do with timeouts",
    )

    result = await AnalysisPipeline(settings, embedder).run(
        AnalysisRepository(connection),
        organization_id=str(world.org), incident_id=str(world.incident), analysis_version=1,
    )

    assert result.similar_incidents
    best = result.similar_incidents[0]

    assert best.title == "Pool exhaustion after DI refactor"
    # The whole point of the match: the answer comes with it.
    assert "Reverted the DbContext" in (best.resolution_notes or "")
    assert any("Reverted the DbContext" in action for action in result.suggested_actions)


@pytest.mark.asyncio
async def test_similarity_search_never_crosses_a_tenant_boundary(connection, settings, embedder):
    signature = (
        "TimeoutException: Connection timeout for user {NUM} | service: payments-api "
        "| environment: production | exception: System.TimeoutException "
        "| Connection timeout for user {NUM}"
    )

    other = await seed_world(connection, embedder)
    await add_resolved_incident(
        connection, other, embedder,
        title="Another organization's identical problem",
        notes="Their fix, which we must never see.",
        signature=signature,
    )

    world = await seed_world(connection, embedder)
    await add_buckets(connection, world, baseline_per_minute=2, window_per_minute=40)

    result = await AnalysisPipeline(settings, embedder).run(
        AnalysisRepository(connection),
        organization_id=str(world.org), incident_id=str(world.incident), analysis_version=1,
    )

    titles = [s.title for s in result.similar_incidents]
    assert "Another organization's identical problem" not in titles


@pytest.mark.asyncio
async def test_unresolved_lookalikes_are_not_offered(connection, settings, embedder):
    world = await seed_world(connection, embedder)
    await add_buckets(connection, world, baseline_per_minute=2, window_per_minute=40)

    # An unresolved twin says "someone else has this too", which is not help.
    twin = uuid.uuid4()
    signature = "TimeoutException: Connection timeout for user {NUM} | service: payments-api"

    async with connection.cursor() as cursor:
        await cursor.execute(
            "INSERT INTO incidents (id, organization_id, monitored_service_id, environment_id,"
            " log_pattern_id, dedupe_key, detection_rule, title, status, severity, occurrence_count,"
            " first_seen_at, last_seen_at, created_at, updated_at)"
            " VALUES (%s, %s, %s, %s, NULL, %s, 'CountThreshold', 'Still broken', 'Detected', 'High', 10,"
            " %s, %s, now(), now())",
            (twin, world.org, world.service, world.environment, f"fp:{uuid.uuid4()}",
             NOW - timedelta(days=1), NOW - timedelta(days=1)),
        )
        await cursor.execute(
            "INSERT INTO ai_analyses (id, organization_id, incident_id, analysis_version, status,"
            " embedding, embedding_model, model_provider, created_at, completed_at)"
            " VALUES (%s, %s, %s, 1, 'Completed', %s, 'hashing', 'deterministic', now(), now())",
            (uuid.uuid4(), world.org, twin, embedder.encode([signature])[0]),
        )

    result = await AnalysisPipeline(settings, embedder).run(
        AnalysisRepository(connection),
        organization_id=str(world.org), incident_id=str(world.incident), analysis_version=1,
    )

    assert all(s.title != "Still broken" for s in result.similar_incidents)


@pytest.mark.asyncio
async def test_weak_matches_are_filtered_out(connection, settings, embedder):
    settings = settings.model_copy(update={"similarity_min_score": 0.95})

    world = await seed_world(connection, embedder)
    await add_buckets(connection, world, baseline_per_minute=2, window_per_minute=40)
    await add_resolved_incident(
        connection, world, embedder,
        title="Completely unrelated", notes="Unrelated fix.",
        signature="disk full storage volume rotation policy",
    )

    result = await AnalysisPipeline(settings, embedder).run(
        AnalysisRepository(connection),
        organization_id=str(world.org), incident_id=str(world.incident), analysis_version=1,
    )

    # A 0.2-similar incident shown as "related" teaches people to skip the
    # section, which costs the strong matches too.
    assert result.similar_incidents == []


@pytest.mark.asyncio
async def test_rerunning_the_same_version_does_not_write_a_second_row(connection, settings, embedder):
    world = await seed_world(connection, embedder)
    await add_buckets(connection, world, baseline_per_minute=2, window_per_minute=40)

    pipeline = AnalysisPipeline(settings, embedder)
    repository = AnalysisRepository(connection)

    for _ in range(3):
        await pipeline.run(
            repository, organization_id=str(world.org),
            incident_id=str(world.incident), analysis_version=1,
        )

    async with connection.cursor() as cursor:
        await cursor.execute(
            "SELECT count(*) FROM ai_analyses WHERE incident_id = %s AND analysis_version = 1",
            (world.incident,),
        )
        assert (await cursor.fetchone())[0] == 1


@pytest.mark.asyncio
async def test_an_incident_in_another_organization_is_reported_as_missing(connection, settings, embedder):
    world = await seed_world(connection, embedder)

    with pytest.raises(IncidentNotFoundError):
        await AnalysisPipeline(settings, embedder).run(
            AnalysisRepository(connection),
            organization_id=str(uuid.uuid4()),
            incident_id=str(world.incident),
            analysis_version=1,
        )


@pytest.mark.asyncio
async def test_a_server_error_spike_incident_gathers_the_services_5xx_patterns(
    connection, settings, embedder
):
    world = await seed_world(connection, embedder, http_status=503, detection_rule="ServerErrorSpike")

    # A spike incident has no single pattern, which is exactly the case the
    # pattern lookup has to handle differently.
    async with connection.cursor() as cursor:
        await cursor.execute(
            "UPDATE incidents SET log_pattern_id = NULL WHERE id = %s", (world.incident,)
        )

    result = await AnalysisPipeline(settings, embedder).run(
        AnalysisRepository(connection),
        organization_id=str(world.org), incident_id=str(world.incident), analysis_version=1,
    )

    assert len(result.patterns) == 1
    assert result.patterns[0].http_status_code == 503
    # No single series means no honest baseline.
    assert result.anomaly is None
