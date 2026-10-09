"""AI-005: the disposable pgvector database, the REAL migrations and the REAL
search_journal_memory_v1 (Docker). No provider call: vectors are fixed or scripted."""

import json
import uuid
from pathlib import Path

import pytest
from journal_fakes import EmbeddingTransport, FixedEmbedder, requires_docker, unit
from lifeos_ai.journal_memory.gemini_embeddings import (
    GeminiEmbeddingSettings,
    GeminiJournalEmbedder,
)
from live_fakes import ScriptedTransport, content, no_sleep, usage
from test_journal_drift import FUNCTION_BODY_SHA256

from lifeos_ai_evals.core.engine import evaluate
from lifeos_ai_evals.evaluators.journal_rag import plugin as rag_plugin
from lifeos_ai_evals.evaluators.journal_rag import predictor as rag_predictor
from lifeos_ai_evals.evaluators.journal_rag.model import RagQuery
from lifeos_ai_evals.evaluators.journal_retrieval import plugin as retrieval_plugin
from lifeos_ai_evals.evaluators.journal_retrieval import (
    predictor as retrieval_predictor,
)
from lifeos_ai_evals.evaluators.journal_retrieval.model import (
    RetrievalExpectation,
    RetrievalOutput,
)
from lifeos_ai_evals.evaluators.journal_retrieval.scoring import JournalRetrievalScorer
from lifeos_ai_evals.journal_memory import database as db
from lifeos_ai_evals.journal_memory.corpus import parse_corpus, resolve_evidence
from lifeos_ai_evals.journal_memory.identity import (
    DATABASE_IMAGE,
    RAG_CONTROL,
    RETRIEVAL_CONTROL,
)
from lifeos_ai_evals.journal_memory.memory_index import MemoryIndex


@pytest.fixture(scope="module")
def schema():
    requires_docker()
    return db.schema_sql()


def entry(key, user, text, lifecycle="active"):
    return {
        "key": key,
        "user": user,
        "occurred_at": "2026-01-01T10:00:00Z",
        "title": None,
        "content": text,
        "lifecycle": lifecycle,
    }


KNOWN = parse_corpus(
    "known-vectors",
    {
        "entries": [
            entry("a", "primary", "Exact vector match, no shared word."),
            entry("b", "primary", "Near vector match, no shared word either."),
            entry("c", "primary", "Orthogonal vector but the word alpha appears."),
            entry("leak", "other", "alpha alpha, exact vector, another user."),
            entry("gone", "primary", "alpha, exact vector, deleted.", "deleted"),
            entry(
                "foreign",
                "primary",
                "alpha, exact vector, foreign.",
                "foreign_identity",
            ),
        ]
    },
)
VECTORS = {
    "alpha": unit(0),
    "Exact vector match, no shared word.": unit(0),
    "Near vector match, no shared word either.": unit(0, 1, 0.5),
    "Orthogonal vector but the word alpha appears.": unit(2),
    "alpha alpha, exact vector, another user.": unit(0),
    "alpha, exact vector, deleted.": unit(0),
    "alpha, exact vector, foreign.": unit(0),
}


def test_real_migrations_and_the_migration_function(schema):
    with db.DisposableDatabase() as database:
        metadata = database.apply_schema(schema)
    assert metadata["function_verified"] is True
    assert metadata["function_body_sha256"] == FUNCTION_BODY_SHA256
    assert metadata["migrations_applied"] == len(db.migration_ids())
    assert metadata["latest_migration"] == db.JOURNAL_MEMORY_MIGRATION
    assert metadata["pgvector_version"] == "0.8.6"
    assert metadata["server_version"].startswith("18.")
    assert metadata["image"] == DATABASE_IMAGE


def test_known_vectors_are_ranked_by_the_production_function_only(schema):
    index = MemoryIndex(KNOWN, lambda: FixedEmbedder(VECTORS), schema=schema)
    try:
        index.build()
        result = index.retrieve("  alpha  ")
        assert result.question == "alpha"  # trimmed like the .NET handler
        assert [(h.entry, h.vector_rank, h.lexical_rank) for h in result.hits] == [
            ("c", 3, 1),
            ("a", 1, None),
            ("b", 2, None),
        ]
        assert [h.score for h in result.hits] == pytest.approx(
            [1 / 63 + 1 / 61, 1 / 61, 1 / 62], abs=1e-12
        )
        # Cross-user, hard-deleted and foreign-identity rows match perfectly and never
        # come back: the user, cascade and identity filters of the real schema/function.
        assert {h.entry for h in result.hits} == {"a", "b", "c"}
        assert index.database.search_calls == 1
        evidence = resolve_evidence(
            KNOWN, {"entry": "a", "quote": "Exact vector match"}
        )
        score = JournalRetrievalScorer().score(
            RetrievalExpectation(True, True, (evidence,)),
            RetrievalOutput(result.hits, 8, 0.0, 0.0, None, None),
        )
        assert score.correct
        assert score.metrics["hit@1"] == 0 and score.metrics["hit@3"] == 1
        assert score.metrics["reciprocal_rank"] == 0.5
        assert score.metrics["precision@3"] == pytest.approx(1 / 3)
        assert score.metrics["vector_only_relevant"] == 1
        for key in (
            "cross_user_results",
            "deleted_results",
            "wrong_identity_results",
            "unknown_source_results",
            "malformed_ranking",
        ):
            assert score.metrics[key] == 0
        other = index.database.search(
            KNOWN.user_id("other"), unit(0), "alpha", index.identity, 8
        )
        assert [row.entry_id for row in other] == [KNOWN.entry_id("leak")]
        chunks = index.database.connection.execute(
            "SELECT count(*) FROM journal_memory_chunks WHERE entry_id = %s",
            (KNOWN.entry_id("gone"),),
        ).fetchone()[0]
        assert chunks == 0
    finally:
        index.close()


