"""AI-005.1: the journal-retrieval-v2 candidate ranked by the REAL migration function
`search_journal_memory_v2` on a disposable pgvector database (Docker). Deterministic
synthetic vectors only; no provider call. The lab never ranks: every expectation below
is what the SQL function returns."""

import json
from functools import partial

import pytest
from journal_fakes import EmbeddingTransport, FixedEmbedder, requires_docker, unit
from live_fakes import no_sleep
from test_journal_drift import FUNCTION_BODY_SHA256, V2_FUNCTION_BODY_SHA256

from lifeos_ai_evals.core.engine import evaluate
from lifeos_ai_evals.evaluators.journal_retrieval import plugin
from lifeos_ai_evals.evaluators.journal_retrieval import predictor as retrieval
from lifeos_ai_evals.evaluators.journal_retrieval.model import RetrievalOutput
from lifeos_ai_evals.evaluators.journal_retrieval.scoring import ranking_violations
from lifeos_ai_evals.journal_memory import database as db
from lifeos_ai_evals.journal_memory import embedders
from lifeos_ai_evals.journal_memory.candidate import RETRIEVAL_CANDIDATE
from lifeos_ai_evals.journal_memory.corpus import SEARCH_USER, parse_corpus
from lifeos_ai_evals.journal_memory.memory_index import MemoryIndex

V1, V2 = db.FUNCTION, db.CANDIDATE_FUNCTION


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


# Query vector: unit(0). Cosine distances: near 0 < code 0.29 < chatty 0.55 < busy 0.68.
TEXTS = {
    "near": "Nessuna parola della domanda, ma il significato giusto.",
    "code": "The zeta code.",
    "chatty": "What is the time for the train, is it late?",
    "busy": "Is the shop for the tourists?",
}
CORPUS = parse_corpus(
    "ai0051-known-vectors",
    {
        "entries": [
            *(entry(key, "primary", text) for key, text in TEXTS.items()),
            entry("leak", "other", "The zeta code, what is it for? Another user."),
            entry("gone", "primary", "The zeta code, deleted.", "deleted"),
            entry("foreign", "primary", "The zeta code, foreign.", "foreign_identity"),
        ]
    },
)
VECTORS = {
    TEXTS["near"]: unit(0),
    TEXTS["code"]: unit(0, 2, 1.0),
    TEXTS["chatty"]: unit(0, 3, 2.0),
    TEXTS["busy"]: unit(0, 4, 3.0),
    "The zeta code, what is it for? Another user.": unit(0),
    "The zeta code, deleted.": unit(0),
    "The zeta code, foreign.": unit(0),
}


def search(index, function, question, limit=8):
    """One call of the real SQL function `function` (the store's statement)."""
    found = index.database.connection.execute(
        db.SEARCH_STATEMENTS[function],
        (
            CORPUS.user_id(SEARCH_USER),
            db.vector_literal(unit(0)),
            question,
            index.identity["embedding_provider"],
            index.identity["embedding_model"],
            index.identity["chunking_version"],
            limit,
        ),
    ).fetchall()
    return [
        (row.entry_id, row.vector_rank, row.lexical_rank, row.rrf_score)
        for row in (db.SearchRow(*values) for values in found)
    ]


def keys(rows):
    by_id = {CORPUS.entry_id(key): key for key in (*TEXTS, "leak", "gone", "foreign")}
    return [(by_id[row[0]], row[1], row[2]) for row in rows]


@pytest.fixture(scope="module")
def index(schema):
    index = MemoryIndex(
        CORPUS,
        lambda: FixedEmbedder(VECTORS),
        schema=schema,
        database_factory=partial(db.DisposableDatabase, function=V2),
    )
    index.build()
    yield index
    index.close()


def test_both_functions_are_verified_and_the_run_uses_v2(index):
    metadata = index.database.metadata
    assert metadata["function"] == V2 and metadata["function_verified"] is True
    assert metadata["function_body_sha256"] == V2_FUNCTION_BODY_SHA256
    assert metadata["latest_migration"] == db.CANDIDATE_MIGRATION
    # v1 is still in the database and still byte-for-byte the AddJournalMemory body.
    (source,) = index.database.connection.execute(
        "SELECT prosrc FROM pg_proc WHERE proname = %s", (V1,)
    ).fetchone()
    assert db.function_body_sha256(source) == FUNCTION_BODY_SHA256
    names = index.database.connection.execute(
        "SELECT proname::text FROM pg_proc WHERE proname LIKE 'search_journal_memory%' "
        "ORDER BY 1"
    ).fetchall()
    assert names == [(V1,), (V2,)]


def test_stop_word_votes_no_longer_outrank_the_closest_meaning(index):
    question = "what is the zeta for"
    # v1: every lexeme votes, so the chatty and busy chunks collect lexical votes from
    # "what / is / the / for" alone and the closest vector match falls to rank 4.
    assert keys(search(index, V1, question)) == [
        ("chatty", 3, 1),
        ("code", 2, 3),
        ("busy", 4, 2),
        ("near", 1, None),
    ]
    # v2: only "zeta" votes; fusion keeps the real lexical match first and the closest
    # meaning second. Candidate depths, RRF and ties are v1's.
    rows = search(index, V2, question)
    assert keys(rows) == [
        ("code", 2, 1),
        ("near", 1, None),
        ("chatty", 3, None),
        ("busy", 4, None),
    ]
    assert [row[3] for row in rows] == pytest.approx(
        [1 / 62 + 1 / 61, 1 / 61, 1 / 63, 1 / 64], abs=1e-12
    )
    assert [r[:3] for r in search(index, V2, question, limit=2)] == [
        r[:3] for r in rows[:2]
    ]


