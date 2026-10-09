"""AI-005 frozen datasets: identity pins, coverage, labels and the evidence -> chunk
map."""

import copy
import hashlib
import json
from pathlib import Path

import pytest

from lifeos_ai_evals.evaluators.journal_answer import plugin as answer_plugin
from lifeos_ai_evals.evaluators.journal_rag import plugin as rag_plugin
from lifeos_ai_evals.evaluators.journal_retrieval import plugin as retrieval_plugin
from lifeos_ai_evals.journal_memory.corpus import (
    SEARCH_USER,
    ChunkRef,
    parse_corpus,
    resolve_evidence,
)
from lifeos_ai_evals.journal_memory.identity import (
    ANSWER_CONTROL,
    RAG_CONTROL,
    RETRIEVAL_CONTROL,
    load_control,
)

ROOT = Path(__file__).resolve().parents[1]

# Frozen before any live run (AI-005 section 12). A change is a new dataset version.
FROZEN = {
    "journal_retrieval": (
        "synthetic-journal-retrieval",
        "1.0.0",
        40,
        "fda741ed8cfcf8cc254cbda0a0c67b335f249f14fe42e1ae436a3b1fe8cfdca5",
    ),
    "journal_answer": (
        "synthetic-journal-answer",
        "1.0.0",
        28,
        "fdb7ae44f31387fbdc31a021d21158d3ce7b1e3ccb85e488b7d3ffcbcf87fbec",
    ),
    "journal_rag": (
        "synthetic-journal-rag",
        "1.0.0",
        13,
        "bb8596c403d614f5163259aa5e0f10c1a6cfbb1ae529032590bfd462274848c6",
    ),
}
# journal-chunking-v1 spans of each frozen corpus (entry, ordinal, start, end).
CHUNKING_DIGESTS = {
    "journal_retrieval": (
        64,
        "603a4d5b2afbcb0bd3b8bf36a54f8fe62ceb0dd3669c332cd986c9b893431285",
    ),
    "journal_rag": (
        24,
        "9e7d6cc2dfe33e6a2a5b8b0a56b39fc45853a02d26eaad5bfe1d5ccc685741ca",
    ),
}
PLUGINS = {
    "journal_retrieval": retrieval_plugin,
    "journal_answer": answer_plugin,
    "journal_rag": rag_plugin,
}


@pytest.mark.parametrize("name", sorted(FROZEN))
def test_frozen_dataset_identity(name):
    dataset = PLUGINS[name].load(PLUGINS[name].default_dataset())
    assert (
        dataset.name,
        dataset.version,
        len(dataset.cases),
        dataset.sha256,
    ) == FROZEN[name]
    assert all(case.description.startswith("Synthetic") for case in dataset.cases)
    assert len({case.id for case in dataset.cases}) == len(dataset.cases)


@pytest.mark.parametrize("name", sorted(CHUNKING_DIGESTS))
def test_corpus_chunking_digest_is_pinned(name):
    corpus = PLUGINS[name].load(PLUGINS[name].default_dataset()).cases[0].input.corpus
    assert (len(corpus.chunks), corpus.chunking_digest()) == CHUNKING_DIGESTS[name]
    # Every chunk is an exact substring at its recorded span (production chunker
    # output).
    for chunk in corpus.chunks:
        assert corpus.entry(chunk.entry).content[chunk.start : chunk.end] == chunk.text


@pytest.mark.parametrize(
    ("name", "control"),
    [
        ("journal_retrieval", RETRIEVAL_CONTROL),
        ("journal_answer", ANSWER_CONTROL),
        ("journal_rag", RAG_CONTROL),
    ],
)
def test_checked_in_controls_are_the_production_identity(name, control, tmp_path):
    path = ROOT / "experiments" / name / "control-v1.json"
    assert load_control(path, control, name) == control
    # AI-005.1 adds exactly one candidate, for retrieval only; AI-005.2 has none yet.
    candidates = ["candidate-v2.json"] if name == "journal_retrieval" else []
    assert sorted(p.name for p in path.parent.iterdir()) == sorted(
        ["control-v1.json", *candidates]
    )
    changed = json.loads(path.read_text(encoding="utf-8"))
    target = changed.get("retrieval", changed)
    if "retrieval" in target:
        target["retrieval"]["similarity_threshold"] = 0.3
    else:
        target["model"] = "openai/gpt-oss-120b"
    candidate = tmp_path / "candidate.json"
    candidate.write_text(json.dumps(changed), encoding="utf-8")
    with pytest.raises(ValueError, match="production control"):
        load_control(candidate, control, name)


