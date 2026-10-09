"""AI-004: the Gemini journal embedding adapter, offline (scripted transport, recorded sleeps)."""

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
from lifeos_ai.journal_memory.gemini_embeddings import (
    DOCUMENT_FORMAT,
    QUERY_FORMAT,
    GeminiJournalEmbedder,
)
from tests.journal_memory_fixtures import ITALIAN_ENTRY, embedder, embeddings_reply, vector

TEXTS = [f"Mare\n\n{ITALIAN_ENTRY}", "Seconda parte del diario.", "Terza parte."]
BATCH_URL = "https://gemini.test/v1beta/models/gemini-embedding-2:batchEmbedContents"


def run(gemini, texts=TEXTS, purpose="index"):
    return asyncio.run(gemini.embed(texts, purpose=purpose))


def body_with(embeddings, status=200):
    return httpx.Response(status, json={"embeddings": embeddings})


def gemini_error(status, code, *, reason=None):
    error = {"code": status, "message": "provider secret detail", "status": code}
    if reason is not None:
        error["details"] = [{"@type": "type.googleapis.com/google.rpc.ErrorInfo", "reason": reason}]
    return httpx.Response(status, json={"error": error})


def request_texts(transport, index=0):
    return [
        request["content"]["parts"][0]["text"] for request in transport.bodies()[index]["requests"]
    ]


# ---- Happy path ----


def test_input_formatting_is_pinned():
    # Changing either template changes every vector: that is a new embedding identity.
    assert QUERY_FORMAT == "task: search result | query: {text}"
    assert DOCUMENT_FORMAT == "title: none | text: {text}"


def test_multiple_chunks_are_one_batch_call_with_one_independent_request_per_chunk():
    gemini, transport, sleep = embedder(embeddings_reply(3, tokens=57))

    result = run(gemini)

    assert result.vectors == [vector(0), vector(1), vector(2)]
    assert len({tuple(values) for values in result.vectors}) == 3
    assert all(len(values) == 1536 for values in result.vectors)
    assert (result.attempts, result.input_tokens) == (1, 57)
    assert sleep.delays == []

    request = transport.requests[0]
    assert str(request.url) == BATCH_URL
    assert request.headers["x-goog-api-key"] == "test-gemini-key"
    assert "authorization" not in request.headers
    assert "key=" not in str(request.url)
    # Never one content with several parts: gemini-embedding-2 would aggregate them into one vector.
    assert transport.bodies() == [
        {
            "requests": [
                {
                    "model": "models/gemini-embedding-2",
                    "content": {"parts": [{"text": f"title: none | text: {text}"}]},
                    "outputDimensionality": 1536,
                }
                for text in TEXTS
            ]
        }
    ]


def test_one_document_gives_one_vector_with_the_document_format():
    gemini, transport, _ = embedder(embeddings_reply(1))

    result = run(gemini, texts=[TEXTS[0]])

    assert result.vectors == [vector(0)]
    # The chunker's embedding input (title + chunk) is kept whole; nothing is invented or dropped.
    assert request_texts(transport) == [f"title: none | text: Mare\n\n{ITALIAN_ENTRY}"]


def test_one_query_gives_one_vector_with_the_query_format():
    gemini, transport, _ = embedder(embeddings_reply(1))

    result = run(gemini, texts=["Con chi sono andato al mare?"], purpose="query")

    assert result.vectors == [vector(0)]
    assert len(result.vectors[0]) == 1536
    assert str(transport.requests[0].url) == BATCH_URL
    assert request_texts(transport) == ["task: search result | query: Con chi sono andato al mare?"]
    assert transport.bodies()[0]["requests"][0]["outputDimensionality"] == 1536


def test_vectors_keep_the_response_order_which_is_the_request_order():
    gemini, _, _ = embedder(body_with([{"values": vector(seed)} for seed in (5, 3, 9)]))

    assert run(gemini).vectors == [vector(5), vector(3), vector(9)]


def test_an_unknown_purpose_is_a_programming_error_before_any_request():
    gemini, transport, _ = embedder()

    with pytest.raises(ValueError):
        run(gemini, purpose="other")

    assert transport.requests == []


@pytest.mark.parametrize(
    "usage",
    [None, {}, {"promptTokenCount": "57"}, {"promptTokenCount": -1}, {"promptTokenCount": True}],
)
def test_missing_or_invalid_usage_is_unknown_not_zero(usage):
    body = {"embeddings": [{"values": vector(index)} for index in range(3)]}
    if usage is not None:
        body["usageMetadata"] = usage
    gemini, _, _ = embedder(httpx.Response(200, json=body))

    assert run(gemini).input_tokens is None


