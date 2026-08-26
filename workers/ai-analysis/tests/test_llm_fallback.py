"""The pipeline must survive the model being absent, slow, broken or refusing.

Narration is an improvement to how an explanation reads, not a dependency of
producing one. Every failure here should leave the incident with the
deterministic summary and the analysis still written to the database.
"""

from datetime import UTC, datetime

import pytest

from app.analysis.evidence import AnalysisResult, PatternEvidence
from app.config import Settings
from app.llm.client import IncidentNarrator, LlmAnalysis, LlmUnavailableError
from app.llm.context import ContextRedactionError, build_context_package

NOW = datetime(2026, 8, 26, 12, 0, tzinfo=UTC)


def _package():
    result = AnalysisResult(
        incident_id="22222222-2222-2222-2222-222222222222",
        analysis_version=1,
        patterns=[
            PatternEvidence(
                log_pattern_id="11111111-1111-1111-1111-111111111111",
                fingerprint="b" * 64,
                message_template="Connection timeout for user {NUM}",
                sample_message="Connection timeout for user 18273",
                exception_type="System.TimeoutException",
                occurrence_count=412,
                first_seen_at=NOW,
                last_seen_at=NOW,
            )
        ],
    )
    return build_context_package(
        result=result,
        title="TimeoutException: Connection timeout for user {NUM}",
        service="payments-api",
        environment="production",
        severity="Critical",
        detection_rule="CountThreshold",
        total_occurrences=412,
        first_seen_at=NOW,
        last_seen_at=NOW,
    )


def _settings(**overrides) -> Settings:
    defaults = dict(
        POSTGRES_DSN="postgresql://x/y",
        KAFKA_BOOTSTRAP_SERVERS="localhost:9092",
        ANTHROPIC_API_KEY="sk-test-not-a-real-key",
    )
    return Settings(**{**defaults, **overrides})


class _StubResponse:
    def __init__(self, parsed, stop_reason="end_turn"):
        self.parsed_output = parsed
        self.stop_reason = stop_reason
        self.usage = type("U", (), {"input_tokens": 900, "output_tokens": 180})()


class _StubMessages:
    def __init__(self, behaviour):
        self._behaviour = behaviour
        self.last_kwargs: dict | None = None

    async def parse(self, **kwargs):
        self.last_kwargs = kwargs
        return self._behaviour(kwargs)


class _StubClient:
    def __init__(self, behaviour):
        self.messages = _StubMessages(behaviour)


def _narrator_with(behaviour, **setting_overrides) -> IncidentNarrator:
    narrator = IncidentNarrator(_settings(**setting_overrides))
    narrator._client = _StubClient(behaviour)  # noqa: SLF001 - injecting the transport
    return narrator


# ---------------------------------------------------------------------------
# Happy path
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_returns_the_models_structured_analysis():
    expected = LlmAnalysis(
        summary="payments-api is timing out against its database.",
        probable_cause="Release 2.31.0 shipped three minutes before the first occurrence.",
        suggested_actions=["Check the DbContext lifetime in 2.31.0", "Compare MaxPoolSize"],
        confidence=0.82,
    )

    narrator = _narrator_with(lambda _: _StubResponse(expected))
    analysis, latency_ms = await narrator.narrate(_package())

    assert analysis.probable_cause.startswith("Release 2.31.0")
    assert latency_ms >= 0


@pytest.mark.asyncio
async def test_only_the_scanned_payload_is_sent():
    """The request body must contain the package and nothing else."""
    narrator = _narrator_with(
        lambda _: _StubResponse(
            LlmAnalysis(summary="s", probable_cause="c", suggested_actions=[], confidence=0.5)
        )
    )

    await narrator.narrate(_package())

    kwargs = narrator._client.messages.last_kwargs  # noqa: SLF001
    sent = str(kwargs["messages"])

    assert "Connection timeout for user {NUM}" in sent
    # The raw sample sitting on the evidence never entered the package.
    assert "18273" not in sent
    assert kwargs["model"] == "claude-opus-5"
    assert kwargs["output_format"] is LlmAnalysis


# ---------------------------------------------------------------------------
# Failure paths - every one of these must be survivable
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_missing_api_key_is_reported_as_unavailable():
    narrator = IncidentNarrator(_settings(ANTHROPIC_API_KEY=None))

    with pytest.raises(LlmUnavailableError, match="ANTHROPIC_API_KEY"):
        await narrator.narrate(_package())


@pytest.mark.asyncio
async def test_a_transport_error_is_unavailable_not_a_crash():
    def boom(_):
        raise TimeoutError("read timed out")

    narrator = _narrator_with(boom)

    with pytest.raises(LlmUnavailableError, match="TimeoutError"):
        await narrator.narrate(_package())


@pytest.mark.asyncio
async def test_a_safety_refusal_is_handled_before_reading_content():
    """A decline is HTTP 200 with no usable output.

    Reading parsed_output first would raise something unrelated and obscure
    what actually happened.
    """
    narrator = _narrator_with(lambda _: _StubResponse(None, stop_reason="refusal"))

    with pytest.raises(LlmUnavailableError, match="declined"):
        await narrator.narrate(_package())


@pytest.mark.asyncio
async def test_an_unparseable_response_is_unavailable():
    narrator = _narrator_with(lambda _: _StubResponse(None))

    with pytest.raises(LlmUnavailableError, match="no parseable analysis"):
        await narrator.narrate(_package())


@pytest.mark.asyncio
async def test_a_redaction_hit_is_not_swallowed_as_unavailable():
    """A leak attempt must be distinguishable from a network blip.

    LlmUnavailableError is routine and logged at warning. A redaction hit means
    something upstream failed to mask, and it has to stand out.
    """
    leaky = AnalysisResult(
        incident_id="x",
        analysis_version=1,
        patterns=[
            PatternEvidence(
                log_pattern_id="p",
                fingerprint="c" * 64,
                message_template="Failed to notify alice@acme.com",
                sample_message="raw",
                occurrence_count=1,
                first_seen_at=NOW,
                last_seen_at=NOW,
            )
        ],
    )
    package = build_context_package(
        result=leaky, title="t", service="s", environment="e", severity="Low",
        detection_rule="CountThreshold", total_occurrences=1,
        first_seen_at=NOW, last_seen_at=NOW,
    )

    narrator = _narrator_with(lambda _: pytest.fail("the call must not be made"))

    with pytest.raises(ContextRedactionError):
        await narrator.narrate(package)


@pytest.mark.asyncio
async def test_the_call_is_never_made_when_the_scan_fails():
    """Fail closed: the gate runs before the client is even constructed."""
    calls = []

    def record(kwargs):
        calls.append(kwargs)
        return _StubResponse(None)

    leaky = AnalysisResult(
        incident_id="x",
        analysis_version=1,
        patterns=[
            PatternEvidence(
                log_pattern_id="p", fingerprint="d" * 64,
                message_template="token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTYifQ",
                sample_message="raw", occurrence_count=1,
                first_seen_at=NOW, last_seen_at=NOW,
            )
        ],
    )
    package = build_context_package(
        result=leaky, title="t", service="s", environment="e", severity="Low",
        detection_rule="CountThreshold", total_occurrences=1,
        first_seen_at=NOW, last_seen_at=NOW,
    )

    narrator = _narrator_with(record)

    with pytest.raises(ContextRedactionError):
        await narrator.narrate(package)

    assert calls == []
