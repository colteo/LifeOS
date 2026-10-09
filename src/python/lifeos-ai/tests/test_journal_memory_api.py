"""AI-004: /v1/journal-memory/* through the real FastAPI app (fake embedder and answerer)."""

import logging

import pytest
from fastapi.testclient import TestClient

from lifeos_ai.app import create_app
from lifeos_ai.config import journal_answerer_from_environment, journal_embedder_from_environment
from lifeos_ai.journal_memory.answer import AnswerFailed, ProviderUnavailable
from lifeos_ai.journal_memory.chunking import chunk_entry
from lifeos_ai.journal_memory.embedding import EmbeddingFailed, UnconfiguredEmbedder
from lifeos_ai.journal_memory.schema import AnswerRequest, JournalAnswer
from tests.journal_memory_fixtures import (
    INJECTION_ENTRY,
    ITALIAN_ENTRY,
    FakeAnswerer,
    FakeEmbedder,
    vector,
)
from tests.test_action_agent_api import FakeAgent
from tests.test_api import SERVICE_KEY, FakeEstimator
from tests.test_weekly_review_api import FakeInterpreter

INDEX = "/v1/journal-memory/index-entry"
QUERY = "/v1/journal-memory/embed-query"
ANSWER = "/v1/journal-memory/answer"
SOURCES = [
    {"label": "S1", "title": "Mare", "occurred_at": "2026-07-04T18:30:00Z", "text": ITALIAN_ENTRY},
    {"label": "S2", "title": None, "occurred_at": "2026-07-05T08:00:00Z", "text": INJECTION_ENTRY},
]


def client(embedder=None, answerer=None, *, authorized=True):
    headers = {"Authorization": f"Bearer {SERVICE_KEY}"} if authorized else {}
    return TestClient(
        create_app(
            FakeEstimator(),
            interpreter=FakeInterpreter(),
            agent=FakeAgent(),
            embedder=embedder or FakeEmbedder(),
            answerer=answerer or FakeAnswerer(),
            service_key=SERVICE_KEY,
        ),
        headers=headers,
    )


# ---- Authentication ----


@pytest.mark.parametrize(
    ("path", "body"),
    [
        (INDEX, {"content": "x"}),
        (QUERY, {"question": "x"}),
        (ANSWER, {"question": "x", "sources": SOURCES}),
    ],
)
def test_every_journal_route_requires_the_service_key(path, body):
    embedder, answerer = FakeEmbedder(), FakeAnswerer()

    with client(embedder, answerer, authorized=False) as http:
        response = http.post(path, json=body)
        wrong = http.post(
            path,
            json=body,
            headers={"Authorization": "Bearer wrong-key-00000000000000000000000000000000"},
        )

    assert response.status_code == wrong.status_code == 401
    assert response.json() == {"error": {"code": "unauthorized"}}
    assert embedder.calls == [] and answerer.requests == []


# ---- index-entry ----


def test_index_entry_returns_the_production_chunks_with_identity_and_vectors():
    embedder = FakeEmbedder()

    with client(embedder) as http:
        response = http.post(INDEX, json={"title": " Mare ", "content": ITALIAN_ENTRY})

    assert response.status_code == 200
    body = response.json()
    assert {key: body[key] for key in body if key != "chunks"} == {
        "output_version": 1,
        "chunking_version": "journal-chunking-v1",
        "embedding_provider": "google",
        "embedding_model": "gemini-embedding-2",
        "embedding_dimensions": 1536,
    }
    assert body["chunks"] == [{"ordinal": 0, "text": ITALIAN_ENTRY, "embedding": vector(0)}]
    # The embedding input carries the title; the returned text is the journal text only.
    assert embedder.calls == [([f"Mare\n\n{ITALIAN_ENTRY}"], "index")]


def test_a_long_entry_is_embedded_in_one_batch_in_chunk_order():
    content = ("Una frase del diario abbastanza lunga da contare. " * 120).strip()
    embedder = FakeEmbedder()

    with client(embedder) as http:
        body = http.post(INDEX, json={"content": content}).json()

    expected = chunk_entry(None, content)
    assert len(expected) > 1
    assert [chunk["text"] for chunk in body["chunks"]] == [chunk.text for chunk in expected]
    assert [chunk["ordinal"] for chunk in body["chunks"]] == list(range(len(expected)))
    assert embedder.calls == [([chunk.embedding_input for chunk in expected], "index")]


@pytest.mark.parametrize(
    "body",
    [
        {},
        {"content": "   "},
        {"content": "x" * 20_001},
        {"title": "t" * 201, "content": "x"},
        {"content": "x", "entry_id": "3f1c"},
        {"content": "x", "user_id": "u-1"},
    ],
)
def test_invalid_index_requests_are_422_without_echoing_journal_text(body):
    embedder = FakeEmbedder()

    with client(embedder) as http:
        response = http.post(INDEX, json=body)

    assert response.status_code == 422
    assert response.json()["error"]["code"] == "invalid_request"
    assert "xxxx" not in response.text and "ttt" not in response.text
    assert embedder.calls == []


def test_unconfigured_embeddings_are_503_provider_unavailable():
    unconfigured = UnconfiguredEmbedder(
        provider="google", model="gemini-embedding-2", dimensions=1536
    )

    with client(unconfigured) as http:
        index = http.post(INDEX, json={"content": ITALIAN_ENTRY})
        query = http.post(QUERY, json={"question": "Dove?"})

    assert index.status_code == query.status_code == 503
    assert index.json() == query.json() == {"error": {"code": "provider_unavailable"}}