# ---- Retries ----


def test_429_is_retried_and_then_succeeds():
    gemini, transport, sleep = embedder(
        gemini_error(429, "RESOURCE_EXHAUSTED"), embeddings_reply(3)
    )

    result = run(gemini)

    assert result.attempts == 2
    assert len(transport.requests) == 2
    assert sleep.delays == [1.0]


def test_5xx_is_retried_with_bounded_backoff_then_unavailable():
    gemini, transport, sleep = embedder(
        httpx.Response(500), httpx.Response(502), httpx.Response(503)
    )

    with pytest.raises(ProviderUnavailable):
        run(gemini)

    assert len(transport.requests) == 3
    assert sleep.delays == [1.0, 2.0]


def test_retries_stop_at_the_attempt_cap():
    gemini, transport, _ = embedder(
        httpx.Response(504), httpx.Response(504), httpx.Response(504), embeddings_reply(3)
    )

    with pytest.raises(ProviderUnavailable):
        run(gemini)

    assert len(transport.requests) == 3


def test_timeouts_and_transport_errors_are_retried():
    gemini, transport, _ = embedder(
        httpx.ReadTimeout("slow"), httpx.ConnectError("down"), embeddings_reply(3)
    )

    assert run(gemini).attempts == 3
    assert len(transport.requests) == 3


@pytest.mark.parametrize("failure", [httpx.ReadTimeout("slow"), httpx.ConnectError("down")])
def test_persistent_timeouts_or_transport_errors_are_unavailable(failure):
    gemini, transport, _ = embedder(failure, failure, failure)

    with pytest.raises(ProviderUnavailable):
        run(gemini)

    assert len(transport.requests) == 3


def test_a_long_retry_after_gives_up_instead_of_waiting():
    gemini, transport, sleep = embedder(httpx.Response(429, headers={"retry-after": "120"}))

    with pytest.raises(ProviderUnavailable):
        run(gemini)

    assert len(transport.requests) == 1
    assert sleep.delays == []


@pytest.mark.parametrize("status", [401, 403, 404])
def test_credential_permission_and_model_errors_are_unavailable_without_retry(status):
    gemini, transport, _ = embedder(gemini_error(status, "PERMISSION_DENIED"))

    with pytest.raises(ProviderUnavailable):
        run(gemini)

    assert len(transport.requests) == 1


@pytest.mark.parametrize(
    "reply",
    [
        gemini_error(400, "INVALID_ARGUMENT", reason="API_KEY_INVALID"),
        gemini_error(400, "INVALID_ARGUMENT", reason="API_KEY_EXPIRED"),
        gemini_error(400, "FAILED_PRECONDITION"),
    ],
)
def test_a_400_for_the_key_or_project_is_configuration_and_unavailable(reply):
    gemini, transport, _ = embedder(reply)

    with pytest.raises(ProviderUnavailable):
        run(gemini)

    assert len(transport.requests) == 1


@pytest.mark.parametrize(
    "reply",
    [
        gemini_error(400, "INVALID_ARGUMENT"),
        httpx.Response(400, text="not json"),
        httpx.Response(400),
    ],
)
def test_any_other_400_is_an_invalid_request_without_retry(reply):
    gemini, transport, _ = embedder(reply)

    with pytest.raises(EmbeddingFailed):
        run(gemini)

    assert len(transport.requests) == 1


# ---- Strict validation ----


@pytest.mark.parametrize(
    "reply",
    [
        httpx.Response(200, text="not json"),
        httpx.Response(200, json={}),
        httpx.Response(200, json=[1, 2, 3]),
        httpx.Response(200, json={"embedding": {"values": vector(0)}}),  # single, not batch
        body_with(None),
        body_with([{"values": vector(0)}]),  # too few
        body_with([{"values": vector(i)} for i in range(4)]),  # too many
        body_with([{"values": vector(i, 1535)} for i in range(3)]),
        body_with([{"values": vector(i, 3072)} for i in range(3)]),
        body_with([{"values": [*vector(i)[:-1], "0.1"]} for i in range(3)]),
        body_with([{"values": [*vector(i)[:-1], True]} for i in range(3)]),
        body_with([{"values": [*vector(i)[:-1], None]} for i in range(3)]),
        body_with([{"values": None} for _ in range(3)]),
        body_with([{} for _ in range(3)]),
        body_with([vector(i) for i in range(3)]),
        body_with([{"values": vector(0)}, {"values": vector(1)}, {"values": []}]),  # partial
    ],
)
def test_malformed_responses_are_rejected_entirely(reply):
    gemini, _, _ = embedder(reply)

    with pytest.raises(EmbeddingFailed):
        run(gemini)


