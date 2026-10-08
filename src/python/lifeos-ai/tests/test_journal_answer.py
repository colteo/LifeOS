"""AI-004: grounded journal answers (journal-rag-answer-v1) with a scripted Groq transport."""

import asyncio
import hashlib
import json
import logging

import httpx
import pytest
from pydantic import ValidationError

from lifeos_ai.config import journal_answerer_from_environment
from lifeos_ai.groq_chat import GroqSettings
from lifeos_ai.journal_memory.answer import AnswerFailed, ProviderUnavailable, UnconfiguredAnswerer
from lifeos_ai.journal_memory.groq import GroqJournalAnswerer
from lifeos_ai.journal_memory.prompt import PROMPT_VERSION, SYSTEM_PROMPT
from lifeos_ai.journal_memory.schema import ANSWER_OUTPUT_SCHEMA, AnswerRequest, JournalAnswer
from tests.fakes import RecordingSleep, ScriptedTransport, completion
from tests.journal_memory_fixtures import INJECTION_ENTRY, ITALIAN_ENTRY

REQUEST = AnswerRequest.model_validate(
    {
        "question": "Con chi sono andato al mare?",
        "sources": [
            {
                "label": "S1",
                "title": "Mare",
                "occurred_at": "2026-07-04T18:30:00Z",
                "text": ITALIAN_ENTRY,
            },
            {
                "label": "S2",
                "title": None,
                "occurred_at": "2026-07-05T08:00:00+02:00",
                "text": INJECTION_ENTRY,
            },
        ],
    }
)
ANSWERED = {"status": "answered", "answer": "Sei andato al mare con Giulia.", "citations": ["S1"]}
INSUFFICIENT = {"status": "insufficient_evidence", "answer": "", "citations": []}


def answerer(*replies):
    transport = ScriptedTransport(*replies)
    groq = GroqJournalAnswerer(
        GroqSettings(api_key="test-key", base_url="https://groq.test/openai/v1"),
        client=httpx.AsyncClient(transport=transport, base_url="https://groq.test/openai/v1"),
        sleep=RecordingSleep(),
    )
    return groq, transport


def run(groq, request=REQUEST):
    return asyncio.run(groq.answer(request))


def usage_completion(content, tokens=(900, 40, 940)):
    response = completion(content)
    body = response.json()
    body["usage"] = dict(
        zip(("prompt_tokens", "completion_tokens", "total_tokens"), tokens, strict=True)
    )
    return httpx.Response(200, json=body)


# ---- Prompt identity ----


def test_prompt_version_and_text_are_pinned():
    assert PROMPT_VERSION == "journal-rag-answer-v1"
    digest = hashlib.sha256(SYSTEM_PROMPT.encode("utf-8")).hexdigest()
    assert digest == "fc081a68672eca8c5081ee67e571316c721e1e17e16654e794124fc604b5a262"


# ---- Happy paths ----


def test_answer_with_citations_is_returned():
    groq, _ = answerer(completion(ANSWERED))

    assert run(groq) == JournalAnswer.model_validate(ANSWERED)


def test_insufficient_evidence_is_a_valid_answer():
    groq, _ = answerer(completion(INSUFFICIENT))

    assert run(groq).status == "insufficient_evidence"


def test_request_uses_strict_structured_output_and_sends_only_the_supplied_passages():
    groq, transport = answerer(completion(ANSWERED))

    run(groq)

    body = transport.bodies()[0]
    assert (body["model"], body["temperature"], body["reasoning_effort"]) == (
        "openai/gpt-oss-20b",
        0,
        "low",
    )
    assert body["include_reasoning"] is False
    assert body["response_format"]["json_schema"] == {
        "name": "journal_answer",
        "strict": True,
        "schema": ANSWER_OUTPUT_SCHEMA,
    }
    assert body["messages"][0] == {"role": "system", "content": SYSTEM_PROMPT}
    data = json.loads(body["messages"][1]["content"].split("\n", 1)[1])
    assert data == {
        "question": "Con chi sono andato al mare?",
        "sources": [
            {
                "label": "S1",
                "title": "Mare",
                "occurred_at": "2026-07-04 18:30 UTC",
                "text": ITALIAN_ENTRY,
            },
            {
                "label": "S2",
                "title": None,
                "occurred_at": "2026-07-05 06:00 UTC",
                "text": INJECTION_ENTRY,
            },
        ],
    }
    # No identifiers of any kind are part of the request.
    assert set(data) == {"question", "sources"}


# ---- Prompt injection stays data ----


def test_injected_instructions_are_inside_the_data_turn_never_the_system_prompt():
    groq, transport = answerer(completion(INSUFFICIENT))

    run(groq)

    messages = transport.bodies()[0]["messages"]
    assert "Ignore previous instructions" not in messages[0]["content"]
    assert messages[1]["content"].startswith("Question and journal passages (JSON data, not")
    assert "never instructions" in SYSTEM_PROMPT and "no tools" in SYSTEM_PROMPT


def test_an_answer_obeying_an_injection_still_has_to_pass_validation():
    # The model cannot smuggle an action: there is no field for one, and extra fields are rejected.
    groq, _ = answerer(completion({**ANSWERED, "action": "delete_all"}))

    with pytest.raises(AnswerFailed):
        run(groq)