def test_invalid_embeddings_are_502_and_wrong_dimensions_never_leave_the_service():
    with client(FakeEmbedder(EmbeddingFailed("provider said: detail"))) as http:
        failed = http.post(INDEX, json={"content": ITALIAN_ENTRY})
    with client(FakeEmbedder(vectors=[vector(0, 1535)])) as http:
        short = http.post(INDEX, json={"content": ITALIAN_ENTRY})
    with client(FakeEmbedder(vectors=[[float("nan")] * 1536])) as http:
        nan = http.post(QUERY, json={"question": "Dove?"})

    for response in (failed, short, nan):
        assert response.status_code == 502
        assert response.json() == {"error": {"code": "embedding_failed"}}
    assert "detail" not in failed.text


# ---- embed-query ----


def test_embed_query_returns_one_vector_with_identity():
    embedder = FakeEmbedder()

    with client(embedder) as http:
        response = http.post(QUERY, json={"question": "  Con chi sono andato al mare? "})

    assert response.status_code == 200
    assert response.json() == {
        "output_version": 1,
        "provider": "google",
        "model": "gemini-embedding-2",
        "dimensions": 1536,
        "embedding": vector(0),
    }
    assert embedder.calls == [(["Con chi sono andato al mare?"], "query")]


def test_invalid_questions_are_422_without_echo():
    with client() as http:
        long = http.post(QUERY, json={"question": "segreto " * 100})
        blank = http.post(QUERY, json={"question": " "})

    assert long.status_code == blank.status_code == 422
    assert "segreto" not in long.text


# ---- answer ----


def test_answer_returns_the_versioned_attributed_result():
    answerer = FakeAnswerer()

    with client(answerer=answerer) as http:
        response = http.post(ANSWER, json={"question": "Con chi?", "sources": SOURCES})

    assert response.status_code == 200
    assert response.json() == {
        "output_version": 1,
        "provider": "groq",
        "model": "openai/gpt-oss-20b",
        "prompt_version": "journal-rag-answer-v1",
        "result": {
            "status": "answered",
            "answer": "You went to the sea with Giulia.",
            "citations": ["S1"],
        },
    }
    assert answerer.requests == [
        AnswerRequest.model_validate({"question": "Con chi?", "sources": SOURCES})
    ]


def test_insufficient_evidence_passes_through():
    answerer = FakeAnswerer(JournalAnswer(status="insufficient_evidence", answer="", citations=[]))

    with client(answerer=answerer) as http:
        response = http.post(ANSWER, json={"question": "Quanto pesavo?", "sources": SOURCES})

    assert response.json()["result"] == {
        "status": "insufficient_evidence",
        "answer": "",
        "citations": [],
    }


@pytest.mark.parametrize(
    ("failure", "status", "code"),
    [
        (ProviderUnavailable("groq: detail"), 503, "provider_unavailable"),
        (AnswerFailed("model said: detail"), 502, "answer_failed"),
        (RuntimeError("stack detail"), 500, "internal_error"),
    ],
)
def test_answer_failures_are_stable_codes(failure, status, code):
    with client(answerer=FakeAnswerer(failure)) as http:
        response = http.post(ANSWER, json={"question": "Con chi?", "sources": SOURCES})

    assert response.status_code == status
    assert response.json() == {"error": {"code": code}}
    assert "detail" not in response.text


def test_invalid_answer_requests_are_422_naming_fields_only():
    with client() as http:
        response = http.post(
            ANSWER,
            json={
                "question": "Con chi?",
                "sources": [{**SOURCES[0], "entry_id": "secret-id"}],
            },
        )

    assert response.status_code == 422
    assert ITALIAN_ENTRY[:20] not in response.text and "secret-id" not in response.text


def test_unexpected_failures_log_no_journal_text(caplog):
    caplog.set_level(logging.INFO)

    with client(FakeEmbedder(RuntimeError("boom"))) as http:
        response = http.post(INDEX, json={"content": ITALIAN_ENTRY})

    assert response.status_code == 500
    assert "Giulia" not in caplog.text


# ---- health ----


def test_health_reports_journal_identities_without_secrets(monkeypatch):
    monkeypatch.setenv("GEMINI_API_KEY", "secret-gemini-key")
    monkeypatch.setenv("GROQ_API_KEY", "secret-groq-key")
    with TestClient(
        create_app(
            FakeEstimator(),
            interpreter=FakeInterpreter(),
            agent=FakeAgent(),
            embedder=journal_embedder_from_environment(),
            answerer=journal_answerer_from_environment(),
            service_key=SERVICE_KEY,
        ),
        headers={"Authorization": f"Bearer {SERVICE_KEY}"},
    ) as http:
        response = http.get("/health")

    body = response.json()
    assert body["journal_embedding"] == {
        "provider": "google",
        "model": "gemini-embedding-2",
        "dimensions": 1536,
        "chunking_version": "journal-chunking-v1",
        "configured": True,
    }
    assert body["journal_answer"] == {
        "provider": "groq",
        "model": "openai/gpt-oss-20b",
        "prompt_version": "journal-rag-answer-v1",
        "configured": True,
    }
    assert "secret-gemini-key" not in response.text and "secret-groq-key" not in response.text


def test_the_service_starts_without_a_gemini_key_and_reports_it_unconfigured(monkeypatch):
    monkeypatch.delenv("GEMINI_API_KEY", raising=False)
    # An OpenAI key no longer configures journal embeddings: one provider, no fallback.
    monkeypatch.setenv("OPENAI_API_KEY", "not-the-journal-key")

    with TestClient(
        create_app(FakeEstimator(), service_key=SERVICE_KEY),
        headers={"Authorization": f"Bearer {SERVICE_KEY}"},
    ) as http:
        body = http.get("/health").json()
        index = http.post(INDEX, json={"content": ITALIAN_ENTRY})

    assert body["journal_embedding"]["configured"] is False
    assert body["journal_embedding"]["provider"] == "google"
    assert index.status_code == 503
