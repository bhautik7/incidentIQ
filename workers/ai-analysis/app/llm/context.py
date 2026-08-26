"""The only thing that is ever allowed to reach the model.

The rule this module exists to enforce: **raw log content never leaves the
process.** Raw logs carry whatever an application happened to write - session
tokens, bearer headers, customer emails, order contents, connection strings
with passwords in them. Sending those to any third party, however reputable, is
a data-egress decision nobody has made.

Three layers enforce it, in order of how much they can be trusted:

1. **A whitelist type.** ``ContextPackage`` has no field capable of holding a
   raw message. Not "we don't populate it" - the field does not exist, so
   there is nothing to forget. The builder is the only constructor, and it
   reads a fixed set of fields from the evidence.
2. **A redaction scan** over the serialised package, immediately before the
   call. Belt and braces: it catches anything that slipped into a field that
   *is* allowed, such as a normaliser miss leaving a token in a template.
3. **Fail closed.** A scan hit aborts the call and falls back to the
   deterministic summary. The system degrades to "no LLM narrative" rather
   than leaking.

The distinction that makes this possible is one the pipeline already draws:
``message_template`` is the *normalised* message, with GUIDs, emails, IPs,
numbers and paths already masked; ``sample_message`` is the raw line, kept for
humans to read in the UI. The template goes; the sample never does.
"""

from __future__ import annotations

import json
import re
from datetime import datetime

import structlog
from pydantic import BaseModel, ConfigDict, Field

from app.analysis.evidence import AnalysisResult, PatternEvidence

logger = structlog.get_logger(__name__)


class ContextRedactionError(Exception):
    """Raised when the outbound package fails its own scan.

    Deliberately an exception rather than a "redact and continue": if
    something unexpected reached this point, the safe move is to not make the
    call and to say so loudly, not to quietly strip it and carry on with a
    package we no longer understand.
    """

    def __init__(self, matches: list[str]) -> None:
        self.matches = matches
        super().__init__(f"Refusing to send context: matched {', '.join(matches)}")


class _Frozen(BaseModel):
    # Frozen and closed: nothing can be bolted on after construction, and an
    # unexpected key is an error rather than a silent passenger.
    model_config = ConfigDict(frozen=True, extra="forbid")


class ContextPatternSummary(_Frozen):
    """One normalised error pattern, with its counts.

    Note what is absent: no ``sample_message``, no ``stack_trace``, no
    ``trace_id``, no ``host``, no properties bag. Those exist on the evidence
    model and are deliberately not carried across.
    """

    #: The MASKED template. Never the raw message.
    normalized_message: str

    exception_type: str | None = None
    occurrence_count: int
    first_seen_at: str
    last_seen_at: str
    http_status_code: int | None = None

    #: First 12 characters, enough to correlate with the UI, not enough to
    #: reconstruct anything.
    fingerprint_prefix: str


class ContextDeployment(_Frozen):
    """The release under suspicion."""

    version: str
    deployed_at: str
    minutes_before_incident: float
    commit_sha: str | None = None


class ContextIncidentSummary(_Frozen):
    """What the incident is, in already-masked terms."""

    #: Derived from the normalised template by the detector, so already masked.
    title: str
    service: str
    environment: str
    severity: str
    detection_rule: str
    total_occurrences: int
    first_seen_at: str
    last_seen_at: str


class ContextPackage(_Frozen):
    """Everything the model is permitted to see.

    Exactly the four categories agreed for this boundary: the incident summary,
    the normalised error patterns, their occurrence counts, and the recent
    deployment. Nothing else has a field here.
    """

    incident: ContextIncidentSummary
    patterns: list[ContextPatternSummary] = Field(default_factory=list)
    deployment: ContextDeployment | None = None

    #: Rate-versus-baseline. Numbers only - no text passes through here.
    occurrence_rate_per_minute: float | None = None
    baseline_rate_per_minute: float | None = None
    anomaly_magnitude: float | None = None

    def to_prompt_json(self) -> str:
        return json.dumps(self.model_dump(exclude_none=True), indent=2, sort_keys=True)


# ---------------------------------------------------------------------------
# Redaction scan
# ---------------------------------------------------------------------------

