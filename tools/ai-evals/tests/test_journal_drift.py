"""AI-005 production drift: the lab's pinned identities and its one mirror (the Ask
handler) must match the AI-004 production sources. A failure means production changed:
AI-005's frozen control no longer describes it and must be revisited deliberately (never
silently)."""

import hashlib
import json
import re
from pathlib import Path

from lifeos_ai.groq_chat import GroqSettings
from lifeos_ai.journal_memory.chunking import CHUNKING_VERSION
from lifeos_ai.journal_memory.groq import GroqJournalAnswerer
from lifeos_ai.journal_memory.prompt import PROMPT_VERSION, SYSTEM_PROMPT
from lifeos_ai.journal_memory.schema import AnswerRequest

from lifeos_ai_evals import __main__ as cli
from lifeos_ai_evals.evaluators.journal_answer import plugin as answer_plugin
from lifeos_ai_evals.evaluators.journal_answer.baseline import ExtractiveBaseline
from lifeos_ai_evals.journal_memory import ask_mirror, database
from lifeos_ai_evals.journal_memory.identity import (
    ANSWER_CONTROL,
    ANSWER_GENERATION,
    DATABASE_IMAGE,
    REPOSITORY_ROOT,
    RETRIEVAL,
    RETRIEVAL_CONTROL,
)

DOTNET = REPOSITORY_ROOT / "src/dotnet"
FUNCTION_BODY_SHA256 = (
    "74e5e4e79588572ebfadae2d4fc3bdafba0364ff4267850e35a40c0b245d4870"
)
PROMPT_SHA256 = "fc081a68672eca8c5081ee67e571316c721e1e17e16654e794124fc604b5a262"


def source(relative: str) -> str:
    return (DOTNET / relative).read_text(encoding="utf-8")


def constant(text: str, name: str) -> str:
    match = re.search(rf"public const \w+ {name} = (.+?);", text)
    assert match, name
    return match.group(1).strip('"')


def test_dotnet_policy_is_the_frozen_control():
    policy = source("LifeOS.Application/Memory/JournalMemoryPolicy.cs")
    embedding = RETRIEVAL_CONTROL["embedding"]
    assert constant(policy, "ChunkingVersion") == RETRIEVAL_CONTROL["chunking_version"]
    assert constant(policy, "EmbeddingProvider") == embedding["provider"]
    assert constant(policy, "EmbeddingModel") == embedding["model"]
    assert int(constant(policy, "EmbeddingDimensions")) == embedding["dimensions"]
    assert constant(policy, "RetrievalVersion") == RETRIEVAL["version"]
    assert constant(policy, "RetrievalFunction") == RETRIEVAL["function"]
    assert int(constant(policy, "AnswerContextChunks")) == RETRIEVAL["limit"]
    assert ask_mirror.ANSWER_CONTEXT_CHUNKS == RETRIEVAL["limit"]
    assert int(constant(policy, "DefaultSearchLimit")) == RETRIEVAL["limit"]


def test_python_production_identity_is_the_frozen_control():
    assert CHUNKING_VERSION == RETRIEVAL_CONTROL["chunking_version"]
    assert PROMPT_VERSION == ANSWER_CONTROL["prompt_version"]
    assert hashlib.sha256(SYSTEM_PROMPT.encode("utf-8")).hexdigest() == PROMPT_SHA256
    body = GroqJournalAnswerer(GroqSettings(api_key="x")).request_body(
        AnswerRequest(
            question="q",
            sources=[
                {"label": "S1", "occurred_at": "2026-01-01T00:00:00Z", "text": "t"}
            ],
        )
    )
    assert body["model"] == ANSWER_CONTROL["model"]
    assert {key: body[key] for key in ANSWER_GENERATION} == ANSWER_GENERATION


