"""AI-005 production drift: the lab's pinned identities and its one mirror (the Ask
handler) must match the AI-004 production sources. A failure means production changed:
AI-005's frozen control no longer describes it and must be revisited deliberately (never
silently)."""

import asyncio
import hashlib
import json
import re
from pathlib import Path

import pytest
from journal_fakes import EmbeddingTransport
from lifeos_ai.groq_chat import GroqSettings
from lifeos_ai.journal_memory.chunking import CHUNKING_VERSION
from lifeos_ai.journal_memory.gemini_embeddings import (
    DEFAULT_EMBEDDING_MODEL,
    DOCUMENT_FORMAT,
    EMBEDDING_DIMENSIONS,
    QUERY_FORMAT,
    GeminiEmbeddingSettings,
    GeminiJournalEmbedder,
)
from lifeos_ai.journal_memory.groq import GroqJournalAnswerer
from lifeos_ai.journal_memory.indexing import embed_query, index_entry
from lifeos_ai.journal_memory.prompt import PROMPT_VERSION, SYSTEM_PROMPT
from lifeos_ai.journal_memory.schema import (
    AnswerRequest,
    EmbedQueryRequest,
    IndexEntryRequest,
)

from lifeos_ai_evals import __main__ as cli
from lifeos_ai_evals.evaluators.journal_answer import plugin as answer_plugin
from lifeos_ai_evals.evaluators.journal_answer.baseline import ExtractiveBaseline
from lifeos_ai_evals.journal_memory import ask_mirror, database, embedders
from lifeos_ai_evals.journal_memory.identity import (
    ANSWER_CONTROL,
    ANSWER_GENERATION,
    DATABASE_IMAGE,
    REPOSITORY_ROOT,
    RETRIEVAL,
    RETRIEVAL_CONTROL,
)
from lifeos_ai_evals.production import runtime

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


# AI-005.1: the only lab modules allowed to name the retrieval-v2 candidate. The answer
# prompt v2 (AI-005.2) does not exist anywhere.
RETRIEVAL_V2_MODULES = {
    "evaluators/journal_retrieval/plugin.py",
    "evaluators/journal_retrieval/predictor.py",
    "evaluators/journal_retrieval/promotion.py",
    "journal_memory/candidate.py",
    "journal_memory/database.py",
}


def test_retrieval_v2_is_named_only_by_the_ai005_1_modules():
    root = Path(cli.__file__).parent
    for path in root.rglob("*.py"):
        text = path.read_text(encoding="utf-8")
        relative = path.relative_to(root).as_posix()
        assert "journal-rag-answer-v2" not in text
        if relative not in RETRIEVAL_V2_MODULES:
            assert "search_journal_memory_v2" not in text, relative
            assert "journal-retrieval-v2" not in text, relative


# ---- Gemini embedding control (production adapter, no lab copy) ----


def test_the_embedding_control_is_the_production_gemini_identity():
    embedding = RETRIEVAL_CONTROL["embedding"]
    assert embedding == {
        "provider": GeminiJournalEmbedder.provider,
        "model": DEFAULT_EMBEDDING_MODEL,
        "dimensions": EMBEDDING_DIMENSIONS,
    }
    assert embedding == {
        "provider": "google",
        "model": "gemini-embedding-2",
        "dimensions": 1536,
    }


def test_the_live_embedder_is_the_production_adapter_with_its_formatting(monkeypatch):
    monkeypatch.delenv("GEMINI_API_KEY", raising=False)
    with pytest.raises(ValueError, match="GEMINI_API_KEY"):
        embedders.gemini_key_from_environment()

    transport = EmbeddingTransport()
    telemetry = runtime.Telemetry()
    model = RETRIEVAL_CONTROL["embedding"]["model"]
    embedder = embedders.live_embedder("offline-test-key", model, telemetry, transport)
    assert type(embedder) is GeminiJournalEmbedder
    production = GeminiJournalEmbedder(GeminiEmbeddingSettings(api_key="x"))

    async def run():
        indexed = await index_entry(
            embedder, IndexEntryRequest(title="Mare", content="Una giornata al mare.")
        )
        await embed_query(embedder, EmbedQueryRequest(question="Dove sono andato?"))
        await embedder.aclose()
        return indexed

    indexed = asyncio.run(run())
    # Lab telemetry reads the same token count the production adapter reports.
    assert [attempt.usage["input_tokens"] for attempt in telemetry.attempts] == [7, 7]
    assert embedders.gemini_usage({"embeddings": []}) is None
    assert embedders.gemini_usage({"usageMetadata": {"promptTokenCount": True}}) is None
    # The production indexing path passes the purpose; the production adapter formats.
    assert transport.bodies == [
        production.request_body(["Mare\n\nUna giornata al mare."], purpose="index"),
        production.request_body(["Dove sono andato?"], purpose="query"),
    ]
    assert (indexed.embedding_provider, indexed.embedding_model) == ("google", model)
    assert indexed.embedding_dimensions == 1536


