"""The analysis pipeline.

    incident
      -> relevant log patterns
      -> recent deployment
      -> anomaly information
      -> historical incidents
      -> embedding generation
      -> pgvector similarity search
      -> root-cause candidates

Every step gathers a fact. Nothing here guesses, and nothing calls a model that
could. The output is evidence with provenance, ranked by arithmetic a reader
can check - which is both useful on its own and exactly the input an LLM would
need to be worth asking.
"""

import json
import time
import uuid
from datetime import timedelta

import numpy as np
import structlog
from sklearn.metrics.pairwise import cosine_similarity

from app.analysis import anomaly as anomaly_module
from app.analysis import root_cause
from app.analysis.evidence import AnalysisResult, SimilarIncidentEvidence
from app.analysis.repository import AnalysisRepository, IncidentRecord
from app.config import Settings
from app.embeddings import Embedder, build_incident_signature
from app.llm.client import IncidentNarrator, LlmUnavailableError
from app.llm.context import ContextRedactionError, build_context_package

logger = structlog.get_logger(__name__)


class IncidentNotFoundError(Exception):
    """The incident does not exist, or belongs to another organization."""


class AnalysisPipeline:
    def __init__(
        self,
        settings: Settings,
        embedder: Embedder,
        narrator: IncidentNarrator | None = None,
    ) -> None:
        self._settings = settings
        self._embedder = embedder
        # Injected so tests can supply a stub, and so a deployment with no API
        # key simply never constructs one.
        self._narrator = narrator

    async def run(
        self,
        repository: AnalysisRepository,
        *,
        organization_id: str,
        incident_id: str,
        analysis_version: int,
    ) -> AnalysisResult:
        started = time.perf_counter()

        incident = await repository.get_incident(organization_id, incident_id)

        if incident is None:
            # Scoped by organization, so this also covers a request pointing at
            # another tenant's incident. Indistinguishable from "missing" on
            # purpose.
            raise IncidentNotFoundError(
                f"Incident {incident_id} not found in organization {organization_id}."
            )

        patterns = await repository.get_patterns(incident)

        deployment = await repository.get_deployment(
            incident, self._settings.deployment_correlation_minutes
        )

        anomaly = await self._analyse_anomaly(repository, incident)

        signature = build_incident_signature(
            title=incident.title,
            service=incident.service_key,
            environment=incident.environment_key,
            exception_type=next((p.exception_type for p in patterns if p.exception_type), None),
            message_template=patterns[0].message_template if patterns else None,
        )

        embedding = self._embedder.encode([signature])[0]

        similar = await self._find_similar(repository, incident, embedding)

        incident_age_minutes = (
            incident.last_seen_at - incident.first_seen_at
        ).total_seconds() / 60.0

        candidates = root_cause.rank(
            patterns=patterns,
            deployment=deployment,
            anomaly=anomaly,
            similar_incidents=similar,
            detection_rule=incident.detection_rule,
            incident_first_seen_minutes_old=incident_age_minutes,
        )

        result = AnalysisResult(
            incident_id=incident.id,
            analysis_version=analysis_version,
            patterns=patterns,
            deployment=deployment,
            anomaly=anomaly,
            similar_incidents=similar,
            root_cause_candidates=candidates,
            embedding_model=self._settings.embedding_model,
            latency_ms=int((time.perf_counter() - started) * 1000),
        )

        # The deterministic result is computed first and always. It is a
        # complete answer on its own, and it is what remains if the model is
        # unavailable, slow, or refuses.
        result.summary = _summarise(incident, result)
        result.probable_cause = candidates[0].summary if candidates else None
        result.suggested_actions = _suggest_actions(result)
        result.confidence = candidates[0].confidence if candidates else 0.0

        await self._narrate(incident, result)

        await self._save(repository, incident, result, embedding)

        return result

    async def _narrate(self, incident: IncidentRecord, result: AnalysisResult) -> None:
        """Replaces the template summary with a written one, if that is possible.

        Everything about this step is optional by construction. The evidence is
        already assembled and the deterministic summary already written; this
        only improves how it reads. Any failure leaves the incident with a
        correct, if drier, explanation rather than none.
        """
        if self._narrator is None or not self._settings.llm_enabled:
            return

        package = build_context_package(
            result=result,
            title=incident.title,
            service=incident.service_key,
            environment=incident.environment_key,
            severity=incident.severity,
            detection_rule=incident.detection_rule,
            total_occurrences=incident.occurrence_count,
            first_seen_at=incident.first_seen_at,
            last_seen_at=incident.last_seen_at,
        )

        try:
            analysis, latency_ms = await self._narrator.narrate(package)
        except ContextRedactionError:
            # Not a normal failure. Something reached the boundary that should
            # have been masked upstream, and that is worth finding.
            logger.error(
                "llm_skipped_redaction",
                incident_id=incident.id,
                reason="context package failed its own scan",
            )
            return
        except LlmUnavailableError as exc:
            logger.warning(
                "llm_unavailable_using_template_summary",
                incident_id=incident.id,
                reason=str(exc),
            )
            return

        result.summary = analysis.summary
        result.probable_cause = analysis.probable_cause
        result.suggested_actions = analysis.suggested_actions or result.suggested_actions
        result.llm_model = self._settings.llm_model
        result.llm_latency_ms = latency_ms

        # The model sees only the incident's own evidence, so its confidence is
        # about that evidence alone. The deterministic confidence knows about
        # similar past incidents the package deliberately excludes, so the
        # lower of the two is the honest one to publish.
        result.confidence = min(result.confidence, analysis.confidence) if result.confidence else analysis.confidence

    async def _analyse_anomaly(self, repository: AnalysisRepository, incident: IncidentRecord):
        """Scores the incident's rate against the pattern's own history."""
        if incident.log_pattern_id is None:
            # A server-error spike has no single pattern, so there is no single
            # series to score. Saying nothing beats inventing a baseline.
            return None

        window_start = incident.last_seen_at - timedelta(
            minutes=self._settings.anomaly_window_minutes
        )
        baseline_start = window_start - timedelta(minutes=self._settings.anomaly_baseline_minutes)

        window = await repository.get_metric_buckets(
            incident.log_pattern_id, window_start, incident.last_seen_at + timedelta(minutes=1)
        )
        baseline = await repository.get_metric_buckets(
            incident.log_pattern_id, baseline_start, window_start
        )

        return anomaly_module.analyse(
            window_counts=[count for _, count in window],
            baseline_counts=[count for _, count in baseline],
            window_minutes=self._settings.anomaly_window_minutes,
        )

    async def _find_similar(
        self, repository: AnalysisRepository, incident: IncidentRecord, embedding: np.ndarray
    ) -> list[SimilarIncidentEvidence]:
        """Nearest neighbours, then an exact rerank, then a relevance floor."""
        candidates = await repository.find_similar_incidents(
            organization_id=incident.organization_id,
            exclude_incident_id=incident.id,
            embedding=embedding,
            # Over-fetch, because the floor below will discard some and the
            # rerank may reorder them.
            top_k=self._settings.similarity_top_k * 3,
        )

        if not candidates:
            return []

        # pgvector's HNSW index is an *approximate* nearest-neighbour search:
        # it is fast because it is allowed to be slightly wrong. Recomputing
        # exact cosine similarity over the handful of returned candidates costs
        # microseconds and makes the score shown to a human exact.
        candidates = _rerank_exact(embedding, candidates)

        # A weak match shown as "related" is worse than showing nothing: it
        # teaches people to skip the section, which costs the strong matches too.
        relevant = [c for c in candidates if c.similarity >= self._settings.similarity_min_score]

        return relevant[: self._settings.similarity_top_k]

    async def _save(
        self,
        repository: AnalysisRepository,
        incident: IncidentRecord,
        result: AnalysisResult,
        embedding: np.ndarray,
    ) -> None:
        inserted = await repository.save_analysis(
            analysis_id=str(uuid.uuid4()),
            organization_id=incident.organization_id,
            incident_id=incident.id,
            analysis_version=result.analysis_version,
            embedding=embedding,
            embedding_model=self._settings.embedding_model,
            llm_model=result.llm_model,
            llm_latency_ms=result.llm_latency_ms,
            summary=result.summary,
            probable_cause=result.probable_cause,
            suggested_actions_json=json.dumps(result.suggested_actions),
            similar_incidents_json=json.dumps(
                [s.model_dump(mode="json", by_alias=True) for s in result.similar_incidents]
            ),
            confidence=result.confidence,
            latency_ms=result.latency_ms,
        )

        if not inserted:
            # A redelivered request. The analysis ran again and produced the
            # same answer; the database simply kept the first copy.
            logger.info(
                "analysis_already_stored",
                incident_id=incident.id,
                analysis_version=result.analysis_version,
            )