@pytest.mark.parametrize("value", ["NaN", "Infinity", "-Infinity"])
def test_non_finite_values_are_rejected(value):
    # Python's JSON parser accepts these tokens, so they must be rejected explicitly.
    values = ",".join(["0.5"] * 1535 + [value])
    items = ",".join(f'{{"values": [{values}]}}' for _ in range(3))
    gemini, _, _ = embedder(httpx.Response(200, text=f'{{"embeddings": [{items}]}}'))

    with pytest.raises(EmbeddingFailed):
        run(gemini)


# ---- Configuration ----


def test_missing_key_is_an_unconfigured_embedder_that_is_always_unavailable():
    configured = journal_embedder_from_environment({"OPENAI_API_KEY": "not-the-journal-key"})

    assert isinstance(configured, UnconfiguredEmbedder)
    assert (configured.provider, configured.model, configured.dimensions) == (
        "google",
        "gemini-embedding-2",
        1536,
    )
    assert configured.configured is False
    with pytest.raises(ProviderUnavailable):
        asyncio.run(configured.embed(["x"], purpose="query"))


def test_key_and_model_come_from_the_environment():
    configured = journal_embedder_from_environment(
        {"GEMINI_API_KEY": " key ", "LIFEOS_AI_JOURNAL_EMBEDDING_MODEL": " other-model "}
    )

    assert isinstance(configured, GeminiJournalEmbedder)
    assert (
        configured.provider,
        configured.model,
        configured.dimensions,
        configured.configured,
    ) == (
        "google",
        "other-model",
        1536,
        True,
    )
    asyncio.run(configured.aclose())


def test_the_default_model_is_gemini_embedding_2():
    configured = journal_embedder_from_environment({"GEMINI_API_KEY": "key"})

    assert (configured.provider, configured.model, configured.dimensions) == (
        "google",
        "gemini-embedding-2",
        1536,
    )
    asyncio.run(configured.aclose())


# ---- Privacy ----


def test_logs_carry_identity_and_counts_but_never_text_vectors_or_key(caplog):
    caplog.set_level(logging.DEBUG)
    gemini, _, _ = embedder(httpx.Response(429), embeddings_reply(3, tokens=57))

    run(gemini)

    records = [record for record in caplog.records if record.name == "lifeos_ai.journal_memory"]
    assert len(records) == 1
    message = records[0].getMessage()
    for field in (
        "result=success",
        "outcome=embedded",
        "purpose=index",
        "provider=google",
        "model=gemini-embedding-2",
        "dimensions=1536",
        "items=3",
        "attempts=2",
        "input_tokens=57",
        "seconds=",
    ):
        assert field in message
    assert "Giulia" not in caplog.text and "diario" not in caplog.text and "Mare" not in caplog.text
    assert "search result" not in caplog.text and "title: none" not in caplog.text
    assert json.dumps(vector(0)[0]) not in message.split("seconds=")[0]
    assert "test-gemini-key" not in caplog.text


def test_query_logs_carry_no_question(caplog):
    caplog.set_level(logging.DEBUG)
    gemini, _, _ = embedder(embeddings_reply(1, tokens=None))

    run(gemini, texts=["Con chi sono andato al mare?"], purpose="query")

    assert "purpose=query" in caplog.text and "input_tokens=-" in caplog.text
    assert "andato" not in caplog.text and "test-gemini-key" not in caplog.text


def test_failure_logs_and_errors_carry_no_provider_text(caplog):
    caplog.set_level(logging.DEBUG)
    gemini, _, _ = embedder(gemini_error(400, "INVALID_ARGUMENT"))

    with pytest.raises(EmbeddingFailed) as failure:
        run(gemini, purpose="query")

    assert "result=invalid outcome=http_400 purpose=query" in caplog.text
    assert "secret detail" not in caplog.text and "Giulia" not in caplog.text
    assert "secret detail" not in str(failure.value)


def test_configuration_failure_logs_carry_no_provider_text_or_key(caplog):
    caplog.set_level(logging.DEBUG)
    gemini, _, _ = embedder(gemini_error(400, "INVALID_ARGUMENT", reason="API_KEY_INVALID"))

    with pytest.raises(ProviderUnavailable) as failure:
        run(gemini)

    assert "result=unavailable outcome=http_400_configuration purpose=index" in caplog.text
    assert "secret detail" not in caplog.text and "test-gemini-key" not in caplog.text
    assert "secret detail" not in str(failure.value)
