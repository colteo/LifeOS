"""AI-004: the OpenAI journal embedding adapter, offline (scripted transport, recorded sleeps)."""

import asyncio
import json
import logging

import httpx
import pytest

from lifeos_ai.config import journal_embedder_from_environment
from lifeos_ai.journal_memory.embedding import (
    EmbeddingFailed,
    ProviderUnavailable,
    UnconfiguredEmbedder,
)
from lifeos_ai.journal_memory.openai_embeddings import OpenAIJournalEmbedder
from tests.journal_memory_fixtures import ITALIAN_ENTRY, embedder, embeddings_reply, vector

TEXTS = [ITALIAN_ENTRY, "Seconda parte del diario.", "Terza parte."]


def run(openai, texts=TEXTS, purpose="index"):
    return asyncio.run(openai.embed(texts, purpose=purpose))


def body_with(data, status=200):
    return httpx.Response(status, json={"object": "list", "data": data})


# ---- Happy path ----


def test_a_valid_batch_returns_one_exact_vector_per_input_with_token_usage():
    openai, transport, sleep = embedder(embeddings_reply(3, tokens=57))

    result = run(openai)

    assert result.vectors == [vector(0), vector(1), vector(2)]
    assert all(len(values) == 1536 for values in result.vectors)
    assert (result.attempts, result.input_tokens) == (1, 57)
    assert sleep.delays == []

    request = transport.requests[0]
    assert str(request.url) == "https://openai.test/v1/embeddings"
    assert request.headers["authorization"] == "Bearer test-openai-key"
    assert transport.bodies() == [
        {
            "model": "text-embedding-3-small",
            "input": TEXTS,
            "dimensions": 1536,
            "encoding_format": "float",
        }
    ]


def test_vectors_are_returned_in_input_order_whatever_the_response_order():
    openai, _, _ = embedder(embeddings_reply(3, order=[2, 0, 1]))

    assert run(openai).vectors == [vector(0), vector(1), vector(2)]


def test_missing_usage_is_unknown_not_zero():
    openai, _, _ = embedder(embeddings_reply(3, tokens=None))

    assert run(openai).input_tokens is None


# ---- Retries ----


def test_429_is_retried_and_then_succeeds():
    openai, transport, sleep = embedder(httpx.Response(429), embeddings_reply(3))

    result = run(openai)

    assert result.attempts == 2
    assert len(transport.requests) == 2
    assert sleep.delays == [1.0]


def test_5xx_is_retried_with_bounded_backoff_then_unavailable():
    openai, transport, sleep = embedder(
        httpx.Response(500), httpx.Response(502), httpx.Response(503)
    )

    with pytest.raises(ProviderUnavailable):
        run(openai)

    assert len(transport.requests) == 3
    assert sleep.delays == [1.0, 2.0]


def test_timeouts_and_transport_errors_are_retried():
    openai, transport, _ = embedder(
        httpx.ReadTimeout("slow"), httpx.ConnectError("down"), embeddings_reply(3)
    )

    assert run(openai).attempts == 3
    assert len(transport.requests) == 3


def test_a_long_retry_after_gives_up_instead_of_waiting():
    openai, transport, sleep = embedder(httpx.Response(429, headers={"retry-after": "120"}))

    with pytest.raises(ProviderUnavailable):
        run(openai)

    assert len(transport.requests) == 1
    assert sleep.delays == []


@pytest.mark.parametrize("status", [401, 403, 404])
def test_credential_and_model_errors_are_unavailable_without_retry(status):
    openai, transport, _ = embedder(httpx.Response(status))

    with pytest.raises(ProviderUnavailable):
        run(openai)

    assert len(transport.requests) == 1


def test_400_is_an_invalid_request_without_retry():
    openai, transport, _ = embedder(httpx.Response(400, json={"error": {"message": "too long"}}))

    with pytest.raises(EmbeddingFailed):
        run(openai)

    assert len(transport.requests) == 1


# ---- Strict validation ----