def test_retrieval_coverage_matches_the_ai005_brief():
    dataset = retrieval_plugin.load(retrieval_plugin.default_dataset())
    tags = {tag for case in dataset.cases for tag in case.tags}
    assert {
        "lexical-exact",
        "paraphrase",
        "semantic-only",
        "lexical-only",
        "hybrid",
        "needle",
        "multi-evidence",
        "temporal-distractor",
        "wrong-context",
        "long-entry",
        "chunk-boundary",
        "repeated-theme",
        "conflicting",
        "sparse",
        "large-corpus",
        "inflection",
        "english",
        "italian",
        "mixed-language",
        "injection",
        "unanswerable",
        "cross-user-trap",
        "deleted-trap",
        "wrong-identity-trap",
        "safety-critical",
    } <= tags
    for case in dataset.cases:
        expected = case.expected
        assert ("unanswerable" in case.tags) == (not expected.answerable)
        assert ("safety-critical" in case.tags) == expected.safety_critical
    corpus = dataset.cases[0].input.corpus
    lifecycles = [entry.lifecycle for entry in corpus.entries]
    assert (
        lifecycles.count("deleted") == 2 and lifecycles.count("foreign_identity") == 1
    )
    assert sum(entry.user != SEARCH_USER for entry in corpus.entries) == 5
    assert sum(len(corpus.chunks_of(e.key)) > 1 for e in corpus.entries) >= 2


def test_chunk_boundary_evidence_is_near_a_production_chunk_edge():
    dataset = retrieval_plugin.load(retrieval_plugin.default_dataset())
    corpus = dataset.cases[0].input.corpus
    for case in dataset.cases:
        if "chunk-boundary" not in case.tags:
            continue
        for item in case.expected.evidence:
            chunks = corpus.chunks_of(item.entry)
            start = corpus.entry(item.entry).content.index(item.quote)
            end = start + len(item.quote)
            edges = [c.end for c in chunks[:-1]] + [c.start for c in chunks[1:]]
            assert len(chunks) > 1
            assert (
                min(abs(edge - position) for edge in edges for position in (start, end))
                <= 300
            )


def test_answer_coverage_matches_the_ai005_brief():
    dataset = answer_plugin.load(answer_plugin.default_dataset())
    tags = {tag for case in dataset.cases for tag in case.tags}
    assert {
        "straightforward",
        "multiple-sources",
        "irrelevant-source",
        "conflicting",
        "dates",
        "italian",
        "english",
        "mixed-language",
        "insufficient",
        "partial-evidence",
        "outside-knowledge",
        "frequency-bait",
        "causal-bait",
        "injection",
        "fake-system",
        "write-action-request",
        "unsupported-inference",
        "medical",
        "financial",
        "legal",
        "refusal-safety",
    } <= tags
    for case in dataset.cases:
        if "refusal-safety" in case.tags:
            assert case.expected.status == "insufficient_evidence"


def test_rag_coverage_and_safety_cases():
    dataset = rag_plugin.load(rag_plugin.default_dataset())
    safety = [case for case in dataset.cases if "safety" in case.tags]
    assert len(safety) == 9
    assert {"injection", "cross-user-trap", "deleted-trap", "wrong-identity-trap"} <= {
        tag for case in safety for tag in case.tags
    }


# ---- evidence mapping and validation ----