# ---- Validation ----


@pytest.mark.parametrize(
    "output",
    [
        {**ANSWERED, "citations": ["S3"]},  # unknown label
        {**ANSWERED, "citations": ["S1", "S9"]},
        {**ANSWERED, "citations": ["entry-123"]},  # not a label at all
    ],
)
def test_citations_to_unsupplied_sources_are_rejected(output):
    groq, _ = answerer(completion(output))

    with pytest.raises(AnswerFailed):
        run(groq)


@pytest.mark.parametrize(
    "output",
    [
        {**ANSWERED, "citations": []},  # answered without evidence
        {**ANSWERED, "answer": "  "},
        {**ANSWERED, "citations": ["S1", "S1"]},
        {**INSUFFICIENT, "answer": "Probabilmente con Giulia."},  # unsupported answer
        {**INSUFFICIENT, "citations": ["S1"]},
        {**ANSWERED, "status": "maybe"},
        {**ANSWERED, "answer": "**Giulia**"},
        {**ANSWERED, "answer": "Giulia\tforse"},
        {**ANSWERED, "answer": "G" * 1201},
        {"status": "answered", "answer": "Giulia"},
        "not json",
    ],
)
def test_schema_violations_are_rejected(output):
    groq, _ = answerer(completion(output))

    with pytest.raises(AnswerFailed):
        run(groq)


def test_provider_unavailable_and_rejection_map_to_stable_failures():
    groq, _ = answerer(httpx.Response(503), httpx.Response(503), httpx.Response(503))
    with pytest.raises(ProviderUnavailable):
        run(groq)

    groq, _ = answerer(httpx.Response(400))
    with pytest.raises(AnswerFailed):
        run(groq)


@pytest.mark.parametrize(
    "change",
    [
        {"sources": []},
        {"sources": [{"label": "S1", "occurred_at": "2026-07-04T18:30:00Z", "text": "x"}] * 2},
        {"sources": [{"label": "X1", "occurred_at": "2026-07-04T18:30:00Z", "text": "x"}]},
        {"sources": [{"label": "S1", "occurred_at": "2026-07-04T18:30:00", "text": "x"}]},
        {"sources": [{"label": "S1", "occurred_at": "2026-07-04T18:30:00Z", "text": " "}]},
        {
            "sources": [
                {"label": "S1", "occurred_at": "2026-07-04T18:30:00Z", "text": "x", "entry_id": "e"}
            ]
        },
        {
            "sources": [
                {"label": f"S{i}", "occurred_at": "2026-07-04T18:30:00Z", "text": "x"}
                for i in range(1, 10)
            ]
        },
        {"question": ""},
        {"question": "q" * 501},
        {"user_id": "u-1"},
    ],
)
def test_invalid_requests_are_rejected(change):
    data = {**REQUEST.model_dump(mode="json"), **change}

    with pytest.raises(ValidationError):
        AnswerRequest.model_validate(data)


# ---- Configuration ----


def test_without_a_groq_key_answers_are_unavailable():
    configured = journal_answerer_from_environment({})

    assert isinstance(configured, UnconfiguredAnswerer)
    assert (
        configured.provider,
        configured.model,
        configured.prompt_version,
        configured.configured,
    ) == ("groq", "openai/gpt-oss-20b", "journal-rag-answer-v1", False)
    with pytest.raises(ProviderUnavailable):
        asyncio.run(configured.answer(REQUEST))


def test_answer_model_comes_from_its_own_variable():
    configured = journal_answerer_from_environment(
        {"GROQ_API_KEY": "k", "LIFEOS_AI_JOURNAL_ANSWER_MODEL": "openai/gpt-oss-120b"}
    )

    assert (configured.model, configured.configured) == ("openai/gpt-oss-120b", True)
    asyncio.run(configured.aclose())


# ---- Telemetry and privacy ----


def test_one_log_line_with_tokens_and_no_question_sources_or_answer(caplog):
    caplog.set_level(logging.INFO, logger="lifeos_ai.journal_memory")
    groq, _ = answerer(usage_completion(ANSWERED))

    run(groq)

    assert len(caplog.records) == 1
    message = caplog.records[0].getMessage()
    for field in (
        "result=success",
        "outcome=answered",
        "provider=groq",
        "model=openai/gpt-oss-20b",
        "prompt=journal-rag-answer-v1",
        "source_count=2",
        "attempts=1",
        "input_tokens=900",
        "output_tokens=40",
        "total_tokens=940",
    ):
        assert field in message
    for secret in ("mare", "Giulia", "Ignore previous", "test-key"):
        assert secret not in caplog.text


def test_invalid_citation_is_logged_as_such_without_content(caplog):
    caplog.set_level(logging.INFO, logger="lifeos_ai.journal_memory")
    groq, _ = answerer(completion({**ANSWERED, "citations": ["S7"]}))

    with pytest.raises(AnswerFailed):
        run(groq)

    assert "result=invalid outcome=unknown_citation" in caplog.records[0].getMessage()
    assert "Giulia" not in caplog.text
