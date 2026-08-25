"""Wire contracts for Kafka messages, mirroring services/contracts in .NET.

These are the Python half of a cross-language contract. The authoritative
format is the JSON in ``contracts/samples/``; both languages are tested against
those same files, so a drift between them fails a test rather than a consumer.

Field names are snake_case here and PascalCase in C#. Neither dictates the wire
format - the ``alias`` on each field does, and it is always camelCase.
"""

from datetime import datetime
from decimal import Decimal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field

# ---------------------------------------------------------------------------
# Topics, event types and consumer groups
# ---------------------------------------------------------------------------


class Topics:
    LOGS_RAW = "logs.raw"
    LOGS_NORMALIZED = "logs.normalized"
    LOGS_FAILED = "logs.failed"
    DEPLOYMENTS_CREATED = "deployments.created"
    INCIDENTS_DETECTED = "incidents.detected"
    INCIDENTS_ANALYSIS_REQUESTED = "incidents.analysis.requested"
    INCIDENTS_ANALYSIS_COMPLETED = "incidents.analysis.completed"


class EventTypes:
    LOG_RECEIVED = "log.received"
    LOG_NORMALIZED = "log.normalized"
    LOG_FAILED = "log.failed"
    DEPLOYMENT_CREATED = "deployment.created"
    INCIDENT_DETECTED = "incident.detected"
    INCIDENT_ANALYSIS_REQUESTED = "incident.analysis.requested"
    INCIDENT_ANALYSIS_COMPLETED = "incident.analysis.completed"


class ConsumerGroups:
    AI_ENRICHER = "ai-enricher"


# ---------------------------------------------------------------------------
# Envelope
# ---------------------------------------------------------------------------

class _Contract(BaseModel):
    """Base for every wire type.

    ``populate_by_name`` allows construction with either the Python name or the
    alias. ``extra="ignore"`` is what makes additive changes safe: a newer
    producer can add a field without breaking this consumer.
    """

    model_config = ConfigDict(populate_by_name=True, extra="ignore")


class EventEnvelope[PayloadT: BaseModel](_Contract):
    event_id: UUID = Field(alias="eventId")
    event_type: str = Field(alias="eventType")
    event_version: int = Field(alias="eventVersion")
    occurred_at: datetime = Field(alias="occurredAt")
    tenant_id: UUID = Field(alias="tenantId")
    correlation_id: UUID = Field(alias="correlationId")
    payload: PayloadT

    def to_wire(self) -> str:
        """Serialise using the aliases, omitting unset optional fields.

        ``by_alias=True`` is not optional - without it this would emit
        snake_case and no .NET consumer would understand a word of it.
        """
        return self.model_dump_json(by_alias=True, exclude_none=True)


# ---------------------------------------------------------------------------
# Payloads the AI worker consumes and produces
# ---------------------------------------------------------------------------


class IncidentDetected(_Contract):
    """`incidents.detected`. A thin pointer: full state is read from PostgreSQL."""

    incident_id: UUID = Field(alias="incidentId")
    log_pattern_id: UUID = Field(alias="logPatternId")
    service: str
    environment: str
    title: str
    severity: str
    first_seen_at: datetime = Field(alias="firstSeenAt")


class IncidentAnalysisRequested(_Contract):
    """`incidents.analysis.requested` - the AI worker's work queue.

    ``analysis_version`` with the incident id is the idempotency key, so a
    redelivered request re-runs the analysis and writes nothing.
    """

    incident_id: UUID = Field(alias="incidentId")
    analysis_version: int = Field(alias="analysisVersion")
    reason: str
    requested_at: datetime = Field(alias="requestedAt")


class IncidentAnalysisCompleted(_Contract):
    """`incidents.analysis.completed`.

    Announces that the worker wrote its result to PostgreSQL. The embedding
    itself stays in the database - 1,536 floats have no business on a topic.
    """

    incident_id: UUID = Field(alias="incidentId")
    analysis_id: UUID = Field(alias="analysisId")
    analysis_version: int = Field(alias="analysisVersion")
    status: str
    completed_at: datetime = Field(alias="completedAt")
    model_name: str | None = Field(default=None, alias="modelName")
    confidence: Decimal | None = None
    similar_incident_count: int = Field(default=0, alias="similarIncidentCount")
    error: str | None = None


class LogReceived(_Contract):
    """`logs.raw`. Modelled here so the worker can read the log stream if needed."""

    log_event_id: UUID = Field(alias="logEventId")
    service: str
    environment: str
    level: str
    message: str
    timestamp: datetime
    exception_type: str | None = Field(default=None, alias="exceptionType")
    stack_trace: str | None = Field(default=None, alias="stackTrace")
    trace_id: str | None = Field(default=None, alias="traceId")
    span_id: str | None = Field(default=None, alias="spanId")
    host: str | None = None
    properties: dict[str, str] | None = None


def partition_key_for_incident(tenant_id: UUID, incident_id: UUID) -> str:
    """Keeps one incident's lifecycle events ordered on a single partition."""
    return f"{tenant_id}:{incident_id}"


def partition_key_for_service(tenant_id: UUID, service: str) -> str:
    """Keeps one service's log events on a single partition, and one consumer."""
    return f"{tenant_id}:{service}"