@pytest.mark.parametrize(
    "reply",
    [
        httpx.Response(200, text="not json"),
        httpx.Response(200, json={"object": "list"}),
        httpx.Response(200, json=[1, 2, 3]),
        body_with([{"index": 0, "embedding": vector(0)}]),  # too few
        body_with([{"index": i, "embedding": vector(i)} for i in range(4)]),  # too many
        body_with([{"index": 0, "embedding": vector(0)}] * 3),  # duplicate index
        body_with([{"index": i + 1, "embedding": vector(i)} for i in range(3)]),  # out of range
        body_with(
            [
                {"index": "0", "embedding": vector(0)},
                {"index": 1, "embedding": vector(1)},
                {"index": 2, "embedding": vector(2)},
            ]
        ),
        body_with([{"index": i, "embedding": vector(i, 1535)} for i in range(3)]),
        body_with([{"index": i, "embedding": vector(i, 3072)} for i in range(3)]),
        body_with([{"index": i, "embedding": [*vector(i)[:-1], "0.1"]} for i in range(3)]),
        body_with([{"index": i, "embedding": [*vector(i)[:-1], True]} for i in range(3)]),
        body_with([{"index": i, "embedding": None} for i in range(3)]),
    ],
)
def test_malformed_responses_are_rejected_entirely(reply):
    openai, _, _ = embedder(reply)

    with pytest.raises(EmbeddingFailed):
        run(openai)


@pytest.mark.parametrize("value", ["NaN", "Infinity", "-Infinity"])
def test_non_finite_values_are_rejected(value):
    # Python's JSON parser accepts these tokens, so they must be rejected explicitly.
    values = ",".join(["0.5"] * 1535 + [value])
    items = ",".join(f'{{"index": {i}, "embedding": [{values}]}}' for i in range(3))
    openai, _, _ = embedder(httpx.Response(200, text=f'{{"data": [{items}]}}'))

    with pytest.raises(EmbeddingFailed):
        run(openai)


# ---- Configuration ----


def test_missing_key_is_an_unconfigured_embedder_that_is_always_unavailable():
    configured = journal_embedder_from_environment({})

    assert isinstance(configured, UnconfiguredEmbedder)
    assert (configured.provider, configured.model, configured.dimensions) == (
        "openai",
        "text-embedding-3-small",
        1536,
    )
    assert configured.configured is False
    with pytest.raises(ProviderUnavailable):
        asyncio.run(configured.embed(["x"], purpose="query"))


def test_key_and_model_come_from_the_environment():
    configured = journal_embedder_from_environment(
        {"OPENAI_API_KEY": " key ", "LIFEOS_AI_JOURNAL_EMBEDDING_MODEL": " other-model "}
    )

    assert isinstance(configured, OpenAIJournalEmbedder)
    assert (configured.model, configured.dimensions, configured.configured) == (
        "other-model",
        1536,
        True,
    )
    asyncio.run(configured.aclose())


# ---- Privacy ----


def test_logs_carry_identity_and_counts_but_never_text_or_vectors(caplog):
    caplog.set_level(logging.INFO, logger="lifeos_ai.journal_memory")
    openai, _, _ = embedder(httpx.Response(429), embeddings_reply(3, tokens=57))

    run(openai)

    assert len(caplog.records) == 1
    message = caplog.records[0].getMessage()
    for field in (
        "result=success",
        "outcome=embedded",
        "purpose=index",
        "provider=openai",
        "model=text-embedding-3-small",
        "dimensions=1536",
        "items=3",
        "attempts=2",
        "input_tokens=57",
        "seconds=",
    ):
        assert field in message
    assert "Giulia" not in caplog.text and "diario" not in caplog.text
    assert json.dumps(vector(0)[0]) not in message.split("seconds=")[0]
    assert "test-openai-key" not in caplog.text


def test_failure_logs_carry_no_provider_text(caplog):
    caplog.set_level(logging.INFO, logger="lifeos_ai.journal_memory")
    openai, _, _ = embedder(httpx.Response(400, json={"error": {"message": "secret detail"}}))

    with pytest.raises(EmbeddingFailed):
        run(openai, purpose="query")

    message = caplog.records[0].getMessage()
    assert "result=invalid outcome=http_400 purpose=query" in message
    assert "secret detail" not in caplog.text and "Giulia" not in caplog.text
