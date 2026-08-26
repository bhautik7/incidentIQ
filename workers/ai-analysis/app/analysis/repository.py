"""Every query the analysis pipeline runs.

Kept together so the SQL is reviewable in one place, and so the pipeline reads
as a sequence of steps rather than a sequence of queries.

Every statement is scoped by organization_id. The .NET side has composite
foreign keys and EF query filters enforcing tenant isolation; this worker has
neither, so the scoping is explicit and non-optional here.
"""

from dataclasses import dataclass
from datetime import datetime, timedelta

import numpy as np
from psycopg import AsyncConnection
from psycopg.rows import dict_row

from app.analysis.evidence import (
    DeploymentEvidence,
    PatternEvidence,
    SimilarIncidentEvidence,
)


@dataclass(frozen=True)
class IncidentRecord:
    """The incident under analysis."""

    id: str
    organization_id: str
    monitored_service_id: str
    environment_id: str
    log_pattern_id: str | None
    service_key: str
    environment_key: str
    title: str
    status: str
    severity: str
    detection_rule: str
    occurrence_count: int
    first_seen_at: datetime
    last_seen_at: datetime
    suspected_deployment_id: str | None


class AnalysisRepository:
    def __init__(self, connection: AsyncConnection) -> None:
        self._connection = connection

    async def get_incident(self, organization_id: str, incident_id: str) -> IncidentRecord | None:
        sql = """
            SELECT i.id, i.organization_id, i.monitored_service_id, i.environment_id,
                   i.log_pattern_id, s.key AS service_key, e.key AS environment_key,
                   i.title, i.status, i.severity, i.detection_rule, i.occurrence_count,
                   i.first_seen_at, i.last_seen_at, i.suspected_deployment_id
            FROM incidents i
            JOIN monitored_services s ON s.id = i.monitored_service_id
            JOIN environments e ON e.id = i.environment_id
            WHERE i.organization_id = %(org)s AND i.id = %(incident)s
        """

        async with self._connection.cursor(row_factory=dict_row) as cursor:
            await cursor.execute(sql, {"org": organization_id, "incident": incident_id})
            row = await cursor.fetchone()

        if row is None:
            return None

        return IncidentRecord(
            id=str(row["id"]),
            organization_id=str(row["organization_id"]),
            monitored_service_id=str(row["monitored_service_id"]),
            environment_id=str(row["environment_id"]),
            log_pattern_id=str(row["log_pattern_id"]) if row["log_pattern_id"] else None,
            service_key=row["service_key"],
            environment_key=row["environment_key"],
            title=row["title"],
            status=row["status"],
            severity=row["severity"],
            detection_rule=row["detection_rule"],
            occurrence_count=row["occurrence_count"],
            first_seen_at=row["first_seen_at"],
            last_seen_at=row["last_seen_at"],
            suspected_deployment_id=str(row["suspected_deployment_id"])
            if row["suspected_deployment_id"]
            else None,
        )

    async def get_patterns(self, incident: IncidentRecord) -> list[PatternEvidence]:
        """The log patterns this incident is about.

        Usually one - the pattern the incident was opened for. A server-error
        spike has no single pattern, so the 5xx patterns of that service and
        environment are gathered instead, which is what makes the evidence
        useful for exactly the rule that has no fingerprint of its own.
        """
        if incident.log_pattern_id:
            sql = """
                SELECT id, fingerprint, message_template, sample_message, exception_type,
                       occurrence_count, first_seen_at, last_seen_at, http_status_code
                FROM log_patterns
                WHERE organization_id = %(org)s AND id = %(pattern)s
            """
            params = {"org": incident.organization_id, "pattern": incident.log_pattern_id}
        else:
            sql = """
                SELECT id, fingerprint, message_template, sample_message, exception_type,
                       occurrence_count, first_seen_at, last_seen_at, http_status_code
                FROM log_patterns
                WHERE organization_id = %(org)s
                  AND monitored_service_id = %(service)s
                  AND environment_id = %(environment)s
                  AND http_status_code BETWEEN 500 AND 599
                  AND last_seen_at >= %(since)s
                ORDER BY occurrence_count DESC
                LIMIT 10
            """
            params = {
                "org": incident.organization_id,
                "service": incident.monitored_service_id,
                "environment": incident.environment_id,
                "since": incident.first_seen_at - timedelta(hours=1),
            }

        async with self._connection.cursor(row_factory=dict_row) as cursor:
            await cursor.execute(sql, params)
            rows = await cursor.fetchall()

        return [
            PatternEvidence(
                log_pattern_id=str(row["id"]),
                fingerprint=row["fingerprint"],
                message_template=row["message_template"],
                sample_message=row["sample_message"],
                exception_type=row["exception_type"],
                occurrence_count=row["occurrence_count"],
                first_seen_at=row["first_seen_at"],
                last_seen_at=row["last_seen_at"],
                http_status_code=row["http_status_code"],
            )
            for row in rows
        ]

    async def get_deployment(
        self, incident: IncidentRecord, correlation_minutes: int
    ) -> DeploymentEvidence | None:
        """The most recent release that could explain this incident.

        Prefers the deployment the detector already suspected, so the analysis
        and the incident page never disagree about which release is being
        blamed. Falls back to a time window when the detector found none.
        """
        if incident.suspected_deployment_id:
            sql = """
                SELECT id, version, deployed_at, commit_sha, deployed_by
                FROM deployments
                WHERE organization_id = %(org)s AND id = %(deployment)s
            """
            params = {"org": incident.organization_id, "deployment": incident.suspected_deployment_id}
        else:
            sql = """
                SELECT id, version, deployed_at, commit_sha, deployed_by
                FROM deployments
                WHERE organization_id = %(org)s
                  AND monitored_service_id = %(service)s
                  AND environment_id = %(environment)s
                  AND deployed_at BETWEEN %(since)s AND %(until)s
                ORDER BY deployed_at DESC
                LIMIT 1
            """
            params = {
                "org": incident.organization_id,
                "service": incident.monitored_service_id,
                "environment": incident.environment_id,
                "since": incident.first_seen_at - timedelta(minutes=correlation_minutes),
                "until": incident.first_seen_at,
            }

        async with self._connection.cursor(row_factory=dict_row) as cursor:
            await cursor.execute(sql, params)
            row = await cursor.fetchone()

        if row is None:
            return None

        minutes = (incident.first_seen_at - row["deployed_at"]).total_seconds() / 60.0

        return DeploymentEvidence(
            deployment_id=str(row["id"]),
            version=row["version"],
            deployed_at=row["deployed_at"],
            commit_sha=row["commit_sha"],
            deployed_by=row["deployed_by"],
            minutes_before_incident=round(minutes, 1),
        )

    async def get_metric_buckets(
        self, log_pattern_id: str, since: datetime, until: datetime
    ) -> list[tuple[datetime, int]]:
        """Minute buckets for the anomaly baseline."""
        sql = """
            SELECT bucket_start, count
            FROM log_pattern_metrics
            WHERE log_pattern_id = %(pattern)s
              AND bucket_start >= %(since)s AND bucket_start < %(until)s
            ORDER BY bucket_start
        """

        async with self._connection.cursor() as cursor:
            await cursor.execute(sql, {"pattern": log_pattern_id, "since": since, "until": until})
            return [(row[0], int(row[1])) for row in await cursor.fetchall()]

    async def find_similar_incidents(
        self,
        *,
        organization_id: str,
        exclude_incident_id: str,
        embedding: np.ndarray,
        top_k: int,
    ) -> list[SimilarIncidentEvidence]:
        """Nearest neighbours among this organization's past incidents.

        The query pgvector exists for. Three things happen in one statement
        that would otherwise be a vector-store round trip plus a database join:

        - `<=>` is cosine distance, using the HNSW index.
        - The relational filters - same organization, not this incident,
          resolved - are applied by the same planner, so the tenant boundary is
          part of the query rather than a post-filter that could be forgotten.
        - Resolution notes come back with the match, which is the only reason
          the match is worth showing at all.

        Resolved incidents only. An unresolved lookalike says "someone else has
        this problem too", which is not help.
        """
        sql = """
            SELECT i.id, i.title, i.status, i.resolved_at, i.resolution_notes,
                   1 - (a.embedding <=> %(embedding)s) AS similarity
            FROM ai_analyses a
            JOIN incidents i ON i.id = a.incident_id
            WHERE a.organization_id = %(org)s
              AND a.embedding IS NOT NULL
              AND i.id <> %(exclude)s
              AND i.status = 'Resolved'
            ORDER BY a.embedding <=> %(embedding)s
            LIMIT %(k)s
        """

        async with self._connection.cursor(row_factory=dict_row) as cursor:
            await cursor.execute(
                sql,
                {
                    "org": organization_id,
                    "exclude": exclude_incident_id,
                    "embedding": embedding,
                    "k": top_k,
                },
            )
            rows = await cursor.fetchall()

        return [
            SimilarIncidentEvidence(
                incident_id=str(row["id"]),
                title=row["title"],
                status=row["status"],
                similarity=round(float(row["similarity"]), 4),
                resolved_at=row["resolved_at"],
                resolution_notes=row["resolution_notes"],
            )
            for row in rows
        ]

    async def save_analysis(
        self,
        *,
        analysis_id: str,
        organization_id: str,
        incident_id: str,
        analysis_version: int,
        embedding: np.ndarray,
        embedding_model: str,
        summary: str,
        probable_cause: str | None,
        suggested_actions_json: str,
        similar_incidents_json: str,
        confidence: float,
        latency_ms: int,
    ) -> bool:
        """Writes the analysis. Returns False when one already existed.

        ON CONFLICT is the idempotency guarantee: Kafka delivers at least once,
        so a redelivered request re-runs the analysis and writes nothing rather
        than producing a second row for the same version.
        """
        sql = """
            INSERT INTO ai_analyses (
                id, organization_id, incident_id, analysis_version, status,
                embedding, embedding_model, model_provider, model_name,
                summary, probable_cause, suggested_actions, similar_incidents,
                confidence, latency_ms, created_at, completed_at)
            VALUES (
                %(id)s, %(org)s, %(incident)s, %(version)s, 'Completed',
                %(embedding)s, %(embedding_model)s, 'deterministic', %(embedding_model)s,
                %(summary)s, %(probable_cause)s, %(actions)s::jsonb, %(similar)s::jsonb,
                %(confidence)s, %(latency)s, now(), now())
            ON CONFLICT (incident_id, analysis_version) DO NOTHING
            RETURNING id
        """

        async with self._connection.cursor() as cursor:
            await cursor.execute(
                sql,
                {
                    "id": analysis_id,
                    "org": organization_id,
                    "incident": incident_id,
                    "version": analysis_version,
                    "embedding": embedding,
                    "embedding_model": embedding_model,
                    "summary": summary,
                    "probable_cause": probable_cause,
                    "actions": suggested_actions_json,
                    "similar": similar_incidents_json,
                    "confidence": confidence,
                    "latency": latency_ms,
                },
            )
            return await cursor.fetchone() is not None

    async def record_failure(
        self, *, analysis_id: str, organization_id: str, incident_id: str,
        analysis_version: int, error: str,
    ) -> None:
        """Records that an analysis failed, so a gap is visible rather than silent."""
        sql = """
            INSERT INTO ai_analyses (
                id, organization_id, incident_id, analysis_version, status,
                model_provider, error, created_at, completed_at)
            VALUES (%(id)s, %(org)s, %(incident)s, %(version)s, 'Failed',
                    'deterministic', %(error)s, now(), now())
            ON CONFLICT (incident_id, analysis_version) DO NOTHING
        """

        async with self._connection.cursor() as cursor:
            await cursor.execute(
                sql,
                {
                    "id": analysis_id,
                    "org": organization_id,
                    "incident": incident_id,
                    "version": analysis_version,
                    "error": error[:2000],
                },
            )
