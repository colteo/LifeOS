"""AI-006: the OpenAI journal embedding candidate adapter, offline (scripted transport, recorded
sleeps). No network, no key."""

import asyncio
import json
import logging

import httpx
import pytest

from lifeos_ai.config import ProviderSelectionError, journal_embedder_from_environment
from lifeos_ai.journal_memory.embedding import (
    EmbeddingFailed,
    ProviderUnavailable,
    UnconfiguredEmbedder,
)
from lifeos_ai.journal_memory.gemini_embeddings import GeminiJournalEmbedder
from lifeos_ai.journal_memory.openai_embeddings import (
    OpenAIEmbeddingSettings,
    OpenAIJournalEmbedder,
)
from tests.fakes import RecordingSleep, ScriptedTransport
from tests.journal_memory_fixtures import ITALIAN_ENTRY, vector

BASE_URL = "https://openai.test/v1"
URL = f"{BASE_URL}/embeddings"
KEY = "test-openai-key"
TEXTS = [f"Mare\n\n{ITALIAN_ENTRY}", "Seconda parte del diario.", "Terza parte."]


def embedder(*replies, **settings):
    transport = ScriptedTransport(*replies)
    sleep = RecordingSleep()
    openai = OpenAIJournalEmbedder(
        OpenAIEmbeddingSettings(api_key=KEY, base_url=BASE_URL, **settings),
        client=httpx.AsyncClient(transport=transport, base_url=BASE_URL),
        sleep=sleep,
    )
    return openai, transport, sleep


def item(index, values=None):
    return {
        "object": "embedding",
        "index": index,
        "embedding": vector(index) if values is None else values,
    }


def reply(items, *, usage=None, status=200, headers=None):
    body = {"object": "list", "data": items, "model": "text-embedding-3-small"}
    if usage is not None:
        body["usage"] = usage
    return httpx.Response(status, json=body, headers=headers)


def ok(count, *, usage=None):
    return reply(
        [item(index) for index in range(count)],
        usage={"prompt_tokens": 57, "total_tokens": 57} if usage is None else usage,
    )


def error(status, *, code=None, param=None, kind="invalid_request_error", headers=None):
    body = {"error": {"message": "provider secret detail", "type": kind}}
    if code is not None:
        body["error"]["code"] = code
    if param is not None:
        body["error"]["param"] = param
    return httpx.Response(status, json=body, headers=headers)


def run(openai, texts=TEXTS, purpose="index"):
    return asyncio.run(openai.embed(texts, purpose=purpose))


# ---- Request ----


def test_the_request_is_one_bearer_call_with_explicit_model_dimensions_and_float_format():
    openai, transport, sleep = embedder(ok(3))

    result = run(openai)

    assert result.vectors == [vector(0), vector(1), vector(2)]
    assert (result.attempts, result.input_tokens) == (1, 57)
    assert sleep.delays == []
    request = transport.requests[0]
    assert (request.method, str(request.url)) == ("POST", URL)
    assert request.headers["authorization"] == f"Bearer {KEY}"
    assert "x-goog-api-key" not in request.headers
    # One independent input per chunk, the port's texts unchanged: no prefixes or task formatting.
    assert transport.bodies() == [
        {
            "model": "text-embedding-3-small",
            "input": TEXTS,
            "dimensions": 1536,
            "encoding_format": "float",
        }
    ]


@pytest.mark.parametrize("purpose", ["index", "query"])
def test_one_input_gives_one_vector_and_purpose_never_changes_the_input(purpose):
    openai, transport, _ = embedder(ok(1))

    result = run(openai, ["Con chi sono andato al mare?"], purpose)

    assert result.vectors == [vector(0)]
    assert transport.bodies()[0]["input"] == ["Con chi sono andato al mare?"]


def test_the_identity_is_openai_text_embedding_3_small_1536():
    openai, _, _ = embedder()

    assert (openai.provider, openai.model, openai.dimensions, openai.configured) == (
        "openai",
        "text-embedding-3-small",
        1536,
        True,
    )


