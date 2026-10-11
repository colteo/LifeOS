"""AI-006: the OpenAI journal answer candidate adapter (Responses API), offline (scripted transport,
recorded sleeps). No network, no key."""

import asyncio
import json
import logging

import httpx
import pytest

from lifeos_ai.config import ProviderSelectionError, journal_answerer_from_environment
from lifeos_ai.groq_chat import GroqSettings
from lifeos_ai.journal_memory.answer import AnswerFailed, ProviderUnavailable, UnconfiguredAnswerer
from lifeos_ai.journal_memory.groq import GroqJournalAnswerer
from lifeos_ai.journal_memory.openai_answer import OpenAIAnswerSettings, OpenAIJournalAnswerer
from lifeos_ai.journal_memory.prompt import PROMPTS, SYSTEM_PROMPT, build_messages
from lifeos_ai.journal_memory.schema import ANSWER_OUTPUT_SCHEMA, JournalAnswer
from tests.fakes import RecordingSleep, ScriptedTransport
from tests.test_journal_answer import ANSWERED, INSUFFICIENT, REQUEST

BASE_URL = "https://openai.test/v1"
URL = f"{BASE_URL}/responses"
KEY = "test-openai-key"
ANSWER_TEXT = ANSWERED["answer"]
USAGE = {
    "input_tokens": 1200,
    "input_tokens_details": {"cached_tokens": 1024},
    "output_tokens": 300,
    "output_tokens_details": {"reasoning_tokens": 256},
    "total_tokens": 1500,
}


def answerer(*replies, **settings):
    transport = ScriptedTransport(*replies)
    sleep = RecordingSleep()
    openai = OpenAIJournalAnswerer(
        OpenAIAnswerSettings(api_key=KEY, base_url=BASE_URL, **settings),
        client=httpx.AsyncClient(transport=transport, base_url=BASE_URL),
        sleep=sleep,
    )
    return openai, transport, sleep


def message(*parts):
    return {
        "type": "message",
        "id": "msg_x",
        "role": "assistant",
        "status": "completed",
        "content": list(parts),
    }


def text_part(content):
    if not isinstance(content, str):
        content = json.dumps(content)
    return {"type": "output_text", "text": content, "annotations": []}


REASONING = {"type": "reasoning", "id": "rs_x", "summary": [], "encrypted_content": None}


def response(output, *, status="completed", usage=USAGE, http=200, **extra):
    body = {
        "id": "resp_secret_id",
        "object": "response",
        "status": status,
        "output": output,
        **extra,
    }
    if usage is not None:
        body["usage"] = usage
    return httpx.Response(http, json=body)


def ok(content=ANSWERED, **kwargs):
    return response([REASONING, message(text_part(content))], **kwargs)


def error(status, *, code=None, param=None, kind="invalid_request_error", headers=None):
    body = {"error": {"message": "provider secret detail", "type": kind}}
    if code is not None:
        body["error"]["code"] = code
    if param is not None:
        body["error"]["param"] = param
    return httpx.Response(status, json=body, headers=headers)


def run(openai, request=REQUEST):
    return asyncio.run(openai.answer(request))


# ---- Request ----


def test_the_request_is_a_stateless_private_responses_call_with_strict_structured_output():
    openai, transport, _ = answerer(ok())

    run(openai)

    request = transport.requests[0]
    assert (request.method, str(request.url)) == ("POST", URL)
    assert request.headers["authorization"] == f"Bearer {KEY}"
    assert transport.bodies() == [
        {
            "model": "gpt-6.1-sol",
            "input": build_messages(REQUEST),
            "store": False,
            "reasoning": {"effort": "medium"},
            "max_output_tokens": 8192,
            "text": {
                "format": {
                    "type": "json_schema",
                    "name": "journal_answer",
                    "strict": True,
                    "schema": ANSWER_OUTPUT_SCHEMA,
                }
            },
        }
    ]


def test_store_is_always_explicitly_false():
    openai, transport, _ = answerer(ok())

    run(openai)

    assert transport.bodies()[0]["store"] is False


