"""Tests for the boundary that keeps raw logs away from the model.

These are the highest-stakes tests in the worker. Everything else being wrong
costs an inaccurate paragraph; this being wrong sends customer data to a third
party.
"""

from datetime import UTC, datetime

import pytest
from pydantic import ValidationError

from app.analysis.evidence import (
    AnalysisResult,
    AnomalyEvidence,
    DeploymentEvidence,
    PatternEvidence,
    SimilarIncidentEvidence,
)
from app.llm.context import (
    ContextPackage,
    ContextRedactionError,
    assert_safe_to_send,
    build_context_package,
    scan_for_sensitive_content,
)

NOW = datetime(2026, 8, 26, 12, 0, tzinfo=UTC)

#: A raw log line of the kind that must never leave. Every field here is the
#: sort of thing applications really do write to logs.
RAW_MESSAGE = (
    "Connection timeout for user 18273 (alice.smith@acme.com) "
    "from 10.0.14.221 token=eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTYifQ "
    "order 4532015112830366"
)

NORMALIZED_MESSAGE = "Connection timeout for user {NUM} ({EMAIL}) from {IP} token={HEX} order {NUM}"


def _pattern(**overrides) -> PatternEvidence:
    defaults = dict(
        log_pattern_id="11111111-1111-1111-1111-111111111111",
        fingerprint="a" * 64,
        message_template=NORMALIZED_MESSAGE,
        sample_message=RAW_MESSAGE,
        exception_type="System.TimeoutException",
        occurrence_count=412,
        first_seen_at=NOW,
        last_seen_at=NOW,
        http_status_code=503,
    )
    return PatternEvidence(**{**defaults, **overrides})


def _result(**overrides) -> AnalysisResult:
    defaults = dict(
        incident_id="22222222-2222-2222-2222-222222222222",
        analysis_version=1,
        patterns=[_pattern()],
        deployment=DeploymentEvidence(
            deployment_id="33333333-3333-3333-3333-333333333333",
            version="2.31.0",
            deployed_at=NOW,
            commit_sha="9f4c2ab",
            minutes_before_incident=3.0,
        ),
        anomaly=AnomalyEvidence(
            window_count=412,
            baseline_mean_per_minute=0.4,
            window_rate_per_minute=82.4,
            magnitude=206.0,
            robust_z_score=14.2,
            is_outlier=True,
            outlier_score=-0.31,
            baseline_sample_count=60,
        ),
    )
    return AnalysisResult(**{**defaults, **overrides})


def _build(result: AnalysisResult) -> ContextPackage:
    return build_context_package(
        result=result,
        title="TimeoutException: Connection timeout for user {NUM}",
        service="payments-api",
        environment="production",
        severity="Critical",
        detection_rule="NewErrorAfterDeployment",
        total_occurrences=412,
        first_seen_at=NOW,
        last_seen_at=NOW,
    )


# ---------------------------------------------------------------------------
# The core guarantee
# ---------------------------------------------------------------------------


def test_raw_sample_message_never_reaches_the_package():
    package = _build(_result())
    payload = package.to_prompt_json()

    # The whole point. The raw line exists on the evidence and must not survive
    # the crossing.
    assert RAW_MESSAGE not in payload
    assert "alice.smith@acme.com" not in payload
    assert "10.0.14.221" not in payload
    assert "eyJhbGciOiJIUzI1NiJ9" not in payload
    assert "4532015112830366" not in payload

    # The masked template does survive - that is what makes the package useful.
    assert NORMALIZED_MESSAGE in payload


def test_the_package_type_cannot_hold_a_raw_message():
    """Structural, not behavioural.

    The guarantee is that there is no field to put a raw message in, so no
    future edit can populate one by accident.
    """
    fields = set(ContextPackage.model_fields)
    assert fields == {
        "incident",
        "patterns",
        "deployment",
        "occurrence_rate_per_minute",
        "baseline_rate_per_minute",
        "anomaly_magnitude",
    }

    from app.llm.context import ContextPatternSummary

    pattern_fields = set(ContextPatternSummary.model_fields)
    for forbidden in ("sample_message", "stack_trace", "trace_id", "span_id", "host", "properties"):
        assert forbidden not in pattern_fields


def test_the_package_rejects_unknown_fields():
    with pytest.raises(ValidationError):
        ContextPackage(
            incident=_build(_result()).incident,
            sample_message=RAW_MESSAGE,  # type: ignore[call-arg]
        )


