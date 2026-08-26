"""Envelope handling: what the worker accepts, and what it refuses outright."""

import uuid
from datetime import UTC, datetime

import pytest

from app.contracts import EventEnvelope, EventTypes, IncidentAnalysisRequested
from app.messaging.kafka import PermanentMessageError


class _ParserOnlyWorker:
    """Exercises the parsing rules without a database or a broker."""

    from app.worker import AnalysisWorker

    _parse = AnalysisWorker._parse


def envelope_json(*, version: int = 1) -> dict:
    envelope = EventEnvelope[IncidentAnalysisRequested](
        eventId=uuid.uuid4(),
        eventType=EventTypes.INCIDENT_ANALYSIS_REQUESTED,
        eventVersion=version,
        occurredAt=datetime.now(UTC),
        tenantId=uuid.uuid4(),
        correlationId=uuid.uuid4(),
        payload=IncidentAnalysisRequested(
            incidentId=uuid.uuid4(),
            analysisVersion=1,
            reason="detected",
            requestedAt=datetime.now(UTC),
        ),
    )
    return envelope.model_dump(mode="json", by_alias=True)


def test_a_well_formed_request_parses():
    parsed = _ParserOnlyWorker()._parse(envelope_json())

    assert parsed.payload.reason == "detected"
    assert parsed.event_version == 1


def test_an_unsupported_version_is_permanent_not_retryable():
    # No retry makes an unknown version knowable, so it must dead-letter rather
    # than block the partition forever.
    with pytest.raises(PermanentMessageError, match="Unsupported"):
        _ParserOnlyWorker()._parse(envelope_json(version=99))


def test_a_payload_missing_required_fields_is_permanent():
    broken = envelope_json()
    del broken["payload"]["incidentId"]

    with pytest.raises(PermanentMessageError, match="does not match the contract"):
        _ParserOnlyWorker()._parse(broken)


def test_an_envelope_missing_its_tenant_is_permanent():
    broken = envelope_json()
    del broken["tenantId"]

    with pytest.raises(PermanentMessageError):
        _ParserOnlyWorker()._parse(broken)


def test_unknown_fields_from_a_newer_producer_are_ignored():
    # Additive changes must deploy without a lockstep release of this worker.
    forward_compatible = envelope_json()
    forward_compatible["payload"]["somethingNew"] = "value"
    forward_compatible["unexpectedTopLevel"] = 42

    assert _ParserOnlyWorker()._parse(forward_compatible).payload.analysis_version == 1
