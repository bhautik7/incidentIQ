"""Cross-language contract tests.

The fixtures in contracts/samples/ are generated from the C# types. If these
tests pass, a message produced by a .NET service is one this worker can read -
which is a guarantee no amount of documentation provides.
"""

import json
from datetime import UTC, datetime
from pathlib import Path
from uuid import UUID

import pytest

from app.contracts import (
    EventEnvelope,
    EventTypes,
    IncidentAnalysisCompleted,
    IncidentAnalysisRequested,
    IncidentDetected,
    LogReceived,
    partition_key_for_incident,
)

SAMPLES = Path(__file__).resolve().parents[3] / "contracts" / "samples"


def load(name: str) -> str:
    return (SAMPLES / f"{name}.json").read_text()


def test_samples_directory_is_present() -> None:
    # A wrong relative path would silently skip every test below.
    assert SAMPLES.is_dir(), f"contract samples not found at {SAMPLES}"
    assert (SAMPLES / "log-received.json").exists()


def test_envelope_fields_parse_from_dotnet_output() -> None:
    envelope = EventEnvelope[LogReceived].model_validate_json(load("log-received"))

    assert envelope.event_id == UUID("e0000000-0000-0000-0000-000000000001")
    assert envelope.event_type == EventTypes.LOG_RECEIVED
    assert envelope.event_version == 1
    assert envelope.tenant_id == UUID("11111111-1111-1111-1111-111111111111")
    assert envelope.correlation_id == UUID("c0000000-0000-0000-0000-00000000c001")
    # .NET writes "+00:00"; it must land as an aware UTC datetime, not naive.
    assert envelope.occurred_at == datetime(2026, 8, 24, 2, 14, 7, 221000, tzinfo=UTC)


def test_log_received_payload_round_trips() -> None:
    envelope = EventEnvelope[LogReceived].model_validate_json(load("log-received"))
    payload = envelope.payload

    assert payload.service == "payments-api"
    assert payload.environment == "production"
    assert payload.exception_type == "Npgsql.NpgsqlException"
    assert payload.properties == {"deploymentVersion": "2.31.0"}


def test_incident_detected_parses() -> None:
    envelope = EventEnvelope[IncidentDetected].model_validate_json(load("incident-detected"))

    assert envelope.payload.severity == "Critical"
    assert envelope.payload.title == "payments-api: connection pool exhausted"


def test_analysis_request_and_result_parse() -> None:
    requested = EventEnvelope[IncidentAnalysisRequested].model_validate_json(
        load("incident-analysis-requested")
    )
    completed = EventEnvelope[IncidentAnalysisCompleted].model_validate_json(
        load("incident-analysis-completed")
    )

    assert requested.payload.analysis_version == 1
    assert requested.payload.reason == "detected"
    assert completed.payload.status == "Completed"
    assert completed.payload.model_name == "claude-sonnet-5"
    assert float(completed.payload.confidence) == pytest.approx(0.870)
    assert completed.payload.similar_incident_count == 3
    # Both sides of the request/response pair are about the same incident.
    assert requested.payload.incident_id == completed.payload.incident_id


def test_python_output_uses_the_camel_case_wire_names() -> None:
    envelope = EventEnvelope[IncidentAnalysisCompleted].model_validate_json(
        load("incident-analysis-completed")
    )

    emitted = json.loads(envelope.to_wire())

    # Without by_alias this would emit snake_case and no .NET consumer would
    # understand a single field.
    assert set(emitted) == {
        "eventId",
        "eventType",
        "eventVersion",
        "occurredAt",
        "tenantId",
        "correlationId",
        "payload",
    }
    assert "analysisVersion" in emitted["payload"]
    assert "similarIncidentCount" in emitted["payload"]


def test_unknown_fields_are_ignored_so_producers_can_add_them() -> None:
    document = json.loads(load("incident-analysis-requested"))
    document["payload"]["fieldFromANewerProducer"] = "hello"
    document["somethingElseEntirely"] = 42

    envelope = EventEnvelope[IncidentAnalysisRequested].model_validate_json(json.dumps(document))

    # Additive changes must not require a lockstep release of every consumer.
    assert envelope.payload.analysis_version == 1


def test_incident_partition_key_matches_the_dotnet_format() -> None:
    tenant = UUID("11111111-1111-1111-1111-111111111111")
    incident = UUID("11111111-0000-0000-0000-0000000000e1")

    assert (
        partition_key_for_incident(tenant, incident)
        == "11111111-1111-1111-1111-111111111111:11111111-0000-0000-0000-0000000000e1"
    )