def test_the_lab_never_recreates_provider_requests_or_formatting():
    root = Path(cli.__file__).parent
    markers = (
        QUERY_FORMAT.split("{")[0],
        DOCUMENT_FORMAT.split("{")[0],
        "batchEmbedContents",
        "x-goog-api-key",
        "generativelanguage",
        "outputDimensionality",
        "OpenAIJournalEmbedder",
        "openai_embeddings",
        "OPENAI_API_KEY",
        "text-embedding-3-small",
    )
    for path in root.rglob("*.py"):
        text = path.read_text(encoding="utf-8")
        for marker in markers:
            assert marker not in text, (path, marker)


# ---- AI-005.1: journal-retrieval-v2 candidate (additive, never the default) ----

V2_FUNCTION_BODY_SHA256 = (
    "51ad82409a814222b41a7eeb991b5a3cde2766153efcaa7167a75cbf927b5fd5"
)
V1_LEXICAL_QUERY = (
    "-- OR of the question's lexemes, built from tsquery's own output form (quoting "
    "safe). SELECT string_agg(plainto_tsquery('simple'::regconfig, lexeme)::text, "
    "' | ')::tsquery INTO v_query FROM unnest(tsvector_to_array(to_tsvector('simple'::"
    "regconfig, coalesce(p_query_text, '')))) AS lexeme;"
)
V2_STOP_WORD_FILTER = (
    "WHERE cardinality(ts_lexize('english_stem'::regdictionary, lexeme)) IS DISTINCT "
    "FROM 0 AND cardinality(ts_lexize('italian_stem'::regdictionary, lexeme)) IS "
    "DISTINCT FROM 0;"
)


def candidate_migration() -> str:
    path = database.MIGRATIONS_DIR / f"{database.CANDIDATE_MIGRATION}.cs"
    return path.read_text(encoding="utf-8-sig").replace("\r\n", "\n")


def test_v1_is_unchanged_and_stays_the_production_default():
    # The v1 body hash is AI-005's; the .NET policy and store still name v1 only.
    assert database.function_body_sha256(database.migration_function_body()) == (
        FUNCTION_BODY_SHA256
    )
    policy = source("LifeOS.Application/Memory/JournalMemoryPolicy.cs")
    assert constant(policy, "RetrievalVersion") == "journal-retrieval-v1"
    assert constant(policy, "RetrievalFunction") == "search_journal_memory_v1"
    for path in DOTNET.rglob("*.cs"):
        if "Migrations" in path.parts:
            continue
        text = path.read_text(encoding="utf-8")
        assert "search_journal_memory_v2" not in text, path
        assert "journal-retrieval-v2" not in text, path
    # The journal_rag control and the default database still rank with v1.
    assert (
        database.DisposableDatabase().function
        == database.FUNCTION
        == (RETRIEVAL["function"])
    )


def test_v2_body_is_v1_with_only_the_lexical_query_changed():
    v1 = database.migration_function_body()
    v2 = database.migration_function_body(function=database.CANDIDATE_FUNCTION)
    assert database.function_body_sha256(v2) == V2_FUNCTION_BODY_SHA256
    assert V1_LEXICAL_QUERY in v1 and v2.count(V2_STOP_WORD_FILTER) == 1
    start = v2.index("-- journal-retrieval-v2:")
    end = v2.index(V2_STOP_WORD_FILTER) + len(V2_STOP_WORD_FILTER)
    statement = v2[start:end]
    # Same statement as v1, plus the stop-word filter (and its comment).
    assert statement.endswith(
        V1_LEXICAL_QUERY.split(" SELECT ", 1)[1].removesuffix(";")
        + " "
        + V2_STOP_WORD_FILTER
    )
    restored = v2[:start] + V1_LEXICAL_QUERY + v2[end:]
    assert restored.replace("search_journal_memory_v2", "search_journal_memory_v1") == (
        v1
    )