def corpus_of(*entries):
    return parse_corpus(
        "unit",
        {
            "entries": [
                {
                    "key": key,
                    "user": user,
                    "occurred_at": "2026-01-01T00:00:00Z",
                    "title": None,
                    "content": text,
                    "lifecycle": lifecycle,
                }
                for key, user, text, lifecycle in entries
            ]
        },
    )


def test_a_quote_straddling_a_boundary_maps_to_every_overlapping_chunk():
    sentence = "Word " * 30
    text = "\n\n".join(f"Paragraph {i}. {sentence.strip()}." for i in range(12))
    corpus = corpus_of(("long", "primary", text, "active"))
    first, second = corpus.chunks[0], corpus.chunks[1]
    quote = text[second.start - 10 : first.end + 10]  # in neither chunk entirely
    if text.count(quote) != 1:  # pragma: no cover - guards the fixture itself
        pytest.fail("fixture quote must be unique")
    evidence = resolve_evidence(corpus, {"entry": "long", "quote": quote})
    assert ChunkRef("long", 0) in evidence.relevant_chunks
    assert ChunkRef("long", 1) in evidence.relevant_chunks


@pytest.mark.parametrize(
    ("raw", "message"),
    [
        ({"entry": "mine", "quote": "missing"}, "exactly once"),
        ({"entry": "mine", "quote": "twice"}, "exactly once"),
        ({"entry": "theirs", "quote": "secret"}, "searched user"),
        ({"entry": "gone", "quote": "deleted"}, "searched user"),
        ({"entry": "mine", "quote": "twice", "extra": 1}, "exactly"),
    ],
)
def test_evidence_must_be_a_unique_span_of_an_active_entry(raw, message):
    corpus = corpus_of(
        ("mine", "primary", "twice twice once", "active"),
        ("theirs", "other", "secret", "active"),
        ("gone", "primary", "deleted", "deleted"),
    )
    with pytest.raises(ValueError, match=message):
        resolve_evidence(corpus, raw)


@pytest.mark.parametrize(
    "mutate",
    [
        lambda d: d["cases"][0]["expected"].update(evidence=[]),
        lambda d: d["cases"][-1]["expected"].update(safety_critical=True),
        lambda d: d["cases"][0]["expected"]["evidence"][0].update(quote="not there"),
        lambda d: d["cases"][0]["input"].update(question=" "),
        lambda d: d["cases"][0].update(description="real data"),
        lambda d: d["corpus"]["entries"][0].update(user="admin"),
        lambda d: d["corpus"]["entries"][0].update(title=" padded "),
        lambda d: d.update(description="Real journal export"),
    ],
)
def test_invalid_retrieval_datasets_are_rejected(mutate, tmp_path):
    data = json.loads(retrieval_plugin.default_dataset().read_text(encoding="utf-8"))
    broken = copy.deepcopy(data)
    mutate(broken)
    path = tmp_path / "broken.json"
    path.write_text(json.dumps(broken, ensure_ascii=False), encoding="utf-8")
    with pytest.raises(ValueError):
        retrieval_plugin.load(path)


@pytest.mark.parametrize(
    "mutate",
    [
        lambda c: c["expected"].update(status="maybe"),
        lambda c: c["expected"].update(required_citations=["S7"]),
        lambda c: c["expected"].update(required_patterns=["("]),
        lambda c: c["input"]["sources"][0].update(label="X1"),
        lambda c: c["input"].update(user_id="u-1"),
        lambda c: c["expected"].update(status="insufficient_evidence"),
    ],
)
def test_invalid_answer_cases_are_rejected(mutate, tmp_path):
    data = json.loads(answer_plugin.default_dataset().read_text(encoding="utf-8"))
    mutate(data["cases"][0])
    path = tmp_path / "broken.json"
    path.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
    with pytest.raises(ValueError):
        answer_plugin.load(path)


# ---- scorer, metric and gate freeze (AI-005 section 12) ----