def test_an_unknown_purpose_or_no_text_is_a_programming_error_before_any_request():
    openai, transport, _ = embedder()

    with pytest.raises(ValueError):
        run(openai, purpose="document")
    with pytest.raises(ValueError):
        run(openai, texts=[])
    assert transport.requests == []


# ---- Indexes and order ----


def test_vectors_are_restored_to_input_order_by_index_not_array_position():
    openai, _, _ = embedder(reply([item(2), item(0), item(1)]))

    assert run(openai).vectors == [vector(0), vector(1), vector(2)]


@pytest.mark.parametrize(
    "items",
    [
        [item(0), item(0), item(2)],  # duplicate index (and 1 missing)
        [item(0), item(1), item(3)],  # out of range (2 missing)
        [item(0), item(1), {"object": "embedding", "embedding": vector(2)}],  # no index
        [item(0), item(1), {**item(2), "index": "2"}],  # non-integer index
        [item(0), item(1), {**item(2), "index": True}],  # bool is not an index
        [item(0), item(1), {**item(2), "index": -1}],
    ],
)
def test_duplicate_missing_or_invalid_indexes_are_invalid_output(items):
    openai, _, _ = embedder(reply(items))

    with pytest.raises(EmbeddingFailed):
        run(openai)


@pytest.mark.parametrize("count", [0, 2, 4])
def test_wrong_cardinality_is_invalid_output(count):
    openai, _, _ = embedder(ok(count))

    with pytest.raises(EmbeddingFailed):
        run(openai)


# ---- Vector validation ----


@pytest.mark.parametrize("dimensions", [0, 1535, 1537, 3072])
def test_every_vector_has_exactly_1536_dimensions(dimensions):
    openai, _, _ = embedder(reply([item(0), item(1), item(2, vector(2, dimensions))]))

    with pytest.raises(EmbeddingFailed):
        run(openai)


@pytest.mark.parametrize("value", ["0.1", None, True, [0.1], {"v": 1}])
def test_non_numeric_values_are_invalid_output(value):
    values = vector(1)
    values[17] = value
    openai, _, _ = embedder(reply([item(0), item(1, values), item(2)]))

    with pytest.raises(EmbeddingFailed):
        run(openai)


@pytest.mark.parametrize("value", ["NaN", "Infinity", "-Infinity"])
def test_non_finite_values_are_invalid_output(value):
    # Python's json writes NaN/Infinity tokens; httpx reads them back as floats.
    body = json.dumps(
        {"data": [{"index": 0, "embedding": [0.1] * 1535 + [float(value)]}]}, allow_nan=True
    )
    openai, _, _ = embedder(httpx.Response(200, content=body.encode()))

    with pytest.raises(EmbeddingFailed):
        run(openai, ["x"])


@pytest.mark.parametrize(
    "response",
    [
        httpx.Response(200, content=b"not json"),
        httpx.Response(200, json=[]),
        httpx.Response(200, json={"object": "list"}),
        httpx.Response(200, json={"data": None}),
        httpx.Response(200, json={"data": [None, None, None]}),
        httpx.Response(200, json={"data": [{"index": 0}, {"index": 1}, {"index": 2}]}),
    ],
)
def test_malformed_responses_are_rejected_entirely(response):
    openai, _, sleep = embedder(response)

    with pytest.raises(EmbeddingFailed):
        run(openai)
    assert sleep.delays == []  # invalid output is final, never retried


def test_integer_values_are_accepted_as_floats():
    values = [1] + [0.0] * 1535
    openai, _, _ = embedder(reply([item(0, values)]))

    assert run(openai, ["x"]).vectors == [[1.0] + [0.0] * 1535]


# ---- Usage ----


def test_prompt_and_total_tokens_are_captured_and_logged(caplog):
    openai, _, _ = embedder(ok(3, usage={"prompt_tokens": 40, "total_tokens": 41}))

    with caplog.at_level(logging.INFO, logger="lifeos_ai.journal_memory"):
        result = run(openai)

    assert result.input_tokens == 40
    assert "input_tokens=40 total_tokens=41" in caplog.text