def test_no_tools_conversation_state_or_sampling_settings_are_sent():
    openai, transport, _ = answerer(ok())

    run(openai)

    body = transport.bodies()[0]
    for key in (
        "tools",
        "tool_choice",
        "previous_response_id",
        "conversation",
        "include",
        "background",
        "temperature",
        "top_p",
        "response_format",
        "messages",
    ):
        assert key not in body, key


def test_the_prompt_is_journal_rag_answer_v1_shared_with_groq():
    openai, transport, _ = answerer(ok())

    run(openai)

    system, user = transport.bodies()[0]["input"]
    assert system == {"role": "system", "content": PROMPTS["journal-rag-answer-v1"]}
    assert SYSTEM_PROMPT == PROMPTS["journal-rag-answer-v1"]
    assert "journal-rag-answer-v2" not in json.dumps(transport.bodies()[0])
    assert user["role"] == "user"
    # Exactly the messages the production Groq answerer sends for the same request.
    groq = GroqJournalAnswerer(GroqSettings(api_key="unused"))
    assert groq.request_body(REQUEST)["messages"] == [system, user]
    asyncio.run(groq.aclose())


def test_the_schema_is_the_existing_answer_contract_and_valid_for_strict_mode():
    openai, _, _ = answerer()

    schema = openai.request_body(REQUEST)["text"]["format"]["schema"]

    assert schema is ANSWER_OUTPUT_SCHEMA
    # Strict mode: every property required, no additional properties.
    assert schema["additionalProperties"] is False
    assert set(schema["required"]) == set(schema["properties"]) == set(JournalAnswer.model_fields)
    assert schema["properties"]["status"]["enum"] == ["answered", "insufficient_evidence"]


def test_the_identity_is_openai_gpt_6_1_sol_with_prompt_v1():
    openai, _, _ = answerer()

    assert (openai.provider, openai.model, openai.prompt_version, openai.configured) == (
        "openai",
        "gpt-6.1-sol",
        "journal-rag-answer-v1",
        True,
    )


def test_a_finite_timeout_is_sent_with_every_request():
    openai, transport, _ = answerer(ok(), timeout_seconds=12.0)

    run(openai)

    assert transport.requests[0].extensions["timeout"]["read"] == 12.0


# ---- Valid output ----


def test_a_valid_answered_response():
    openai, _, _ = answerer(ok())

    assert run(openai) == JournalAnswer(status="answered", answer=ANSWER_TEXT, citations=["S1"])


def test_a_valid_insufficient_evidence_response():
    openai, _, _ = answerer(ok(INSUFFICIENT))

    assert run(openai) == JournalAnswer(status="insufficient_evidence", answer="", citations=[])


def test_valid_citations_of_several_supplied_sources():
    openai, _, _ = answerer(ok({**ANSWERED, "citations": ["S2", "S1"]}))

    assert run(openai).citations == ["S2", "S1"]


def test_a_response_without_reasoning_items_is_accepted():
    openai, _, _ = answerer(response([message(text_part(ANSWERED))]))

    assert run(openai).status == "answered"


# ---- Invalid output (502) ----


@pytest.mark.parametrize(
    ("reply", "outcome"),
    [
        (
            response([message({"type": "refusal", "refusal": "I can't help with that."})]),
            "refusal",
        ),
        (
            response([message(text_part(ANSWERED), {"type": "refusal", "refusal": "no"})]),
            "refusal",
        ),
        (
            response(
                [message(text_part('{"status": "ans'))],
                status="incomplete",
                incomplete_details={"reason": "max_output_tokens"},
            ),
            "incomplete",
        ),
        (response([REASONING]), "missing_output"),
        (response([]), "missing_output"),
        (response(None), "missing_output"),
        (response([message()]), "missing_output"),
        (response([message({"type": "output_text"})]), "missing_output"),
        (response([message(text_part(ANSWERED), text_part(ANSWERED))]), "unexpected_output_item"),
        (
            response([message(text_part(ANSWERED)), message(text_part(ANSWERED))]),
            "unexpected_output_item",
        ),
        (
            response([{"type": "function_call", "name": "x", "arguments": "{}"}]),
            "unexpected_output_item",
        ),
        (response([{**message(text_part(ANSWERED)), "role": "user"}]), "unexpected_output_item"),
        (response([message(text_part(ANSWERED))], status="in_progress"), "invalid_output"),
        (httpx.Response(200, content=b"not json"), "invalid_output"),
        (httpx.Response(200, json=[]), "invalid_output"),
        (ok("not json at all"), "invalid_output"),
        (ok({**ANSWERED, "extra": 1}), "invalid_output"),
        (ok({"status": "answered", "answer": "", "citations": ["S1"]}), "invalid_output"),
        (ok({"status": "insufficient_evidence", "answer": "x", "citations": []}), "invalid_output"),
        (ok({**ANSWERED, "citations": ["S9"]}), "unknown_citation"),
    ],
)
def test_unusable_output_is_an_invalid_answer_without_retry(reply, outcome, caplog):
    openai, transport, sleep = answerer(reply)

    with (
        caplog.at_level(logging.INFO, logger="lifeos_ai.journal_memory"),
        pytest.raises(AnswerFailed),
    ):
        run(openai)

    assert f"result=invalid outcome={outcome} " in caplog.text
    assert (len(transport.requests), sleep.delays) == (1, [])


