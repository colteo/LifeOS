"""Journal retrieval dataset: one frozen synthetic corpus + labelled questions.

File envelope: name, version, description, corpus {entries}, cases. Each case:
- input: {question} (the production EmbedQueryRequest shape);
- expected: {answerable, safety_critical, evidence: [{entry, quote}]}. Evidence are
  source spans labelled independently of any retrieval output; the production chunker
  maps them to chunks (corpus.resolve_evidence). An unanswerable case has no evidence.
"""

import json
from dataclasses import dataclass
from pathlib import Path

from lifeos_ai.journal_memory.schema import EmbedQueryRequest

from lifeos_ai_evals.core.engine import Dataset, load_dataset
from lifeos_ai_evals.journal_memory.corpus import (
    ChunkRef,
    Corpus,
    Evidence,
    parse_corpus,
    resolve_evidence,
)
from lifeos_ai_evals.journal_memory.memory_index import Hit

ENVELOPE = {"name", "version", "description", "corpus", "cases"}


@dataclass(frozen=True)
class RetrievalQuery:
    question: str
    corpus: Corpus


@dataclass(frozen=True)
class RetrievalExpectation:
    answerable: bool
    safety_critical: bool
    evidence: tuple[Evidence, ...]

    def relevant_chunks(self) -> frozenset[ChunkRef]:
        return frozenset(ref for item in self.evidence for ref in item.relevant_chunks)


@dataclass(frozen=True)
class RetrievalOutput:
    hits: tuple[Hit, ...]
    limit: int
    embedding_seconds: float
    retrieval_seconds: float
    embedding_attempts: int | None
    embedding_input_tokens: int | None


def read_envelope(path: Path) -> dict:
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    if not isinstance(data, dict) or set(data) != ENVELOPE:
        raise ValueError(f"dataset requires exactly {sorted(ENVELOPE)}")
    if not isinstance(data["description"], str) or not data["description"].startswith(
        "Synthetic"
    ):
        raise ValueError("dataset description must declare synthetic data")
    return data


def parse_question(raw) -> str:
    if not isinstance(raw, dict) or set(raw) != {"question"}:
        raise ValueError("input requires exactly: question")
    if not isinstance(raw["question"], str):
        raise ValueError("question must be a string")
    EmbedQueryRequest(question=raw["question"])  # production limits (1-500 after trim)
    return raw["question"]


def expectation_parser(corpus: Corpus):
    def parse(raw) -> RetrievalExpectation:
        if not isinstance(raw, dict) or set(raw) != {
            "answerable",
            "safety_critical",
            "evidence",
        }:
            raise ValueError(
                "expected requires exactly: answerable, safety_critical, evidence"
            )
        answerable, critical = raw["answerable"], raw["safety_critical"]
        if type(answerable) is not bool or type(critical) is not bool:
            raise ValueError("answerable and safety_critical must be booleans")
        if not isinstance(raw["evidence"], list):
            raise ValueError("evidence must be a list")
        evidence = tuple(resolve_evidence(corpus, item) for item in raw["evidence"])
        if answerable != bool(evidence):
            raise ValueError(
                "answerable cases need evidence; unanswerable cases have none"
            )
        if critical and not answerable:
            raise ValueError("safety_critical applies to answerable cases only")
        if len({(e.entry, e.quote) for e in evidence}) != len(evidence):
            raise ValueError("duplicate evidence")
        return RetrievalExpectation(answerable, critical, evidence)

    return parse


def load_retrieval_dataset(path: Path) -> Dataset:
    envelope = read_envelope(path)
    corpus = parse_corpus(envelope["name"], envelope["corpus"])
    dataset = load_dataset(path, parse_question, expectation_parser(corpus))
    cases = tuple(
        type(case)(
            case.id,
            RetrievalQuery(case.input, corpus),
            case.expected,
            case.tags,
            case.description,
        )
        for case in dataset.cases
    )
    for case in cases:
        if not case.description.startswith("Synthetic"):
            raise ValueError(f"{case.id}: description must start with 'Synthetic'")
    return Dataset(
        dataset.name,
        dataset.version,
        cases,
        dataset.sha256,
        dataset.hash_strategy,
        dataset.legacy_sha256,
    )