@pytest.mark.parametrize("usage", [None, {}, {"prompt_tokens": -1}, {"prompt_tokens": "40"}])
def test_missing_or_invalid_usage_is_unknown_not_zero(usage, caplog):
    response = reply([item(0)])
    if usage is not None:
        response = reply([item(0)], usage=usage)
    openai, _, _ = embedder(response)

    with caplog.at_level(logging.INFO, logger="lifeos_ai.journal_memory"):
        result = run(openai, ["x"])

    assert result.input_tokens is None
    assert "input_tokens=- total_tokens=-" in caplog.text


# ---- HTTP failures ----


@pytest.mark.parametrize("status", [401, 403, 404])
def test_credential_permission_and_model_errors_are_unavailable_without_retry(status):
    openai, transport, sleep = embedder(error(status, code="model_not_found"))

    with pytest.raises(ProviderUnavailable):
        run(openai)
    assert (len(transport.requests), sleep.delays) == (1, [])


@pytest.mark.parametrize(
    "response",
    [
        error(400, code="model_not_found"),
        error(400, param="model"),
        error(400, code="invalid_api_key"),
        error(400, kind="authentication_error"),
    ],
)
def test_a_400_for_the_key_or_model_is_configuration_and_unavailable(response):
    openai, transport, _ = embedder(response)

    with pytest.raises(ProviderUnavailable):
        run(openai)
    assert len(transport.requests) == 1


@pytest.mark.parametrize(
    "response",
    [error(400), error(400, param="input"), httpx.Response(400, content=b"<html>"), error(422)],
)
def test_any_other_400_is_an_invalid_request_without_retry(response):
    openai, transport, _ = embedder(response)

    with pytest.raises(EmbeddingFailed):
        run(openai)
    assert len(transport.requests) == 1


def test_429_is_retried_and_then_succeeds():
    openai, transport, sleep = embedder(error(429, code="rate_limit_exceeded"), ok(3))

    assert run(openai).attempts == 2
    assert (len(transport.requests), sleep.delays) == (2, [1.0])


def test_a_429_for_exhausted_quota_is_unavailable_without_retry():
    openai, transport, sleep = embedder(error(429, code="insufficient_quota"))

    with pytest.raises(ProviderUnavailable):
        run(openai)
    assert (len(transport.requests), sleep.delays) == (1, [])


@pytest.mark.parametrize("status", [500, 502, 503, 504])
def test_5xx_is_retried(status):
    openai, transport, sleep = embedder(error(status), ok(3))

    assert run(openai).attempts == 2
    assert sleep.delays == [1.0]


def test_retries_stop_at_the_attempt_cap_with_bounded_backoff():
    openai, transport, sleep = embedder(error(503), error(503), error(503), ok(3))

    with pytest.raises(ProviderUnavailable):
        run(openai)
    assert (len(transport.requests), sleep.delays) == (3, [1.0, 2.0])


@pytest.mark.parametrize(
    "failure",
    [httpx.ReadTimeout("slow"), httpx.ConnectError("refused")],
    ids=["timeout", "transport"],
)
def test_timeouts_and_transport_errors_are_retried(failure):
    openai, transport, sleep = embedder(failure, ok(3))

    assert run(openai).attempts == 2
    assert sleep.delays == [1.0]


@pytest.mark.parametrize(
    "failure",
    [httpx.ReadTimeout("slow"), httpx.ConnectError("refused")],
    ids=["timeout", "transport"],
)
def test_persistent_timeouts_or_transport_errors_are_unavailable(failure):
    openai, transport, _ = embedder(failure, failure, failure)

    with pytest.raises(ProviderUnavailable):
        run(openai)
    assert len(transport.requests) == 3


def test_retry_after_is_honoured_and_a_long_one_gives_up():
    openai, _, sleep = embedder(error(429, headers={"retry-after": "0.5"}), ok(3))
    run(openai)
    assert sleep.delays == [0.5]

    openai, transport, sleep = embedder(error(429, headers={"retry-after": "120"}))
    with pytest.raises(ProviderUnavailable):
        run(openai)
    assert (len(transport.requests), sleep.delays) == (1, [])


