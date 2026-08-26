"""Embedding helpers and the signature that gets embedded."""

import numpy as np

from app.embeddings import HashingEmbedder, build_incident_signature


def test_the_signature_uses_the_normalised_template_not_the_raw_message():
    signature = build_incident_signature(
        title="TimeoutException: Connection timeout for user {NUM}",
        service="payments-api",
        environment="production",
        exception_type="System.TimeoutException",
        message_template="Connection timeout for user {NUM}",
    )

    assert "{NUM}" in signature
    assert "payments-api" in signature
    assert "production" in signature


def test_the_signature_separates_the_same_failure_in_different_services():
    # "Pool exhausted in payments" and "pool exhausted in search" are different
    # problems, and a suggested match must not blur them.
    payments = build_incident_signature(
        title="pool exhausted", service="payments-api", environment="production",
        exception_type=None, message_template=None,
    )
    search = build_incident_signature(
        title="pool exhausted", service="search-api", environment="production",
        exception_type=None, message_template=None,
    )

    assert payments != search


def test_optional_fields_are_omitted_rather_than_rendered_as_none():
    signature = build_incident_signature(
        title="something broke", service="api", environment="staging",
        exception_type=None, message_template=None,
    )

    assert "None" not in signature


def test_the_hashing_embedder_is_deterministic_and_normalised():
    embedder = HashingEmbedder(dimensions=64)

    first = embedder.encode(["connection pool exhausted"])
    second = embedder.encode(["connection pool exhausted"])

    np.testing.assert_array_equal(first, second)
    assert first.shape == (1, 64)
    np.testing.assert_allclose(np.linalg.norm(first[0]), 1.0, rtol=1e-5)


def test_the_hashing_embedder_separates_unrelated_text():
    embedder = HashingEmbedder(dimensions=256)
    vectors = embedder.encode(["connection pool exhausted", "disk full on log volume"])

    assert float(vectors[0] @ vectors[1]) < 0.5


def test_encoding_an_empty_string_does_not_produce_nan():
    # A zero vector would divide by a zero norm and poison every downstream
    # similarity comparison with NaN.
    vector = HashingEmbedder(dimensions=32).encode([""])[0]

    assert not np.isnan(vector).any()