def test_v2_migration_is_additive_and_down_removes_only_v2():
    text = candidate_migration()
    up = text[text.index("void Up(") : text.index("void Down(")]
    down = text[text.index("void Down(") :]
    assert up.count("CREATE FUNCTION ") == 1
    assert "CREATE FUNCTION search_journal_memory_v2(" in up
    assert "COMMENT ON FUNCTION search_journal_memory_v2(" in up
    for forbidden in (
        "CREATE TABLE",
        "ALTER TABLE",
        "CREATE INDEX",
        "DROP ",
        "INSERT ",
        "UPDATE ",
        "DELETE ",
        "search_journal_memory_v1",
        "vector(1536)",
    ):
        assert forbidden not in up, forbidden
    statements = [
        line.strip()
        for line in down.splitlines()
        if line.strip().startswith(("DROP", "CREATE", "ALTER"))
    ]
    assert statements == [
        "DROP FUNCTION search_journal_memory_v2"
        "(uuid, vector, text, text, text, text, integer);"
    ]
    assert database.migration_ids()[-1] == database.CANDIDATE_MIGRATION
    designer = (
        database.MIGRATIONS_DIR / f"{database.CANDIDATE_MIGRATION}.Designer.cs"
    ).read_text(encoding="utf-8")
    previous = (
        database.MIGRATIONS_DIR / f"{database.JOURNAL_MEMORY_MIGRATION}.Designer.cs"
    ).read_text(encoding="utf-8")

    def model(text):
        return text[text.index("BuildTargetModel") :].replace("\r\n", "\n")

    assert model(designer) == model(previous)  # no EF model change


def test_the_candidate_differs_from_the_control_only_by_retrieval_policy(monkeypatch):
    # Building a live system makes no call; the key only has to be present.
    monkeypatch.setenv("GEMINI_API_KEY", "offline-test-key")
    from lifeos_ai_evals.evaluators.journal_retrieval import (
        plugin,
        predictor,
        promotion,
    )
    from lifeos_ai_evals.journal_memory.candidate import (
        RETRIEVAL_CANDIDATE,
        RETRIEVAL_V2,
    )

    assert promotion.candidate_identity_problems(RETRIEVAL_CANDIDATE) == []
    assert {
        key: value for key, value in RETRIEVAL_CANDIDATE.items() if key != "retrieval"
    } == {
        key: value
        for key, value in {
            **RETRIEVAL_CONTROL,
            "system": "journal-memory-retrieval-v2-candidate",
        }.items()
        if key != "retrieval"
    }
    assert RETRIEVAL_CANDIDATE["embedding"] == {
        "provider": "google",
        "model": "gemini-embedding-2",
        "dimensions": 1536,
    }
    assert RETRIEVAL_CANDIDATE["chunking_version"] == "journal-chunking-v1"
    assert {
        key: value
        for key, value in RETRIEVAL_V2.items()
        if key not in ("version", "function", "lexical_query")
    } == {
        key: value
        for key, value in RETRIEVAL.items()
        if key not in ("version", "function")
    }
    assert (RETRIEVAL_V2["version"], RETRIEVAL_V2["function"]) == (
        promotion.CANDIDATE_RETRIEVAL_VERSION,
        promotion.CANDIDATE_FUNCTION,
    )
    experiments = REPOSITORY_ROOT / "tools/ai-evals/experiments/journal_retrieval"
    candidate = json.loads(
        (experiments / "candidate-v2.json").read_text(encoding="utf-8")
    )
    assert candidate == RETRIEVAL_CANDIDATE
    system = plugin.experiment(experiments / "candidate-v2.json")
    assert isinstance(system, predictor.RetrievalPredictor)
    assert system.identity == RETRIEVAL_CANDIDATE
    assert system.name == "journal-retrieval-v2-candidate"
    assert system._database_factory().function == "search_journal_memory_v2"
    control = plugin.experiment(experiments / "control-v1.json")
    assert control.identity == RETRIEVAL_CONTROL
    assert control._database_factory().function == "search_journal_memory_v1"