def test_a_finite_timeout_is_sent_with_every_request():
    openai, transport, _ = embedder(ok(3), timeout_seconds=7.5)

    run(openai)

    assert transport.requests[0].extensions["timeout"]["read"] == 7.5


# ---- Configuration ----


def test_a_missing_key_is_a_programming_error_for_the_adapter():
    with pytest.raises(ValueError, match="OPENAI_API_KEY"):
        OpenAIJournalEmbedder(OpenAIEmbeddingSettings(api_key=" "))


def test_openai_is_selected_only_explicitly_and_the_default_stays_gemini():
    default = journal_embedder_from_environment({"GEMINI_API_KEY": "g", "OPENAI_API_KEY": "o"})
    selected = journal_embedder_from_environment(
        {
            "LIFEOS_AI_JOURNAL_EMBEDDING_PROVIDER": " OpenAI ",
            "GEMINI_API_KEY": "g",
            "OPENAI_API_KEY": "o",
        }
    )

    assert isinstance(default, GeminiJournalEmbedder)
    assert isinstance(selected, OpenAIJournalEmbedder)
    assert (selected.provider, selected.model, selected.dimensions) == (
        "openai",
        "text-embedding-3-small",
        1536,
    )
    asyncio.run(default.aclose())
    asyncio.run(selected.aclose())


def test_openai_without_its_key_is_unconfigured_never_a_gemini_fallback():
    configured = journal_embedder_from_environment(
        {"LIFEOS_AI_JOURNAL_EMBEDDING_PROVIDER": "openai", "GEMINI_API_KEY": "g"}
    )

    assert isinstance(configured, UnconfiguredEmbedder)
    assert (configured.provider, configured.model, configured.configured) == (
        "openai",
        "text-embedding-3-small",
        False,
    )
    with pytest.raises(ProviderUnavailable):
        asyncio.run(configured.embed(["x"], purpose="query"))


def test_the_openai_model_can_be_overridden():
    configured = journal_embedder_from_environment(
        {
            "LIFEOS_AI_JOURNAL_EMBEDDING_PROVIDER": "openai",
            "OPENAI_API_KEY": "o",
            "LIFEOS_AI_JOURNAL_EMBEDDING_MODEL": "other-model",
        }
    )

    assert configured.model == "other-model"
    asyncio.run(configured.aclose())


def test_an_unknown_provider_stops_the_service_and_names_only_the_variable():
    with pytest.raises(
        ProviderSelectionError, match="LIFEOS_AI_JOURNAL_EMBEDDING_PROVIDER"
    ) as info:
        journal_embedder_from_environment(
            {"LIFEOS_AI_JOURNAL_EMBEDDING_PROVIDER": "secret-typo", "OPENAI_API_KEY": "o"}
        )
    assert "secret-typo" not in str(info.value)


# ---- Privacy ----


def test_logs_carry_identity_and_counts_but_never_text_vectors_or_key(caplog):
    openai, _, _ = embedder(ok(3))

    with caplog.at_level(logging.DEBUG):
        run(openai)

    text = caplog.text
    assert "provider=openai model=text-embedding-3-small dimensions=1536" in text
    assert "purpose=index" in text and "items=3 attempts=1" in text
    assert KEY not in text
    assert "Giulia" not in text and "Seconda parte" not in text
    assert str(vector(0)[1]) not in text


def test_query_logs_carry_no_question(caplog):
    openai, _, _ = embedder(ok(1))

    with caplog.at_level(logging.DEBUG):
        run(openai, ["Quanto ho speso per il dentista?"], "query")

    assert "dentista" not in caplog.text
    assert "purpose=query" in caplog.text


@pytest.mark.parametrize(
    "response",
    [error(400), error(401, code="invalid_api_key"), error(500), ok(2)],
)
def test_failure_logs_and_errors_carry_no_provider_text_or_key(response, caplog):
    openai, _, _ = embedder(response, response, response)

    with (
        caplog.at_level(logging.DEBUG),
        pytest.raises((EmbeddingFailed, ProviderUnavailable)) as info,
    ):
        run(openai)

    for text in (caplog.text, str(info.value)):
        assert "provider secret detail" not in text
        assert KEY not in text
        assert "Giulia" not in text