#: Shapes that must never appear in an outbound package.
#:
#: These are not a substitute for masking - normalisation already removes the
#: common ones upstream. They are a tripwire for the case where it did not, and
#: they are deliberately biased towards false positives: a refused LLM call
#: costs a narrative paragraph, a leaked bearer token costs considerably more.
_FORBIDDEN_PATTERNS: list[tuple[str, re.Pattern[str]]] = [
    ("email address", re.compile(r"\b[\w.%+-]+@[\w.-]+\.[A-Za-z]{2,}\b")),
    ("IPv4 address", re.compile(r"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b")),
    ("UUID", re.compile(r"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")),
    ("JWT", re.compile(r"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}")),
    ("bearer token", re.compile(r"(?i)\bbearer\s+[A-Za-z0-9._~+/-]{16,}")),
    ("AWS access key", re.compile(r"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b")),
    ("private key block", re.compile(r"-----BEGIN [A-Z ]*PRIVATE KEY-----")),
    ("password in a connection string", re.compile(r"(?i)(?:password|pwd)\s*=\s*\S+")),
    ("credential-shaped assignment", re.compile(
        r"(?i)\b(?:api[_-]?key|secret|access[_-]?token|auth[_-]?token)\b\s*[=:]\s*\S{8,}")),
    ("long credit-card-like number", re.compile(r"\b(?:\d[ -]?){13,19}\b")),
]

#: Placeholders the normaliser leaves behind. A package full of "{EMAIL}" is
#: working exactly as intended and must not trip the scanner that is looking
#: for the thing it replaced.
_PLACEHOLDER = re.compile(r"\{[A-Z_]+\}")


def scan_for_sensitive_content(payload: str) -> list[str]:
    """Returns the names of any forbidden shapes found."""
    # Placeholders are removed first so "{IP}" is not read as an address and
    # "{NUM}" is not read as a card number.
    haystack = _PLACEHOLDER.sub(" ", payload)

    return [name for name, pattern in _FORBIDDEN_PATTERNS if pattern.search(haystack)]


# ---------------------------------------------------------------------------
# Builder
# ---------------------------------------------------------------------------


def _iso(value: datetime | str | None) -> str:
    if value is None:
        return ""
    return value.isoformat() if isinstance(value, datetime) else str(value)


def _pattern_summary(pattern: PatternEvidence) -> ContextPatternSummary:
    """Copies field by field, deliberately.

    Written as an explicit construction rather than a dict comprehension or a
    ``model_dump(exclude=...)`` so that adding a field to ``PatternEvidence``
    can never silently widen what is sent. A new field has to be added here on
    purpose to escape.
    """
    return ContextPatternSummary(
        normalized_message=pattern.message_template,
        exception_type=pattern.exception_type,
        occurrence_count=pattern.occurrence_count,
        first_seen_at=_iso(pattern.first_seen_at),
        last_seen_at=_iso(pattern.last_seen_at),
        http_status_code=pattern.http_status_code,
        fingerprint_prefix=pattern.fingerprint[:12],
    )


def build_context_package(
    *,
    result: AnalysisResult,
    title: str,
    service: str,
    environment: str,
    severity: str,
    detection_rule: str,
    total_occurrences: int,
    first_seen_at: datetime,
    last_seen_at: datetime,
    max_patterns: int = 10,
) -> ContextPackage:
    """Assembles the package. The only path from evidence to the model."""
    patterns = [_pattern_summary(p) for p in result.patterns[:max_patterns]]

    deployment = None
    if result.deployment is not None:
        deployment = ContextDeployment(
            version=result.deployment.version,
            deployed_at=_iso(result.deployment.deployed_at),
            minutes_before_incident=result.deployment.minutes_before_incident,
            commit_sha=result.deployment.commit_sha,
        )

    anomaly = result.anomaly

    return ContextPackage(
        incident=ContextIncidentSummary(
            title=title,
            service=service,
            environment=environment,
            severity=severity,
            detection_rule=detection_rule,
            total_occurrences=total_occurrences,
            first_seen_at=_iso(first_seen_at),
            last_seen_at=_iso(last_seen_at),
        ),
        patterns=patterns,
        deployment=deployment,
        occurrence_rate_per_minute=anomaly.window_rate_per_minute if anomaly else None,
        baseline_rate_per_minute=anomaly.baseline_mean_per_minute if anomaly else None,
        anomaly_magnitude=anomaly.magnitude if anomaly else None,
    )


def assert_safe_to_send(package: ContextPackage) -> str:
    """Final gate. Returns the JSON to send, or refuses.

    Every call into the model goes through here. Nothing else serialises a
    package for transmission.
    """
    payload = package.to_prompt_json()
    matches = scan_for_sensitive_content(payload)

    if matches:
        # The matched values are never logged - that would move the leak from
        # the API call to the log pipeline.
        logger.error(
            "context_package_rejected",
            matched_patterns=matches,
            pattern_count=len(package.patterns),
        )
        raise ContextRedactionError(matches)

    return payload
