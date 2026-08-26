"""Turning incident text into vectors.

No LLM. A sentence-transformers bi-encoder maps text to a fixed-width vector so
that semantically similar text lands nearby. That is the entire mechanism
behind "has this happened before?", and it works because the two descriptions

    "The connection pool has been exhausted, raise MaxPoolSize"
    "Timeout expired prior to obtaining a connection from the pool"

share almost no words but a great deal of meaning. Exact matching, fingerprints
and full-text search all miss that; an embedding does not.
"""

from typing import Protocol

import numpy as np
import structlog

logger = structlog.get_logger(__name__)


class Embedder(Protocol):
    """The seam that keeps a 90MB model out of unit tests."""

    @property
    def dimensions(self) -> int: ...

    def encode(self, texts: list[str]) -> np.ndarray:
        """Returns an (n, dimensions) array of L2-normalised vectors."""
        ...


class SentenceTransformerEmbedder:
    """The real model, loaded once per process.

    Loading takes a second or two and holds the weights in memory, so it is
    created at startup rather than per message. Vectors are normalised on the
    way out, which makes cosine similarity a dot product and lets pgvector's
    cosine operator and scikit-learn agree on what a score means.
    """

    def __init__(self, model_name: str, expected_dimensions: int) -> None:
        # Imported lazily: the import alone pulls in torch, which is slow and
        # unnecessary for anything that injects a different Embedder.
        from sentence_transformers import SentenceTransformer

        logger.info("embedding_model_loading", model=model_name)
        self._model = SentenceTransformer(model_name)
        self._dimensions = self._model.get_sentence_embedding_dimension()

        if self._dimensions != expected_dimensions:
            # Caught here rather than at the first INSERT, where it would
            # surface as an opaque pgvector type error.
            raise RuntimeError(
                f"Model {model_name} emits {self._dimensions} dimensions but the "
                f"schema expects {expected_dimensions}. The vector(N) column and "
                f"the model must agree."
            )

        logger.info("embedding_model_ready", model=model_name, dimensions=self._dimensions)

    @property
    def dimensions(self) -> int:
        return self._dimensions

    def encode(self, texts: list[str]) -> np.ndarray:
        return self._model.encode(
            texts,
            normalize_embeddings=True,
            convert_to_numpy=True,
            show_progress_bar=False,
        )


class HashingEmbedder:
    """Deterministic stand-in for tests.

    Produces stable vectors from a hash of the text, so identical text embeds
    identically and different text does not. Enough to exercise the pipeline,
    the SQL and the ranking without downloading a model or importing torch.

    It has no semantic understanding whatsoever, which is exactly why the real
    model is also exercised in an end-to-end test.
    """

    def __init__(self, dimensions: int = 384) -> None:
        self._dimensions = dimensions

    @property
    def dimensions(self) -> int:
        return self._dimensions

    def encode(self, texts: list[str]) -> np.ndarray:
        vectors = np.zeros((len(texts), self._dimensions), dtype=np.float32)

        for row, text in enumerate(texts):
            # Token-level hashing so that texts sharing words share direction -
            # a crude but useful approximation of semantic proximity.
            for token in text.lower().split():
                vectors[row, hash(token) % self._dimensions] += 1.0

        norms = np.linalg.norm(vectors, axis=1, keepdims=True)
        norms[norms == 0] = 1.0
        return vectors / norms


def build_incident_signature(
    *,
    title: str,
    service: str,
    environment: str,
    exception_type: str | None,
    message_template: str | None,
) -> str:
    """The text that gets embedded.

    Deliberately the *normalised* template rather than a raw sample: the raw
    message carries user ids and timeouts that differ between occurrences of
    the same failure, and embedding those pushes identical failures apart.

    Service and environment are included because "pool exhausted in payments"
    and "pool exhausted in search" are genuinely different problems, and a
    reader looking at a suggested match needs that distinction to survive.
    """
    parts = [title, f"service: {service}", f"environment: {environment}"]

    if exception_type:
        parts.append(f"exception: {exception_type}")

    if message_template:
        parts.append(message_template)

    return " | ".join(parts)