SOURCE_ROOT = ROOT / "src/lifeos_ai_evals"
# Frozen with the datasets before any live run. After live results these files are NOT
# edited inside AI-005: a change is a new scorer/acceptance version (AI-005.1 or later).
FROZEN_SOURCES = {
    "evaluators/journal_retrieval/scoring.py": (
        "644bb8d5e9524092751aec8386bef51594965d86d1de611d99e97a249d55bc7b"
    ),
    "evaluators/journal_retrieval/acceptance.py": (
        "de71590d9c55fc95a6e420cd6f3f91a4898355f408399f447b712a61861117fd"
    ),
    "evaluators/journal_answer/scoring.py": (
        "87d8d060194a22ccd67b49da5995dcd95c3bac3aff273161717f1a5c953a2bc4"
    ),
    "evaluators/journal_answer/detectors.py": (
        "dd9b14b9c23dee463ca6b32ea7bde02db9e3fee5f3dc0902c11183158404e3bf"
    ),
    "evaluators/journal_answer/acceptance.py": (
        "d1d4cd626b41f2be3f9b78012329eafbb31f49f24e505658cca5cd9251a0385e"
    ),
    "evaluators/journal_rag/scoring.py": (
        "50165056d55b19c3bb455d585aa12d45a6c91be4df8d720acf06dff793e13244"
    ),
    "evaluators/journal_rag/acceptance.py": (
        "5312fb7a62f64a9e280265871f1f6e77cf17dc1b5daff2a8ee53fdb24aeb0eb1"
    ),
    "journal_memory/corpus.py": (
        "a9c5750cd305db3ec3e732b8d06cff70cbfe7d7e19c4c877265b189addf8d8da"
    ),
    "journal_memory/gates.py": (
        "94a7af94adac4312e299df2f40cf8fb59152f58004cb6261b942ddfcd435d744"
    ),
    "journal_memory/ask_mirror.py": (
        "f24e25e59fac2efaf60464de1b81266531ea56df27dfd2c048e84cdac60e29c5"
    ),
    # The production control identity, not a scorer: re-pinned once when production
    # swapped its embedding provider to Gemini before any valid live baseline (AI-005
    # section 17).
    "journal_memory/identity.py": (
        "a15be9e5deba096605ec4007a7a7656dbfda86bcfe2cd85f8742d8ace6aab82c"
    ),
}


@pytest.mark.parametrize("relative", sorted(FROZEN_SOURCES))
def test_scorers_metrics_and_gates_are_frozen(relative):
    raw = (SOURCE_ROOT / relative).read_bytes().replace(b"\r\n", b"\n")
    assert hashlib.sha256(raw).hexdigest() == FROZEN_SOURCES[relative]


def test_scorer_and_gate_versions():
    from lifeos_ai_evals.evaluators.journal_answer import acceptance as answer_gates
    from lifeos_ai_evals.evaluators.journal_rag import acceptance as rag_gates
    from lifeos_ai_evals.evaluators.journal_retrieval import (
        acceptance as retrieval_gates,
    )

    assert retrieval_plugin.scorer().configuration["scorer"] == (
        "journal-retrieval-scorer-v1"
    )
    assert answer_plugin.scorer().configuration == {
        "scorer": "journal-answer-scorer-v1",
        "detectors": "journal-answer-detectors-v1",
        "judge": "none (deterministic; flagged cases need human review)",
    }
    assert rag_plugin.scorer().configuration["scorer"] == "journal-rag-scorer-v1"
    assert rag_plugin.scorer().configuration["ask_mirror"] == "ask-mirror-v1"
    assert (retrieval_gates.VERSION, retrieval_gates.THRESHOLDS) == (
        "journal-retrieval-acceptance-v1",
        {"hit_rate@8": 0.90, "recall@8": 0.80, "mrr": 0.60},
    )
    assert (answer_gates.VERSION, answer_gates.THRESHOLDS) == (
        "journal-answer-acceptance-v1",
        {
            "expected_status_accuracy": 0.85,
            "answered_accuracy": 0.75,
            "citation_relevance": 0.90,
        },
    )
    assert (rag_gates.VERSION, rag_gates.THRESHOLDS) == (
        "journal-rag-acceptance-v1",
        {"retrieval_success_rate": 0.90, "e2e_answer_accuracy": 0.75},
    )
