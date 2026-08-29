"""Turning gathered evidence into ranked hypotheses.

Fixed arithmetic, not a model. Every confidence below is a number someone can
look at, disagree with, and change - which is the property that matters most
while there is no labelled data to learn better weights from.

The weights encode judgements about the domain, and they are worth stating
plainly:

- A deployment minutes before a *new* failure is the strongest evidence
  available, because that is how most regressions actually arrive.
- A closely-matching past incident *with resolution notes* is nearly as strong,
  because it comes with the answer attached.
- A large rate change is real evidence but weaker on its own: it says something
  changed, not what.
"""

from app.analysis.evidence import (
    AnomalyEvidence,
    DeploymentEvidence,
    PatternEvidence,
    RootCauseCandidate,
    RootCauseKind,
    SimilarIncidentEvidence,
)

#: Inside this window a release is a prime suspect rather than background.
STRONG_DEPLOYMENT_WINDOW_MINUTES = 15

#: A match this close is worth leading with.
STRONG_SIMILARITY = 0.80

#: Rate multiple above which "something changed" is worth saying out loud.
NOTABLE_MAGNITUDE = 3.0

#: Words that genuinely indicate a failure originating outside this service.
#: Kept narrow on purpose - see _looks_like_dependency_failure.
DEPENDENCY_MARKERS = (
    "refused",
    "unreachable",
    "gateway",
    "no route to host",
    "name resolution",
    "connection reset",
    "service unavailable",
)


def rank(
    *,
    patterns: list[PatternEvidence],
    deployment: DeploymentEvidence | None,
    anomaly: AnomalyEvidence | None,
    similar_incidents: list[SimilarIncidentEvidence],
    detection_rule: str,
    incident_first_seen_minutes_old: float,
) -> list[RootCauseCandidate]:
    candidates: list[RootCauseCandidate] = []

    # Every weight below was chosen against the pattern the incident was opened
    # for, and several of these tests are existential: "no occurrence predates
    # the release", "this looks like a dependency failure". Evaluated over the
    # neighbouring patterns as well, they would quietly answer a different
    # question - one unrelated pattern that started last week would defeat the
    # deployment check for every incident on a busy service.
    #
    # Neighbours are context for the model, not inputs to this arithmetic.
    # Falling back to the full list keeps behaviour intact for the server-error
    # spike, where every pattern is equally the subject.
    patterns = [p for p in patterns if p.is_primary] or patterns

    if deployment is not None:
        candidates.append(_deployment_candidate(deployment, patterns, detection_rule))

    resolved_matches = [s for s in similar_incidents if s.resolution_notes]

    if resolved_matches:
        candidates.append(_past_incident_candidate(resolved_matches[0]))

    if anomaly is not None and anomaly.magnitude >= NOTABLE_MAGNITUDE:
        candidates.append(_rate_change_candidate(anomaly))

    if _is_new_failure_mode(patterns, incident_first_seen_minutes_old) and not resolved_matches:
        candidates.append(_new_failure_candidate(patterns))

    if _looks_like_dependency_failure(patterns):
        candidates.append(_dependency_candidate(patterns))

    # Highest confidence first; that ordering is what the UI and, later, the
    # prompt will both rely on.
    return sorted(candidates, key=lambda c: c.confidence, reverse=True)


def _deployment_candidate(
    deployment: DeploymentEvidence,
    patterns: list[PatternEvidence],
    detection_rule: str,
) -> RootCauseCandidate:
    minutes = deployment.minutes_before_incident
    evidence = [
        f"Release {deployment.version} went out {minutes:.0f} minute(s) before the first occurrence."
    ]

    # Base confidence decays with distance in time: a release four minutes
    # earlier is evidence, one fifty minutes earlier is context.
    if minutes <= STRONG_DEPLOYMENT_WINDOW_MINUTES:
        confidence = 0.75
    elif minutes <= 30:
        confidence = 0.55
    else:
        confidence = 0.35

    # The detector already concluded this was a post-deployment regression by a
    # different route. Two independent signals agreeing is worth more than
    # either alone.
    if detection_rule == "NewErrorAfterDeployment":
        confidence = min(confidence + 0.15, 0.95)
        evidence.append("The incident was opened by the post-deployment rule.")

    if patterns and all(p.first_seen_at >= deployment.deployed_at for p in patterns):
        confidence = min(confidence + 0.10, 0.95)
        evidence.append("No occurrence of this pattern predates the release.")

    if deployment.commit_sha:
        evidence.append(f"Commit {deployment.commit_sha[:12]}.")

    return RootCauseCandidate(
        kind=RootCauseKind.RECENT_DEPLOYMENT,
        confidence=round(confidence, 2),
        summary=f"Regression introduced by release {deployment.version}.",
        supporting_evidence=evidence,
    )


