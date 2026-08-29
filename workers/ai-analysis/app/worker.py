"""The analysis worker: consume a request, run the pipeline, announce the result."""

import uuid
from datetime import UTC, datetime

import structlog
from prometheus_client import Counter, Histogram

from app.analysis.pipeline import AnalysisPipeline, IncidentNotFoundError
from app.analysis.repository import AnalysisRepository
from app.config import Settings
from app.contracts import (
    EventEnvelope,
    EventTypes,
    IncidentAnalysisCompleted,
    IncidentAnalysisRequested,
    Topics,
    partition_key_for_incident,
)
from app.db import Database
from app.messaging.kafka import EventConsumer, EventProducer, PermanentMessageError

logger = structlog.get_logger(__name__)

SUPPORTED_VERSION = 1

ANALYSES_COMPLETED = Counter(
    "incidentiq_analyses_completed_total", "Incident analyses completed.", ["outcome"]
)
ANALYSIS_DURATION = Histogram(
    "incidentiq_analysis_duration_seconds", "Time to analyse one incident."
)
SIMILAR_INCIDENTS_FOUND = Histogram(
    "incidentiq_similar_incidents_found",
    "Similar resolved incidents returned per analysis.",
    buckets=[0, 1, 2, 3, 5, 10],
)


class AnalysisWorker:
    def __init__(
        self,
        settings: Settings,
        database: Database,
        pipeline: AnalysisPipeline,
        producer: EventProducer,
    ) -> None:
        self._settings = settings
        self._database = database
        self._pipeline = pipeline
        self._producer = producer

        self._consumer = EventConsumer(
            settings,
            topic=Topics.INCIDENTS_ANALYSIS_REQUESTED,
            handler=self._handle,
            # The incident path's own dead-letter topic. This used to point at
            # LOGS_FAILED, which meant a dead analysis request - one incident
            # that will never be explained - was filed among millions of dead
            # log lines and triaged by nobody.
            dead_letter_topic=Topics.INCIDENTS_FAILED,
            producer=producer,
        )

    async def run(self) -> None:
        await self._consumer.run()

    def stop(self) -> None:
        self._consumer.stop()

    async def _handle(self, envelope_json: dict, headers: dict[str, str]) -> None:
        envelope = self._parse(envelope_json)
        request = envelope.payload

        log = logger.bind(
            incident_id=str(request.incident_id),
            analysis_version=request.analysis_version,
            correlation_id=str(envelope.correlation_id),
            reason=request.reason,
        )

        with ANALYSIS_DURATION.time():
            async with self._database.connection() as connection:
                repository = AnalysisRepository(connection)

                try:
                    result = await self._pipeline.run(
                        repository,
                        organization_id=str(envelope.tenant_id),
                        incident_id=str(request.incident_id),
                        analysis_version=request.analysis_version,
                    )
                except IncidentNotFoundError as error:
                    # No retry will make the incident appear, and it may belong
                    # to another tenant. Permanent by definition.
                    raise PermanentMessageError(str(error)) from error
                except Exception as error:  # noqa: BLE001
                    # Record the failure so a missing analysis is visible on the
                    # incident rather than silently absent, then let the
                    # consumer retry via redelivery.
                    await repository.record_failure(
                        analysis_id=str(uuid.uuid4()),
                        organization_id=str(envelope.tenant_id),
                        incident_id=str(request.incident_id),
                        analysis_version=request.analysis_version,
                        error=str(error),
                    )
                    await connection.commit()

                    ANALYSES_COMPLETED.labels(outcome="failed").inc()
                    self._publish_completed(envelope, request, status="Failed", error=str(error))

                    log.error("analysis_failed", error=str(error), exc_info=True)
                    raise

                await connection.commit()

        SIMILAR_INCIDENTS_FOUND.observe(len(result.similar_incidents))
        ANALYSES_COMPLETED.labels(outcome="completed").inc()

        self._publish_completed(
            envelope,
            request,
            status="Completed",
            confidence=result.confidence,
            similar_count=len(result.similar_incidents),
        )

        log.info(
            "analysis_completed",
            similar_incidents=len(result.similar_incidents),
            root_cause_candidates=len(result.root_cause_candidates),
            probable_cause=result.probable_cause,
            confidence=result.confidence,
            latency_ms=result.latency_ms,
        )

    def _parse(self, envelope_json: dict) -> EventEnvelope[IncidentAnalysisRequested]:
        try:
            envelope = EventEnvelope[IncidentAnalysisRequested].model_validate(envelope_json)
        except Exception as error:  # noqa: BLE001
            raise PermanentMessageError(f"Envelope does not match the contract: {error}") from error

        if envelope.event_version != SUPPORTED_VERSION:
            raise PermanentMessageError(
                f"Unsupported {envelope.event_type} version {envelope.event_version}; "
                f"this build handles v{SUPPORTED_VERSION}."
            )

        return envelope

    def _publish_completed(
        self,
        request_envelope: EventEnvelope[IncidentAnalysisRequested],
        request: IncidentAnalysisRequested,
        *,
        status: str,
        confidence: float | None = None,
        similar_count: int = 0,
        error: str | None = None,
    ) -> None:
        """Announces the outcome.

        The analysis itself - including the embedding - is already in
        PostgreSQL. This event only says that it happened, so anything that
        reacts does not have to poll. 384 floats have no business on a topic.
        """
        completed = EventEnvelope[IncidentAnalysisCompleted](
            eventId=uuid.uuid4(),
            eventType=EventTypes.INCIDENT_ANALYSIS_COMPLETED,
            eventVersion=1,
            occurredAt=datetime.now(UTC),
            tenantId=request_envelope.tenant_id,
            # Carried through, so one id traces the whole path from the log line
            # that opened the incident to this result.
            correlationId=request_envelope.correlation_id,
            payload=IncidentAnalysisCompleted(
                incidentId=request.incident_id,
                analysisId=uuid.uuid4(),
                analysisVersion=request.analysis_version,
                status=status,
                completedAt=datetime.now(UTC),
                modelName=self._settings.embedding_model,
                confidence=confidence,
                similarIncidentCount=similar_count,
                error=error,
            ),
        )

        self._producer.publish(
            Topics.INCIDENTS_ANALYSIS_COMPLETED,
            key=partition_key_for_incident(request_envelope.tenant_id, request.incident_id),
            payload=completed.to_wire(),
            headers={
                "event-id": str(completed.event_id),
                "event-type": completed.event_type,
                "event-version": str(completed.event_version),
                "tenant-id": str(completed.tenant_id),
                "correlation-id": str(completed.correlation_id),
            },
        )
