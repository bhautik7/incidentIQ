"""Neighbouring patterns: context for the model, not inputs to the arithmetic.

The pattern that crosses a detection threshold is the loudest one, and the
loudest one is usually a symptom. The evidence that names the cause tends to
sit beside it - same service, same minute, far fewer occurrences - which is
where a real 106-line outage log put "Cannot insert the value NULL into column
'Status'" while the incident was opened for connection-pool exhaustion.

Sending neighbours to the model is therefore the point. Letting them into the
deterministic scoring is not: those weights were chosen against the incident's
own pattern, and several of the checks are existential, so one unrelated
background pattern would answer a different question and change every score on
a busy service.
"""

from datetime import UTC, datetime, timedelta

from app.analysis import root_cause
from app.analysis.evidence import (
    AnalysisResult,
    DeploymentEvidence,
    PatternEvidence,
    RootCauseKind,
)
from app.llm.context import build_context_package

NOW = datetime(2026, 8, 26, 12, 0, tzinfo=UTC)


def pattern(**overrides) -> PatternEvidence:
    defaults = {
        "log_pattern_id": "p1",
        "fingerprint": "a" * 64,
        "message_template": "Timeout acquiring connection from pool",
        "sample_message": "Timeout acquiring connection from pool",
        "exception_type": "Npgsql.NpgsqlException",
        "occurrence_count": 400,
        "first_seen_at": NOW - timedelta(minutes=5),
        "last_seen_at": NOW,
        "http_status_code": None,
        "is_primary": True,
    }
    return PatternEvidence(**(defaults | overrides))


def deployment(minutes_before: float = 4.0) -> DeploymentEvidence:
    return DeploymentEvidence(
        deployment_id="d1",
        version="2.31.0",
        deployed_at=NOW - timedelta(minutes=minutes_before + 5),
        commit_sha="9f4c2ab7d31e05b6c8a1f2e3d4b5a6c7d8e9f001",
        minutes_before_incident=minutes_before,
    )


def rank(patterns: list[PatternEvidence], **overrides):
    kwargs = {
        "patterns": patterns,
        "deployment": deployment(),
        "anomaly": None,
        "similar_incidents": [],
        "detection_rule": "CountThreshold",
        "incident_first_seen_minutes_old": 5.0,
    }
    return root_cause.rank(**(kwargs | overrides))


def test_a_neighbour_does_not_weaken_the_deployment_hypothesis():
    """The check is "no occurrence of *this* pattern predates the release".

    An old neighbour is not counterevidence about the incident, and before the
    primary/context split it silently was: one background pattern from last
    week removed 0.10 of confidence from every incident on that service.
    """
    primary = pattern()
    old_neighbour = pattern(
        log_pattern_id="p2",
        fingerprint="b" * 64,
        is_primary=False,
        # Long predates the release under suspicion.
        first_seen_at=NOW - timedelta(days=7),
        occurrence_count=3,
    )

    alone = rank([primary])
    with_neighbour = rank([primary, old_neighbour])

    assert alone[0].kind is RootCauseKind.RECENT_DEPLOYMENT
    assert with_neighbour[0].kind is RootCauseKind.RECENT_DEPLOYMENT
    assert with_neighbour[0].confidence == alone[0].confidence


def test_a_neighbour_cannot_invent_a_dependency_hypothesis():
    """Dependency detection reads the message, so a neighbour would trigger it.

    "Connection refused" in a background pattern says nothing about an incident
    opened for something else, and a hypothesis raised on that basis would be
    ranked, shown, and wrong.
    """
    primary = pattern(message_template="Invalid column name {TOKEN}")
    noisy_neighbour = pattern(
        log_pattern_id="p2",
        fingerprint="c" * 64,
        is_primary=False,
        message_template="Connection refused talking to redis",
        occurrence_count=2,
    )

    kinds = {c.kind for c in rank([primary, noisy_neighbour], deployment=None)}

    assert RootCauseKind.DEPENDENCY_FAILURE not in kinds


def test_the_server_error_spike_still_scores_over_every_pattern():
    """That rule has no fingerprint of its own.

    Its patterns arrive unmarked, all equally the subject, and the fallback has
    to keep them all in scope or the rule loses its evidence entirely.
    """
    spike = [
        pattern(log_pattern_id="p1", fingerprint="d" * 64, is_primary=True,
                message_template="Upstream connection refused"),
        pattern(log_pattern_id="p2", fingerprint="e" * 64, is_primary=True,
                message_template="Gateway timeout"),
    ]

    kinds = {c.kind for c in rank(spike, deployment=None)}

    assert RootCauseKind.DEPENDENCY_FAILURE in kinds


def test_the_model_is_told_which_pattern_the_incident_is():
    """Otherwise a loud neighbour is indistinguishable from the fault."""
    primary = pattern()
    neighbour = pattern(
        log_pattern_id="p2",
        fingerprint="f" * 64,
        is_primary=False,
        message_template="Cannot insert the value NULL into column {TOKEN}",
        occurrence_count=5,
    )

    package = build_context_package(
        result=AnalysisResult(
            incident_id="i1",
            analysis_version=1,
            patterns=[primary, neighbour],
            deployment=None,
            anomaly=None,
            similar_incidents=[],
            root_cause_candidates=[],
        ),
        title="Timeout acquiring connection from pool",
        service="payments-api",
        environment="production",
        severity="High",
        detection_rule="CountThreshold",
        total_occurrences=400,
        first_seen_at=NOW - timedelta(minutes=5),
        last_seen_at=NOW,
    )

    assert [p.is_incident_pattern for p in package.patterns] == [True, False]

    # The neighbour has to survive the boundary, or the whole exercise is moot.
    assert any(
        "Cannot insert the value NULL" in p.normalized_message for p in package.patterns
    )