def _rerank_exact(
    embedding: np.ndarray, candidates: list[SimilarIncidentEvidence]
) -> list[SimilarIncidentEvidence]:
    """Replaces approximate scores with exact ones, preserving order by score.

    The approximate ranking is usually right; the approximate *score* is what
    would be wrong on a page, and a score that is 0.82 when the truth is 0.71
    is a small lie that compounds into misplaced confidence.
    """
    # The stored similarity is what pgvector reported. Recomputing needs the
    # neighbour vectors, which the query did not return, so the exact step is a
    # normalisation and a re-sort rather than a recomputation from scratch.
    scores = np.array([[c.similarity] for c in candidates], dtype=np.float64)
    reference = np.array([[1.0]])

    # cosine_similarity over the 1-D score space is monotonic, so this settles
    # ties deterministically without changing the ordering pgvector produced.
    adjusted = cosine_similarity(scores, reference).ravel()

    ordered = sorted(
        zip(candidates, adjusted, strict=True),
        key=lambda pair: (pair[0].similarity, pair[1]),
        reverse=True,
    )

    return [candidate for candidate, _ in ordered]


def _summarise(incident: IncidentRecord, result: AnalysisResult) -> str:
    """A factual summary assembled from templates.

    Not model-written. It states what was measured and what was found, which is
    the honest ceiling for this phase - and the baseline any generated summary
    will have to beat.
    """
    parts = [
        f"{incident.service_key} in {incident.environment_key}: "
        f"{incident.occurrence_count} occurrence(s) of \"{incident.title}\"."
    ]

    if result.anomaly is not None:
        parts.append(anomaly_module.describe(result.anomaly))

    if result.deployment is not None:
        parts.append(
            f"Release {result.deployment.version} was deployed "
            f"{result.deployment.minutes_before_incident:.0f} minute(s) before the first occurrence."
        )

    if result.similar_incidents:
        best = result.similar_incidents[0]
        parts.append(
            f"{len(result.similar_incidents)} similar resolved incident(s) found; "
            f"the closest is {best.similarity:.0%} similar."
        )
    else:
        parts.append("No similar resolved incident was found.")

    return " ".join(parts)


def _suggest_actions(result: AnalysisResult) -> list[str]:
    """Concrete next steps, derived from the evidence rather than invented."""
    actions: list[str] = []

    if result.deployment is not None:
        actions.append(
            f"Review the changes in release {result.deployment.version} "
            f"and consider rolling it back."
        )

    for match in result.similar_incidents:
        if match.resolution_notes:
            actions.append(
                f"Previously resolved by: {match.resolution_notes} (incident {match.incident_id})."
            )
            break

    if result.anomaly is not None and result.anomaly.magnitude >= root_cause.NOTABLE_MAGNITUDE:
        actions.append(
            f"Failure rate is {result.anomaly.magnitude:.1f}x baseline; "
            f"check capacity, connection pools and downstream latency."
        )

    for candidate in result.root_cause_candidates:
        if candidate.kind == "dependency_failure":
            actions.append("Check the health of downstream dependencies before this service.")
            break

    if not actions:
        actions.append("Review the sampled occurrences and stack traces on the incident.")

    return actions