def test_a_user_without_indexed_chunks_gets_no_rows_and_ask_skips_the_model(schema):
    corpus = parse_corpus(
        "no-rows",
        {"entries": [entry("only-foreign", "primary", "alpha", "foreign_identity")]},
    )
    calls = []
    system = rag_predictor.RagPredictor(
        name="test",
        identity={},
        index_embedder=lambda: FixedEmbedder(VECTORS),
        query_embedder=lambda: FixedEmbedder(VECTORS),
        answer=lambda request: calls.append(request),
        schema=schema,
    )
    with system:
        output = system.predict(RagQuery("alpha", corpus))
    assert calls == []
    assert output.value.hits == ()
    assert output.value.answer_called is False
    assert output.value.answer.status == "insufficient_evidence"


def test_seeding_requires_an_empty_marked_database(schema):
    with db.DisposableDatabase() as database:
        database.apply_schema(schema)
        database.insert_user(uuid.uuid4())
        with pytest.raises(db.UnsafeDatabase):
            database.assert_empty()
        database.connection.execute("UPDATE ai005_disposable_marker SET marker = 'x'")
        with pytest.raises(db.UnsafeDatabase):
            database.insert_user(uuid.uuid4())


def test_the_container_is_destroyed_on_close(schema):
    database = db.DisposableDatabase()
    database.start()
    name = database.container
    database.close()
    assert (
        db.Docker()("ps", "-a", "--filter", f"name={name}", "--format", "{{.Names}}")
        == ""
    )
    with pytest.raises(db.UnsafeDatabase):
        _ = database.connection


def test_offline_sanity_retrieval_run_is_complete_and_safe(schema):
    dataset = retrieval_plugin.load(retrieval_plugin.default_dataset())
    system = retrieval_predictor.offline_sanity(schema=schema)
    with system:
        result = evaluate(
            "journal_retrieval", dataset, system, retrieval_plugin.scorer()
        )
        metadata = system.experiment_metadata()
    run = json.loads(result.to_json())
    run["metadata"].update(metadata)
    assert run["errors"] == 0 and run["metadata"]["scored_cases"] == 40
    for key in (
        "cross_user_results",
        "deleted_results",
        "wrong_identity_results",
        "unknown_source_results",
        "malformed_ranking",
    ):
        assert run["aggregate_metrics"][key] == 0
    # V1 has no similarity threshold: every unanswerable question still gets 8 rows.
    assert run["aggregate_metrics"]["unanswerable_nonempty_rate"] == 1.0
    assert run["aggregate_metrics"]["min_result_count"] == 8
    retrieval_run = run["metadata"]["retrieval_run"]
    assert retrieval_run["search_calls"] == 40
    assert retrieval_run["indexing"]["lifecycles"] == {
        "active": 58,
        "deleted": 2,
        "foreign_identity": 1,
    }
    report = retrieval_plugin.run_acceptance(run)
    statuses = {gate["gate"]: gate["status"] for gate in report["gates"]}
    assert (
        statuses["production_control"] == "fail"
    )  # the lab embedder is not the control
    assert statuses["production_function_verified"] == "pass"
    assert statuses["zero_cross_user_results"] == "pass"


