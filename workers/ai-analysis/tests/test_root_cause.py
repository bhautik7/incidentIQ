"""Root-cause ranking: fixed arithmetic over gathered evidence."""

from datetime import UTC, datetime, timedelta

from app.analysis import root_cause
from app.analysis.evidence import (
    AnomalyEvidence,
    DeploymentEvidence,
    PatternEvidence,
    RootCauseKind,
    SimilarIncidentEvidence,
)

NOW = datetime(2026, 8, 26, 12, 0, tzinfo=UTC)


def pattern(**overrides) -> PatternEvidence:
    defaults = {
        "log_pattern_id": "p1",
        "fingerprint": "f" * 64,
        "message_template": "Connection timeout for user {NUM}",
        "sample_message": "Connection timeout for user 18273",
        "exception_type": "System.TimeoutException",
        "occurrence_count": 400,
        "first_seen_at": NOW - timedelta(minutes=5),
        "last_seen_at": NOW,
        "http_status_code": None,
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


def anomaly(magnitude: float = 20.0) -> AnomalyEvidence:
    return AnomalyEvidence(
        window_count=200,
        baseline_mean_per_minute=2.0,
        window_rate_per_minute=40.0,
        magnitude=magnitude,
        robust_z_score=12.0,
        is_outlier=True,
        outlier_score=-0.3,
        baseline_sample_count=60,
    )


def similar(
    similarity: float = 0.91,
    notes: str | None = "Reverted the DbContext lifetime change.",
) -> SimilarIncidentEvidence:
    return SimilarIncidentEvidence(
        incident_id="i-old",
        title="Pool exhaustion after DI refactor",
        status="Resolved",
        similarity=similarity,
        resolved_at=NOW - timedelta(days=120),
        resolution_notes=notes,
    )


def test_a_recent_deployment_is_ranked_first():
    candidates = root_cause.rank(
        patterns=[pattern()],
        deployment=deployment(minutes_before=4),
        anomaly=anomaly(),
        similar_incidents=[],
        detection_rule="NewErrorAfterDeployment",
        incident_first_seen_minutes_old=5,
    )

    assert candidates[0].kind == RootCauseKind.RECENT_DEPLOYMENT
    assert candidates[0].confidence >= 0.8


def test_deployment_confidence_decays_with_distance_in_time():
    close = root_cause.rank(
        patterns=[pattern()], deployment=deployment(4), anomaly=None,
        similar_incidents=[], detection_rule="CountThreshold",
        incident_first_seen_minutes_old=5,
    )[0]

    far = root_cause.rank(
        patterns=[pattern()], deployment=deployment(50), anomaly=None,
        similar_incidents=[], detection_rule="CountThreshold",
        incident_first_seen_minutes_old=5,
    )[0]

    # A release 50 minutes earlier is context; one 4 minutes earlier is a suspect.
    assert close.confidence > far.confidence


def test_the_detector_agreeing_raises_deployment_confidence():
    generic = root_cause.rank(
        patterns=[pattern()], deployment=deployment(4), anomaly=None,
        similar_incidents=[], detection_rule="CountThreshold",
        incident_first_seen_minutes_old=5,
    )[0]

    post_deploy = root_cause.rank(
        patterns=[pattern()], deployment=deployment(4), anomaly=None,
        similar_incidents=[], detection_rule="NewErrorAfterDeployment",
        incident_first_seen_minutes_old=5,
    )[0]

    # Two independent signals agreeing is worth more than either alone.
    assert post_deploy.confidence > generic.confidence


def test_a_matching_past_incident_carries_its_resolution_forward():
    candidates = root_cause.rank(
        patterns=[pattern()], deployment=None, anomaly=None,
        similar_incidents=[similar()], detection_rule="CountThreshold",
        incident_first_seen_minutes_old=5,
    )

    known = next(c for c in candidates if c.kind == RootCauseKind.KNOWN_PAST_INCIDENT)

    # The only reason to show a match at all is the answer attached to it.
    assert any("Reverted the DbContext" in e for e in known.supporting_evidence)


def test_a_similar_incident_without_resolution_notes_is_not_offered_as_a_cause():
    # "Someone else had this too" is not help.
    candidates = root_cause.rank(
        patterns=[pattern()], deployment=None, anomaly=None,
        similar_incidents=[similar(notes=None)], detection_rule="CountThreshold",
        incident_first_seen_minutes_old=5,
    )

    assert all(c.kind != RootCauseKind.KNOWN_PAST_INCIDENT for c in candidates)


def test_confidence_tracks_similarity():
    strong = root_cause.rank(
        patterns=[pattern()], deployment=None, anomaly=None,
        similar_incidents=[similar(0.95)], detection_rule="CountThreshold",
        incident_first_seen_minutes_old=5,
    )[0]

    weak = root_cause.rank(
        patterns=[pattern()], deployment=None, anomaly=None,
        similar_incidents=[similar(0.60)], detection_rule="CountThreshold",
        incident_first_seen_minutes_old=5,
    )[0]

    assert strong.confidence > weak.confidence


def test_a_rate_change_alone_never_outranks_evidence_that_names_a_cause():
    candidates = root_cause.rank(
        patterns=[pattern()], deployment=deployment(4), anomaly=anomaly(100.0),
        similar_incidents=[], detection_rule="CountThreshold",
        incident_first_seen_minutes_old=5,
    )

    rate = next(c for c in candidates if c.kind == RootCauseKind.SUDDEN_RATE_CHANGE)

    # A rate change says something changed, never what. It must not lead.
    assert rate.confidence < candidates[0].confidence
    assert rate.confidence <= 0.5


def test_a_modest_rate_change_produces_no_candidate_at_all():
    candidates = root_cause.rank(
        patterns=[pattern()], deployment=None, anomaly=anomaly(1.5),
        similar_incidents=[], detection_rule="CountThreshold",
        incident_first_seen_minutes_old=5,
    )

    assert all(c.kind != RootCauseKind.SUDDEN_RATE_CHANGE for c in candidates)


def test_gateway_errors_are_attributed_to_a_dependency():
    candidates = root_cause.rank(
        patterns=[pattern(http_status_code=503, exception_type="System.TimeoutException")],
        deployment=None, anomaly=None, similar_incidents=[],
        detection_rule="ServerErrorSpike", incident_first_seen_minutes_old=5,
    )

    assert any(c.kind == RootCauseKind.DEPENDENCY_FAILURE for c in candidates)


def test_a_bare_timeout_is_not_blamed_on_a_dependency():
    # "timeout" is the least specific word in this domain: a connection-pool
    # timeout, a lock timeout and a slow query all use it, and none of them is
    # a downstream dependency. Claiming otherwise is a confident wrong answer.
    candidates = root_cause.rank(
        patterns=[pattern(
            http_status_code=None,
            exception_type="System.TimeoutException",
            message_template="Could not acquire a database connection before the timeout elapsed",
        )],
        deployment=None, anomaly=None, similar_incidents=[],
        detection_rule="CountThreshold", incident_first_seen_minutes_old=5,
    )

    assert all(c.kind != RootCauseKind.DEPENDENCY_FAILURE for c in candidates)


def test_a_connection_refused_error_is_blamed_on_a_dependency():
    candidates = root_cause.rank(
        patterns=[pattern(
            http_status_code=None,
            exception_type="System.Net.Sockets.SocketException",
            message_template="Connection refused to {IP}",
        )],
        deployment=None, anomaly=None, similar_incidents=[],
        detection_rule="CountThreshold", incident_first_seen_minutes_old=5,
    )

    assert any(c.kind == RootCauseKind.DEPENDENCY_FAILURE for c in candidates)


def test_dependency_evidence_never_claims_status_codes_it_did_not_observe():
    candidates = root_cause.rank(
        patterns=[pattern(http_status_code=None, message_template="Connection refused to {IP}")],
        deployment=None, anomaly=None, similar_incidents=[],
        detection_rule="CountThreshold", incident_first_seen_minutes_old=5,
    )

    dependency = next(c for c in candidates if c.kind == RootCauseKind.DEPENDENCY_FAILURE)

    assert all("status codes observed: ." not in e for e in dependency.supporting_evidence)


def test_a_novel_failure_with_no_precedent_is_named_as_such():
    candidates = root_cause.rank(
        patterns=[pattern(exception_type="Contoso.BrandNewException", message_template="something odd")],
        deployment=None, anomaly=None, similar_incidents=[],
        detection_rule="CountThreshold", incident_first_seen_minutes_old=5,
    )

    assert any(c.kind == RootCauseKind.NEW_FAILURE_MODE for c in candidates)


def test_candidates_come_back_ordered_by_confidence():
    candidates = root_cause.rank(
        patterns=[pattern(http_status_code=503)],
        deployment=deployment(4), anomaly=anomaly(),
        similar_incidents=[similar()], detection_rule="NewErrorAfterDeployment",
        incident_first_seen_minutes_old=5,
    )

    confidences = [c.confidence for c in candidates]
    assert confidences == sorted(confidences, reverse=True)


def test_no_evidence_produces_no_invented_candidates():
    candidates = root_cause.rank(
        patterns=[], deployment=None, anomaly=None, similar_incidents=[],
        detection_rule="CountThreshold", incident_first_seen_minutes_old=5,
    )

    assert candidates == []