@pytest.mark.parametrize(
    "question",
    ["what is the", "Which is it for?", "di che è", "Quale è il"],
)
def test_a_stop_word_only_question_ranks_by_meaning_alone(index, question):
    rows = search(index, V2, question)
    assert all(row[2] is None for row in rows)
    assert [key for key, _, _ in keys(rows)] == ["near", "code", "chatty", "busy"]


def test_rare_codes_and_content_words_still_vote(index):
    rows = keys(search(index, V2, "Which ZETA code?"))
    assert rows[0] == ("code", 2, 1)
    assert rows[1:] == [("near", 1, None), ("chatty", 3, None), ("busy", 4, None)]


def test_v2_never_returns_other_users_deleted_or_foreign_rows(index):
    for question in ("The zeta code, what is it for?", "zeta", "the"):
        returned = {key for key, _, _ in keys(search(index, V2, question))}
        assert returned <= set(TEXTS), question
    other = index.database.connection.execute(
        db.SEARCH_STATEMENTS[V2],
        (
            CORPUS.user_id("other"),
            db.vector_literal(unit(0)),
            "zeta",
            index.identity["embedding_provider"],
            index.identity["embedding_model"],
            index.identity["chunking_version"],
            8,
        ),
    ).fetchall()
    assert [row[0] for row in other] == [CORPUS.entry_id("leak")]
    remaining = index.database.connection.execute(
        "SELECT count(*) FROM journal_memory_chunks WHERE entry_id = %s",
        (CORPUS.entry_id("gone"),),
    ).fetchone()[0]
    assert remaining == 0


def test_v2_rows_keep_the_frozen_scorer_ranking_contract(index):
    for question in ("what is the zeta for", "the", "zeta code"):
        hits = index.retrieve(question).hits
        assert ranking_violations(RetrievalOutput(hits, 8, 0, 0, None, None)) == []
    assert index.database.search_calls == 3


def test_v2_argument_checks_match_v1(index):
    connection = index.database.connection
    for limit in (0, 41):
        with pytest.raises(Exception, match="search_journal_memory_v2: limit"):
            connection.execute(
                db.SEARCH_STATEMENTS[V2],
                (
                    CORPUS.user_id(SEARCH_USER),
                    db.vector_literal(unit(0)),
                    "zeta",
                    "lab",
                    "m",
                    "c",
                    limit,
                ),
            )


def test_candidate_offline_run_on_the_development_set_is_complete_and_safe(schema):
    # Lab hashing embedder (no provider) through the candidate function on the
    # development set: pipeline coverage and safety only, never a quality claim.
    dataset = plugin.load(plugin.default_dataset())
    system = retrieval.RetrievalPredictor(
        name="journal-retrieval-v2-offline-pipeline-check",
        identity={
            **RETRIEVAL_CANDIDATE,
            "embedding": {
                "provider": embedders.HashingEmbedder.provider,
                "model": embedders.HashingEmbedder.model,
                "dimensions": embedders.HashingEmbedder.dimensions,
            },
        },
        index_embedder=embedders.HashingEmbedder,
        query_embedder=embedders.HashingEmbedder,
        database_factory=partial(db.DisposableDatabase, function=V2),
        schema=schema,
    )
    with system:
        result = evaluate("journal_retrieval", dataset, system, plugin.scorer())
        metadata = system.experiment_metadata()
    run = json.loads(result.to_json())
    assert run["errors"] == 0 and run["metadata"]["scored_cases"] == 40
    for key in (
        "cross_user_results",
        "deleted_results",
        "wrong_identity_results",
        "unknown_source_results",
        "malformed_ranking",
    ):
        assert run["aggregate_metrics"][key] == 0, key
    database = metadata["retrieval_run"]["database"]
    assert (database["function"], database["function_verified"]) == (V2, True)
    assert metadata["retrieval_run"]["search_calls"] == 40


def test_live_candidate_uses_the_production_adapter_and_its_own_function(schema):
    dataset = plugin.load(plugin.default_dataset())
    transport = EmbeddingTransport()
    system = retrieval.retrieval_v2_candidate(
        RETRIEVAL_CANDIDATE, transport=transport, sleep=no_sleep, schema=schema
    )
    subset = type(dataset)(
        dataset.name, dataset.version, dataset.cases[:2], dataset.sha256
    )
    with system:
        result = evaluate("journal_retrieval", subset, system, plugin.scorer())
        metadata = system.experiment_metadata()
    assert result.errors == 0
    assert result.metadata["experiment"] == RETRIEVAL_CANDIDATE
    assert result.system["name"] == "journal-retrieval-v2-candidate"
    database = metadata["retrieval_run"]["database"]
    assert (database["function"], database["function_verified"]) == (V2, True)
    assert database["function_body_sha256"] == V2_FUNCTION_BODY_SHA256
    assert metadata["retrieval_run"]["search_calls"] == 2
    # Same production indexing as the control: one batch per entry, one per question.
    corpus = dataset.cases[0].input.corpus
    assert len(transport.bodies) == len(corpus.entries) + 2
    assert "offline-test-key" not in json.dumps(metadata) + result.to_json()