def test_a_failed_response_is_unavailable():
    openai, _, _ = answerer(
        response([], status="failed", error={"code": "server_error", "message": "secret"})
    )

    with pytest.raises(ProviderUnavailable):
        run(openai)


# ---- HTTP failures ----


@pytest.mark.parametrize("status", [401, 403, 404])
def test_credential_permission_and_model_errors_are_unavailable_without_retry(status):
    openai, transport, sleep = answerer(error(status, code="model_not_found"))

    with pytest.raises(ProviderUnavailable):
        run(openai)
    assert (len(transport.requests), sleep.delays) == (1, [])


def test_a_400_for_the_model_is_configuration_and_unavailable():
    openai, _, _ = answerer(error(400, code="model_not_found", param="model"))

    with pytest.raises(ProviderUnavailable):
        run(openai)


def test_any_other_400_is_an_invalid_answer_without_retry():
    openai, transport, _ = answerer(error(400, param="text.format.schema"))

    with pytest.raises(AnswerFailed):
        run(openai)
    assert len(transport.requests) == 1


def test_429_is_retried_and_then_succeeds():
    openai, transport, sleep = answerer(error(429, code="rate_limit_exceeded"), ok())

    assert run(openai).status == "answered"
    assert (len(transport.requests), sleep.delays) == (2, [1.0])


def test_a_429_for_exhausted_quota_is_unavailable_without_retry():
    openai, transport, _ = answerer(error(429, code="insufficient_quota"))

    with pytest.raises(ProviderUnavailable):
        run(openai)
    assert len(transport.requests) == 1


@pytest.mark.parametrize("status", [500, 502, 503, 504])
def test_5xx_is_retried(status):
    openai, transport, sleep = answerer(error(status, kind="server_error"), ok())

    assert run(openai).status == "answered"
    assert sleep.delays == [1.0]


@pytest.mark.parametrize(
    "failure",
    [httpx.ReadTimeout("slow"), httpx.ConnectError("refused")],
    ids=["timeout", "transport"],
)
def test_timeouts_and_transport_errors_are_retried_then_unavailable(failure):
    openai, transport, _ = answerer(failure, ok())
    assert run(openai).status == "answered"

    openai, transport, sleep = answerer(failure, failure, failure)
    with pytest.raises(ProviderUnavailable):
        run(openai)
    assert (len(transport.requests), sleep.delays) == (3, [1.0, 2.0])


def test_retries_stop_at_the_attempt_cap():
    openai, transport, _ = answerer(error(500), error(502), error(503), ok())

    with pytest.raises(ProviderUnavailable):
        run(openai)
    assert len(transport.requests) == 3


# ---- Usage ----


def test_token_usage_including_cached_and_reasoning_tokens_is_logged(caplog):
    openai, _, _ = answerer(ok())

    with caplog.at_level(logging.INFO, logger="lifeos_ai.journal_memory"):
        run(openai)

    assert (
        "input_tokens=1200 cached_input_tokens=1024 output_tokens=300 reasoning_tokens=256 "
        "total_tokens=1500"
    ) in caplog.text
    assert "provider=openai model=gpt-6.1-sol prompt=journal-rag-answer-v1" in caplog.text


