"""The shapes the pipeline produces.

Everything here is evidence: a fact with a provenance, not a conclusion. The
worker's job at this stage is to assemble what is knowable and rank it, so that
an engineer - and later an LLM - is reasoning over gathered facts rather than
going looking for them.
"""

from datetime import datetime
from enum import StrEnum

from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel


class _Model(BaseModel):
    """Base for the evidence payloads.

    camelCase on the wire, matching every other contract in the system: these
    objects are returned by the diagnostics endpoints and are what the
    dashboard will consume, so they must not be the one thing that speaks
    snake_case.
    """

    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
        extra="ignore",
    )


class PatternEvidence(_Model):
    """A log pattern that belongs to this incident."""

    log_pattern_id: str
    fingerprint: str
    message_template: str
    sample_message: str
    exception_type: str | None = None
    occurrence_count: int
    first_seen_at: datetime
    last_seen_at: datetime
    http_status_code: int | None = None


class DeploymentEvidence(_Model):
    """A release close enough in time to be worth suspecting."""

    deployment_id: str
    version: str
    deployed_at: datetime
    commit_sha: str | None = None
    deployed_by: str | None = None

    #: Negative means the release preceded the incident, which is the
    #: interesting direction.
    minutes_before_incident: float


class SimilarIncidentEvidence(_Model):
    """A past incident whose signature is close to this one's."""

    incident_id: str
    title: str
    status: str
    similarity: float
    resolved_at: datetime | None = None

    #: The whole reason similarity search is worth running: what fixed it.
    resolution_notes: str | None = None


class AnomalyEvidence(_Model):
    """How unusual the current rate is, relative to this pattern's own history."""

    window_count: int
    baseline_mean_per_minute: float
    window_rate_per_minute: float

    #: Current rate divided by baseline rate. The number a human actually reads.
    magnitude: float

    #: Robust z-score, in median absolute deviations. Resistant to the spike
    #: itself dragging the mean upwards.
    robust_z_score: float

    #: scikit-learn's independent verdict on whether the window is an outlier.
    is_outlier: bool
    outlier_score: float
    baseline_sample_count: int


class RootCauseKind(StrEnum):
    RECENT_DEPLOYMENT = "recent_deployment"
    KNOWN_PAST_INCIDENT = "known_past_incident"
    SUDDEN_RATE_CHANGE = "sudden_rate_change"
    NEW_FAILURE_MODE = "new_failure_mode"
    DEPENDENCY_FAILURE = "dependency_failure"


class RootCauseCandidate(_Model):
    """A ranked hypothesis, with the evidence that produced it.

    Confidence is computed from the evidence by fixed arithmetic, not by a
    model. That means it is reproducible, explainable and adjustable - and that
    a reader can disagree with the weighting rather than with a black box.
    """

    kind: RootCauseKind
    confidence: float = Field(ge=0.0, le=1.0)
    summary: str

    #: What the confidence was built from, in human-readable form.
    supporting_evidence: list[str] = Field(default_factory=list)


class AnalysisResult(_Model):
    """Everything one analysis produced."""

    incident_id: str
    analysis_version: int
    patterns: list[PatternEvidence] = Field(default_factory=list)
    deployment: DeploymentEvidence | None = None
    anomaly: AnomalyEvidence | None = None
    similar_incidents: list[SimilarIncidentEvidence] = Field(default_factory=list)
    root_cause_candidates: list[RootCauseCandidate] = Field(default_factory=list)

    #: A plain-language summary assembled from templates. Not model-written -
    #: that is the next phase - but honest about what was found.
    summary: str = ""
    probable_cause: str | None = None
    suggested_actions: list[str] = Field(default_factory=list)

    #: Aggregate confidence across the candidates, for sorting incident lists.
    confidence: float = 0.0
    embedding_model: str = ""
    latency_ms: int = 0
