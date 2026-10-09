"""End-to-end dataset: a frozen synthetic corpus + questions asked through the whole Ask
path.

Each case: input {question}; expected {status, evidence, required_patterns,
forbidden_patterns}. Evidence are source spans (as in the retrieval dataset); an
insufficient-evidence case has none. Cases tagged `safety` are absolute acceptance
gates.
"""

import re
from dataclasses import dataclass
from pathlib import Path

from lifeos_ai_evals.core.engine import Dataset, load_dataset
from lifeos_ai_evals.evaluators.journal_answer.model import AnswerOutput
from lifeos_ai_evals.evaluators.journal_retrieval.model import (
    parse_question,
    read_envelope,
)
from lifeos_ai_evals.journal_memory.corpus import (
    Corpus,
    Evidence,
    parse_corpus,
    resolve_evidence,
)
from lifeos_ai_evals.journal_memory.memory_index import Hit

EXPECTED_KEYS = {"status", "evidence", "required_patterns", "forbidden_patterns"}


@dataclass(frozen=True)
class RagQuery:
    question: str
    corpus: Corpus


@dataclass(frozen=True)
class RagExpectation:
    status: str
    evidence: tuple[Evidence, ...]
    required_patterns: tuple[str, ...]
    forbidden_patterns: tuple[str, ...]


@dataclass(frozen=True)
class RagOutput:
    hits: tuple[Hit, ...]
    answer_called: bool
    answer: AnswerOutput
    cited: tuple[Hit | None, ...]
    embedding_seconds: float
    retrieval_seconds: float
    answer_seconds: float | None
    total_seconds: float
    embedding_input_tokens: int | None
    # Detector classes whose wording appears in the retrieved passages (no text
    # exported).
    source_claim_classes: tuple[str, ...] = ()


def _patterns(values) -> tuple[str, ...]:
    if not isinstance(values, list) or not all(
        isinstance(v, str) and v for v in values
    ):
        raise ValueError("patterns must be a list of nonempty regexes")
    for value in values:
        try:
            re.compile(value)
        except re.error as exc:
            raise ValueError(f"invalid regex: {value}") from exc
    return tuple(values)


def expectation_parser(corpus: Corpus):
    def parse(raw) -> RagExpectation:
        if not isinstance(raw, dict) or set(raw) != EXPECTED_KEYS:
            raise ValueError(f"expected requires exactly {sorted(EXPECTED_KEYS)}")
        if raw["status"] not in ("answered", "insufficient_evidence"):
            raise ValueError("unknown expected status")
        evidence = tuple(resolve_evidence(corpus, item) for item in raw["evidence"])
        required = _patterns(raw["required_patterns"])
        if (raw["status"] == "answered") != bool(evidence):
            raise ValueError(
                "answered cases need evidence; insufficient cases have none"
            )
        if raw["status"] == "answered" and not required:
            raise ValueError("answered cases need required patterns")
        if raw["status"] != "answered" and required:
            raise ValueError("insufficient-evidence cases have no required pattern")
        return RagExpectation(
            raw["status"], evidence, required, _patterns(raw["forbidden_patterns"])
        )

    return parse


def load_rag_dataset(path: Path) -> Dataset:
    envelope = read_envelope(path)
    corpus = parse_corpus(envelope["name"], envelope["corpus"])
    dataset = load_dataset(path, parse_question, expectation_parser(corpus))
    cases = []
    for case in dataset.cases:
        if not case.description.startswith("Synthetic"):
            raise ValueError(f"{case.id}: description must start with 'Synthetic'")
        cases.append(
            type(case)(
                case.id,
                RagQuery(case.input, corpus),
                case.expected,
                case.tags,
                case.description,
            )
        )
    return Dataset(
        dataset.name,
        dataset.version,
        tuple(cases),
        dataset.sha256,
        dataset.hash_strategy,
        dataset.legacy_sha256,
    )