@pytest.mark.parametrize(
    "usage",
    [None, {}, {"input_tokens": -1, "input_tokens_details": None, "output_tokens": "3"}],
)
def test_missing_usage_is_unknown_not_zero(usage, caplog):
    openai, _, _ = answerer(ok(usage=usage))

    with caplog.at_level(logging.INFO, logger="lifeos_ai.journal_memory"):
        run(openai)

    assert (
        "input_tokens=- cached_input_tokens=- output_tokens=- reasoning_tokens=- total_tokens=-"
    ) in caplog.text


def test_usage_of_an_invalid_answer_is_still_logged(caplog):
    openai, _, _ = answerer(ok({**ANSWERED, "citations": ["S9"]}))

    with (
        caplog.at_level(logging.INFO, logger="lifeos_ai.journal_memory"),
        pytest.raises(AnswerFailed),
    ):
        run(openai)

    assert "outcome=unknown_citation" in caplog.text and "total_tokens=1500" in caplog.text


# ---- Configuration ----


def test_a_missing_key_is_a_programming_error_for_the_adapter():
    with pytest.raises(ValueError, match="OPENAI_API_KEY"):
        OpenAIJournalAnswerer(OpenAIAnswerSettings(api_key=""))


def test_openai_is_selected_only_explicitly_and_the_default_stays_groq():
    default = journal_answerer_from_environment({"GROQ_API_KEY": "g", "OPENAI_API_KEY": "o"})
    selected = journal_answerer_from_environment(
        {"LIFEOS_AI_JOURNAL_ANSWER_PROVIDER": "openai", "GROQ_API_KEY": "g", "OPENAI_API_KEY": "o"}
    )

    assert isinstance(default, GroqJournalAnswerer)
    assert (default.provider, default.model) == ("groq", "openai/gpt-oss-20b")
    assert isinstance(selected, OpenAIJournalAnswerer)
    assert (selected.provider, selected.model, selected.prompt_version) == (
        "openai",
        "gpt-6.1-sol",
        "journal-rag-answer-v1",
    )
    asyncio.run(default.aclose())
    asyncio.run(selected.aclose())


def test_openai_without_its_key_is_unconfigured_never_a_groq_fallback():
    configured = journal_answerer_from_environment(
        {"LIFEOS_AI_JOURNAL_ANSWER_PROVIDER": "openai", "GROQ_API_KEY": "g"}
    )

    assert isinstance(configured, UnconfiguredAnswerer)
    assert (configured.provider, configured.model, configured.configured) == (
        "openai",
        "gpt-6.1-sol",
        False,
    )
    with pytest.raises(ProviderUnavailable):
        run(configured)


def test_an_unknown_answer_provider_stops_the_service():
    with pytest.raises(ProviderSelectionError, match="LIFEOS_AI_JOURNAL_ANSWER_PROVIDER"):
        journal_answerer_from_environment({"LIFEOS_AI_JOURNAL_ANSWER_PROVIDER": "anthropic"})


# ---- Privacy ----


@pytest.mark.parametrize(
    "reply",
    [
        ok(),
        ok({**ANSWERED, "citations": ["S9"]}),
        response([message({"type": "refusal", "refusal": "provider secret detail"})]),
        error(400),
        error(401, code="invalid_api_key"),
    ],
)
def test_logs_and_errors_never_carry_key_question_passages_answer_reasoning_or_ids(reply, caplog):
    openai, _, _ = answerer(reply)

    with caplog.at_level(logging.DEBUG):
        try:
            run(openai)
            failure = ""
        except (AnswerFailed, ProviderUnavailable) as exc:
            failure = str(exc)

    for text in (caplog.text, failure):
        assert KEY not in text
        assert "Con chi sono andato al mare" not in text  # question
        assert "Giulia" not in text  # passage and answer
        assert "Ignore previous instructions" not in text
        assert ANSWER_TEXT not in text
        assert "provider secret detail" not in text
        assert "resp_secret_id" not in text and "rs_x" not in text