def _past_incident_candidate(match: SimilarIncidentEvidence) -> RootCauseCandidate:
    # Confidence tracks the similarity score directly, capped: a 0.99 match is
    # not a certainty, it is a very good lead.
    confidence = min(0.30 + match.similarity * 0.6, 0.90)

    evidence = [
        f"{match.similarity:.0%} similar to a previously resolved incident: \"{match.title}\".",
    ]

    if match.resolution_notes:
        evidence.append(f"That incident was resolved by: {match.resolution_notes}")

    if match.similarity >= STRONG_SIMILARITY:
        evidence.append("Similarity is high enough that the same fix is likely to apply.")

    return RootCauseCandidate(
        kind=RootCauseKind.KNOWN_PAST_INCIDENT,
        confidence=round(confidence, 2),
        summary=f"Recurrence of a known problem: {match.title}",
        supporting_evidence=evidence,
    )


def _rate_change_candidate(anomaly: AnomalyEvidence) -> RootCauseCandidate:
    # Deliberately capped low. A rate change establishes that something
    # changed, never what changed, so it must not outrank evidence that names
    # a cause.
    confidence = 0.30 + min(anomaly.magnitude / 100.0, 0.20)

    evidence = [
        f"Rate is {anomaly.magnitude:.1f}x the baseline "
        f"({anomaly.window_rate_per_minute:.1f}/min against {anomaly.baseline_mean_per_minute:.2f}/min).",
        f"Robust z-score {anomaly.robust_z_score:.1f} over {anomaly.baseline_sample_count} baseline bucket(s).",
    ]

    if anomaly.is_outlier:
        evidence.append("IsolationForest independently classifies the current window as an outlier.")

    return RootCauseCandidate(
        kind=RootCauseKind.SUDDEN_RATE_CHANGE,
        confidence=round(confidence, 2),
        summary="A sharp change in failure rate, cause not yet identified.",
        supporting_evidence=evidence,
    )


def _new_failure_candidate(patterns: list[PatternEvidence]) -> RootCauseCandidate:
    exception = next((p.exception_type for p in patterns if p.exception_type), None)

    return RootCauseCandidate(
        kind=RootCauseKind.NEW_FAILURE_MODE,
        confidence=0.40,
        summary="A failure mode with no precedent in this organization's history.",
        supporting_evidence=[
            "No similar resolved incident was found.",
            f"Exception type: {exception}." if exception else "No exception type reported.",
        ],
    )


def _dependency_candidate(patterns: list[PatternEvidence]) -> RootCauseCandidate:
    statuses = sorted({p.http_status_code for p in patterns if p.http_status_code})

    evidence = []

    # Only claim what was actually observed. "HTTP status codes observed: ."
    # is worse than saying nothing: it looks like a bug, and it is.
    if statuses:
        evidence.append(f"HTTP status codes observed: {', '.join(str(s) for s in statuses)}.")

    markers = sorted(
        {
            marker
            for p in patterns
            for marker in DEPENDENCY_MARKERS
            if marker in p.message_template.lower() or marker in (p.exception_type or "").lower()
        }
    )

    if markers:
        evidence.append(f"Messages mention: {', '.join(markers)}.")

    evidence.append("Refused, unreachable and gateway errors originate outside the service reporting them.")

    return RootCauseCandidate(
        kind=RootCauseKind.DEPENDENCY_FAILURE,
        confidence=0.45,
        summary="Failures look like a downstream dependency rather than this service.",
        supporting_evidence=evidence,
    )


def _is_new_failure_mode(patterns: list[PatternEvidence], incident_age_minutes: float) -> bool:
    if not patterns:
        return False

    # "New" means the pattern is barely older than the incident: it started
    # here, rather than having been around and merely got louder.
    return all(
        (p.last_seen_at - p.first_seen_at).total_seconds() / 60.0 <= incident_age_minutes + 15
        for p in patterns
    )


def _looks_like_dependency_failure(patterns: list[PatternEvidence]) -> bool:
    """Only fires on markers that genuinely point outside this service.

    "timeout" is deliberately absent. It is the most common word in this
    domain and the least specific: a connection-pool timeout, a lock timeout
    and a slow query all use it, and none of them is a downstream dependency.
    Including it made every pool-exhaustion incident claim a dependency
    failure, which is a confident wrong answer - the worst kind.
    """
    return any(
        p.http_status_code in (502, 503, 504)
        or any(
            marker in (p.exception_type or "").lower() or marker in p.message_template.lower()
            for marker in DEPENDENCY_MARKERS
        )
        for p in patterns
    )