def test_live_retrieval_control_uses_the_production_adapter(schema):
    dataset = retrieval_plugin.load(retrieval_plugin.default_dataset())
    transport = EmbeddingTransport(429)
    system = retrieval_predictor.production_control(
        RETRIEVAL_CONTROL, transport=transport, sleep=no_sleep, schema=schema
    )
    subset = type(dataset)(
        dataset.name, dataset.version, dataset.cases[:2], dataset.sha256
    )
    with system:
        result = evaluate(
            "journal_retrieval", subset, system, retrieval_plugin.scorer()
        )
        metadata = system.experiment_metadata()
    assert result.errors == 0
    assert result.metadata["experiment"] == RETRIEVAL_CONTROL
    corpus = dataset.cases[0].input.corpus
    production = GeminiJournalEmbedder(GeminiEmbeddingSettings(api_key="x"))
    # 1 retried 429 + one batch per entry + one per question; bodies are the
    # production's, including its document/query input formatting.
    assert len(transport.bodies) == 1 + len(corpus.entries) + 2
    first_entry = corpus.entries[0]
    assert transport.bodies[1] == production.request_body(
        [c.embedding_input for c in corpus.chunks_of(first_entry.key)], purpose="index"
    )
    assert transport.bodies[-1] == production.request_body(
        [subset.cases[1].input.question], purpose="query"
    )
    index_summary = metadata["telemetry"]["index_embedding"]
    query_summary = metadata["telemetry"]["query_embedding"]
    assert index_summary["logical_requests"] == len(corpus.entries)
    assert index_summary["retries"] == 1
    assert query_summary["logical_requests"] == 2 and query_summary["retries"] == 0
    assert query_summary["token_usage"]["input_tokens"]["total"] == 14
    # Telemetry and run metadata carry identities, counts and timings, never content or
    # keys (the exported `expected` labels are the committed synthetic dataset itself).
    telemetry = json.dumps(metadata)
    assert (
        "camera bag" not in telemetry
        and subset.cases[0].input.question not in telemetry
    )
    assert "offline-test-key" not in telemetry + result.to_json()


def test_live_rag_control_keeps_retrieval_and_answer_telemetry_apart(schema):
    dataset = rag_plugin.load(rag_plugin.default_dataset())
    insufficient = {"status": "insufficient_evidence", "answer": "", "citations": []}
    groq = ScriptedTransport(
        *[content(insufficient, tokens=usage(500, 20)) for _ in range(2)]
    )
    system = rag_predictor.production_control(
        RAG_CONTROL,
        embedding_transport=EmbeddingTransport(),
        answer_transport=groq,
        sleep=no_sleep,
        case_sleep=lambda _: None,
        schema=schema,
    )
    subset = type(dataset)(
        dataset.name, dataset.version, dataset.cases[:2], dataset.sha256
    )
    with system:
        result = evaluate("journal_rag", subset, system, rag_plugin.scorer())
        metadata = system.experiment_metadata()
    assert result.errors == 0
    telemetry = metadata["telemetry"]
    assert telemetry["answer"]["logical_requests"] == 2
    assert telemetry["answer"]["token_usage"]["total_tokens"]["total"] == 1040
    assert telemetry["query_embedding"]["logical_requests"] == 2
    case = result.cases[0].score.metrics
    assert case["answer_total_tokens"] == 520 and case["embedding_input_tokens"] == 7
    body = groq.bodies[0]
    assert body["model"] == RAG_CONTROL["answer"]["model"]
    payload = json.loads(body["messages"][1]["content"].split("\n", 1)[1])
    assert [s["label"] for s in payload["sources"]] == [f"S{i}" for i in range(1, 9)]
    assert set(payload["sources"][0]) == {"label", "title", "occurred_at", "text"}
    corpus = dataset.cases[0].input.corpus
    for key in ("primary", "other"):
        assert str(corpus.user_id(key)) not in json.dumps(body)


# ---- safeguards (no Docker needed) ----


@pytest.mark.parametrize(
    "conninfo",
    [
        {"host": "ep-cool-123.eu-central-1.aws.neon.tech", "port": 5432},
        {"host": "db.internal", "port": 5432},
        {"host": "127.0.0.1", "port": 5433},
        {"host": "127.0.0.1", "port": 5432, "dbname": "lifeos"},
        {"host": "127.0.0.1", "port": 5432, "sslmode": "require"},
        {"host": "127.0.0.1", "port": 5432, "user": "neondb_owner"},
    ],
)
def test_guard_rejects_anything_but_the_disposable_database(conninfo):
    info = {"dbname": db.DATABASE_NAME, "user": "postgres", **conninfo}
    with pytest.raises(db.UnsafeDatabase):
        db.guard_conninfo(info, port=5432)


def test_guard_accepts_only_the_disposable_database():
    db.guard_conninfo(
        {
            "host": "127.0.0.1",
            "port": 5432,
            "dbname": db.DATABASE_NAME,
            "user": "postgres",
        },
        port=5432,
    )


def test_only_the_pinned_image_and_no_environment_connection_string(monkeypatch):
    with pytest.raises(ValueError):
        db.DisposableDatabase(image="postgres:18")
    monkeypatch.setenv("DATABASE_URL", "postgresql://user:pw@prod.example/lifeos")
    database = db.DisposableDatabase()
    with pytest.raises(db.UnsafeDatabase):
        database.conninfo()  # not started: there is no connection target at all
    source = Path(db.__file__).read_text(encoding="utf-8")
    assert "os.environ" not in source and "getenv" not in source