def test_the_migration_function_is_frozen_with_its_documented_policy():
    body = database.migration_function_body()
    assert database.function_body_sha256(body) == FUNCTION_BODY_SHA256
    assert body.count("LIMIT 20") == 2  # vector and lexical candidate depths
    assert RETRIEVAL["vector_candidates"] == RETRIEVAL["lexical_candidates"] == 20
    assert "1.0::double precision / (60 + v.hit_rank)" in body
    assert "1.0::double precision / (60 + l.hit_rank)" in body
    assert RETRIEVAL["rrf_k"] == 60
    assert "p_limit < 1 OR p_limit > 40" in body
    # No similarity floor in V1: candidates are never filtered by distance or score.
    assert RETRIEVAL["similarity_threshold"] is None
    assert not re.search(r"distance\s*[<>]|score\s*[<>]", body)
    assert "to_tsvector('simple'::regconfig" in body


def test_the_lab_search_statement_is_the_dotnet_store_statement():
    store = source("LifeOS.Infrastructure/Memory/JournalMemoryStore.cs")
    search = store[store.index("SearchAsync") :]
    search = search[: search.index("ORDER BY s.retrieval_rank")]
    dotnet_columns = re.findall(r"s\.(\w+) AS", search)
    lab_columns = re.findall(r"s\.(\w+)", database.SEARCH_SQL.split("FROM")[0])
    assert dotnet_columns == lab_columns
    assert "FROM search_journal_memory_v1(" in search
    assert database.SEARCH_SQL.count("search_journal_memory_v1(") == 1
    assert "ORDER BY s.retrieval_rank" in database.SEARCH_SQL


def test_the_ask_mirror_matches_the_dotnet_handler():
    handlers = source("LifeOS.Application/Memory/JournalMemoryHandlers.cs")
    ask = handlers[handlers.index("class AskJournalMemoryHandler") :]
    assert "JournalMemoryPolicy.AnswerContextChunks" in ask
    assert '$"S{index + 1}"' in ask and ask_mirror.label(0) == "S1"
    assert (
        'new JournalAnswerSource($"S{index + 1}", hit.Title, hit.OccurredAtUtc, '
        "hit.ChunkText)" in ask
    )
    # Zero hits -> insufficient evidence BEFORE the answer model is called.
    assert ask.index("if (hits.Count == 0)") < ask.index("answers.AnswerAsync(text,")
    retrieval = handlers[handlers.index("class JournalMemoryRetrieval") :]
    assert "embeddings.EmbedQueryAsync(question" in retrieval
    assert "store.SearchAsync(userId, embedding.Embedding, question" in retrieval
    assert "var text = question?.Trim();" in handlers


def test_the_database_image_is_the_ai004_test_image():
    fixture = (
        REPOSITORY_ROOT
        / "tests/dotnet/LifeOS.IntegrationTests/PostgreSql/PostgreSqlFixture.cs"
    ).read_text(encoding="utf-8")
    assert f'DefaultImage = "{DATABASE_IMAGE}"' in fixture
    compose = (REPOSITORY_ROOT / "docker-compose.yml").read_text(encoding="utf-8")
    assert f"image: {DATABASE_IMAGE}" in compose


def test_the_cli_attaches_absolute_gates_and_closes_systems(tmp_path, monkeypatch):
    closed = []

    class Closing(ExtractiveBaseline):
        def close(self):
            closed.append(True)

    monkeypatch.setattr(answer_plugin, "system", lambda name=None: Closing())
    output = tmp_path / "run.json"
    assert cli.main(["evaluate", "journal_answer", "--output", str(output)]) == 0
    run = json.loads(output.read_text(encoding="utf-8"))
    assert closed == [True]
    acceptance = run["metadata"]["acceptance"]
    assert acceptance["version"] == "journal-answer-acceptance-v1"
    assert acceptance["all_gates_pass"] is False  # the baseline is not the control


def test_no_threshold_or_candidate_exists_in_the_lab():
    root = Path(cli.__file__).parent
    for path in root.rglob("*.py"):
        text = path.read_text(encoding="utf-8")
        assert "search_journal_memory_v2" not in text
        assert "journal-rag-answer-v2" not in text
        assert "journal-retrieval-v2" not in text