def test_similar_incidents_are_not_included():
    """Resolution notes are free text a human typed.

    They are the most useful thing the similarity search finds, and they are
    still excluded: the agreed boundary is four categories, and human-written
    notes routinely contain hostnames, ticket links and customer names.
    """
    result = _result(
        similar_incidents=[
            SimilarIncidentEvidence(
                incident_id="44444444-4444-4444-4444-444444444444",
                title="Pool exhaustion after DI refactor",
                status="Resolved",
                similarity=0.91,
                resolved_at=NOW,
                resolution_notes="Reverted; contact bob@internal.example.com about the runbook",
            )
        ]
    )

    payload = _build(result).to_prompt_json()

    assert "bob@internal.example.com" not in payload
    assert "Pool exhaustion after DI refactor" not in payload


# ---------------------------------------------------------------------------
# The scanner
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("text", "expected"),
    [
        ("contact alice@acme.com", "email address"),
        ("refused by 10.0.14.221", "IPv4 address"),
        ("id 3f2a9c1e-1234-4567-89ab-cdef01234567", "UUID"),
        ("Authorization: Bearer sk-abcdefghijklmnop123456", "bearer token"),
        ("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTYifQ", "JWT"),
        ("key AKIAIOSFODNN7EXAMPLE", "AWS access key"),
        ("Password=hunter2;Database=x", "password in a connection string"),
        ("api_key: abcd1234efgh5678", "credential-shaped assignment"),
        ("card 4532015112830366", "long credit-card-like number"),
        ("-----BEGIN RSA PRIVATE KEY-----", "private key block"),
    ],
)
def test_scanner_catches_sensitive_shapes(text: str, expected: str):
    assert expected in scan_for_sensitive_content(text)


def test_scanner_ignores_the_placeholders_normalisation_leaves_behind():
    """A package full of {EMAIL} and {IP} is working correctly.

    Without this, the scanner would reject every well-normalised package it was
    built to approve.
    """
    normalized = "Connection timeout for user {NUM} ({EMAIL}) from {IP} at {TIMESTAMP}"

    assert scan_for_sensitive_content(normalized) == []


def test_scanner_accepts_a_well_formed_package():
    assert scan_for_sensitive_content(_build(_result()).to_prompt_json()) == []


def test_send_gate_refuses_a_package_that_slipped_something_through():
    """The second layer, tested by defeating the first.

    A normaliser miss that leaves a real email in the *template* would pass the
    whitelist - the field is allowed - and must still be stopped.
    """
    leaky = _result(patterns=[_pattern(message_template="Failed to notify alice@acme.com")])
    package = _build(leaky)

    with pytest.raises(ContextRedactionError) as caught:
        assert_safe_to_send(package)

    assert "email address" in caught.value.matches


def test_send_gate_returns_the_payload_when_clean():
    payload = assert_safe_to_send(_build(_result()))

    assert "payments-api" in payload
    assert "2.31.0" in payload


def test_rejection_does_not_log_the_matched_value(caplog):
    """A leak moved from the API call to the log file is still a leak."""
    leaky = _result(patterns=[_pattern(message_template="notify alice@acme.com")])

    with pytest.raises(ContextRedactionError):
        assert_safe_to_send(_build(leaky))

    assert "alice@acme.com" not in caplog.text


# ---------------------------------------------------------------------------
# The four permitted categories
# ---------------------------------------------------------------------------


def test_package_carries_the_incident_summary():
    incident = _build(_result()).incident

    assert incident.service == "payments-api"
    assert incident.environment == "production"
    assert incident.severity == "Critical"
    assert incident.total_occurrences == 412


def test_package_carries_normalized_patterns_and_counts():
    package = _build(_result())
    pattern = package.patterns[0]

    assert pattern.normalized_message == NORMALIZED_MESSAGE
    assert pattern.occurrence_count == 412
    assert pattern.fingerprint_prefix == "a" * 12
    assert len(pattern.fingerprint_prefix) == 12


def test_package_carries_the_recent_deployment():
    deployment = _build(_result()).deployment

    assert deployment is not None
    assert deployment.version == "2.31.0"
    assert deployment.minutes_before_incident == 3.0


def test_package_carries_anomaly_magnitude_as_numbers_only():
    package = _build(_result())

    assert package.anomaly_magnitude == 206.0
    assert package.occurrence_rate_per_minute == 82.4
    assert package.baseline_rate_per_minute == 0.4


def test_package_handles_an_incident_with_no_deployment():
    package = _build(_result(deployment=None))

    assert package.deployment is None
    assert "deployment" not in package.to_prompt_json()


def test_pattern_count_is_capped():
    """Bounds the prompt regardless of how many patterns a spike produced."""
    many = _result(patterns=[_pattern(fingerprint=f"{i:064d}") for i in range(50)])

    assert len(_build(many).patterns) == 10
