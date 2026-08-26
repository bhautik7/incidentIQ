"""Calling Claude with a context package, and never with anything else.

The model's job here is narrow: turn assembled evidence into a readable
explanation. It does not investigate, it does not fetch, and it has no tools -
everything it is allowed to know is already in the package it is handed.

That narrowness is the point. Retrieval and correlation were built first
because they are deterministic and checkable; this layer only writes the
sentence a human reads at 03:00.
"""

from __future__ import annotations

import time

import structlog
from pydantic import BaseModel, Field

from app.config import Settings
from app.llm.context import ContextPackage, ContextRedactionError, assert_safe_to_send

logger = structlog.get_logger(__name__)

SYSTEM_PROMPT = """\
You are an incident analysis assistant for a production monitoring platform.

You are given a JSON context package about one incident. It contains the \
incident summary, normalised error patterns, occurrence counts, and the recent \
deployment if there was one.

Important properties of your input:

- Error messages are NORMALISED. Placeholders like {NUM}, {UUID}, {IP} and \
{EMAIL} are where variable values were masked out. Treat a placeholder as \
"a value of this kind was here", never as a literal.
- You are seeing aggregates, not individual log lines. You cannot request more.
- The package is everything available. If something is not in it, say so rather \
than speculating about what the logs might have contained.

Write for an engineer who has just been paged and has not seen this incident \
before. Be concrete and brief. Prefer naming the specific evidence - a version \
number, a count, a rate multiple - over describing it in general terms.

Rules:
- Never invent a cause the evidence does not support. "The evidence does not \
identify a cause" is a valid and useful answer.
- A deployment shortly before the first occurrence is strong evidence, but it \
is correlation. Say "shipped N minutes before" rather than "caused by".
- Suggested actions must be things this evidence justifies doing next, ordered \
so the cheapest check that could disconfirm the leading hypothesis comes first.
- Do not mention that you are an AI, and do not describe the JSON structure.\
"""


class LlmAnalysis(BaseModel):
    """The structured shape the model must return."""

    summary: str = Field(description="Two or three sentences: what is broken, where, and how badly.")

    probable_cause: str = Field(
        description="The most likely cause given the evidence, or an explicit statement "
                    "that the evidence does not identify one."
    )

    suggested_actions: list[str] = Field(
        default_factory=list,
        description="Two to four concrete next steps, cheapest disconfirming check first.",
    )

    confidence: float = Field(
        ge=0.0, le=1.0,
        description="Confidence in probable_cause given only this evidence. "
                    "Below 0.4 when no deployment correlates and no clear pattern stands out.",
    )


class LlmUnavailableError(Exception):
    """The call could not be completed. Callers fall back to the template summary."""


class IncidentNarrator:
    """Wraps the Anthropic client with this system's constraints applied."""

    def __init__(self, settings: Settings) -> None:
        self._settings = settings
        self._client = None

    def _ensure_client(self):
        if self._client is not None:
            return self._client

        # Imported lazily so the package stays optional: a deployment that has
        # not configured an API key never imports the SDK.
        try:
            from anthropic import AsyncAnthropic
        except ImportError as exc:  # pragma: no cover - import guard
            raise LlmUnavailableError("The anthropic package is not installed.") from exc

        if not self._settings.anthropic_api_key:
            raise LlmUnavailableError("ANTHROPIC_API_KEY is not configured.")

        self._client = AsyncAnthropic(
            api_key=self._settings.anthropic_api_key,
            timeout=self._settings.llm_timeout_seconds,
            max_retries=self._settings.llm_max_retries,
        )
        return self._client

    async def narrate(self, package: ContextPackage) -> tuple[LlmAnalysis, int]:
        """Returns the model's analysis and the call latency in milliseconds.

        Raises LlmUnavailableError for anything the caller should fall back
        from. It never raises ContextRedactionError outward as a normal
        failure: a redaction hit is a defect worth surfacing distinctly, so it
        propagates.
        """
        # The gate. Nothing below this line has access to anything the scan
        # did not clear.
        payload = assert_safe_to_send(package)

        client = self._ensure_client()
        started = time.monotonic()

        try:
            response = await client.messages.parse(
                model=self._settings.llm_model,
                max_tokens=self._settings.llm_max_tokens,
                system=SYSTEM_PROMPT,
                # Root-cause reasoning over conflicting evidence benefits from
                # thinking; effort is tunable because most incidents are
                # straightforward and a minority are not.
                thinking={"type": "adaptive"},
                output_config={"effort": self._settings.llm_effort},
                messages=[{
                    "role": "user",
                    "content": f"Analyse this incident.\n\n```json\n{payload}\n```",
                }],
                output_format=LlmAnalysis,
            )
        except ContextRedactionError:
            raise
        except Exception as exc:
            # Typed handling lives in the caller's fallback; anything that
            # reaches here means no narrative this time, which is survivable.
            raise LlmUnavailableError(f"{type(exc).__name__}: {exc}") from exc

        latency_ms = int((time.monotonic() - started) * 1000)

        # A safety decline is a 200 with no usable content, so it has to be
        # checked before reading the parsed output.
        if response.stop_reason == "refusal":
            raise LlmUnavailableError("The model declined to analyse this incident.")

        analysis = response.parsed_output

        if analysis is None:
            raise LlmUnavailableError("The model returned no parseable analysis.")

        logger.info(
            "llm_analysis_complete",
            model=self._settings.llm_model,
            latency_ms=latency_ms,
            input_tokens=response.usage.input_tokens,
            output_tokens=response.usage.output_tokens,
            confidence=analysis.confidence,
        )

        return analysis, latency_ms
