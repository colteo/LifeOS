"""Journal answer dataset: PRESELECTED evidence passages per question (no retrieval).

Each case:
- input: exactly the production `AnswerRequest` ({question, sources: [{label, title,
  occurred_at, text}]}), validated by the production model;
- expected: {status, supporting, required_citations, required_patterns,
  forbidden_patterns}.

Expected fields:
- status: what a grounded answer must be (answered | insufficient_evidence);
- supporting: labels whose text supports the answer (citation relevance);
- required_citations: labels an answered output must cite (subset of supporting);
- required_patterns: case-insensitive regexes an answered output must contain (the key
  fact, e.g. an amount or a name);
- forbidden_patterns: case-insensitive regexes that must never appear (followed
  injections, outside knowledge, unsupported inference).
"""

import re
from dataclasses import dataclass

from lifeos_ai.journal_memory.schema import AnswerRequest

from lifeos_ai_evals.evaluators.journal_answer.detectors import claim_classes

STATUSES = ("answered", "insufficient_evidence")
EXPECTED_KEYS = {
    "status",
    "supporting",
    "required_citations",
    "required_patterns",
    "forbidden_patterns",
}


@dataclass(frozen=True)
class AnswerExpectation:
    status: str
    supporting: tuple[str, ...]
    required_citations: tuple[str, ...]
    required_patterns: tuple[str, ...]
    forbidden_patterns: tuple[str, ...]
    labels: tuple[str, ...] = ()
    # Detector classes (causal, frequency, comparison) whose wording appears in the
    # sources.
    source_claim_classes: tuple[str, ...] = ()


@dataclass(frozen=True)
class AnswerOutput:
    """One answer attempt. `outcome` is the production adapter's own log classification
    (answered, insufficient_evidence, invalid_output, unknown_citation, http_400,
    ...)."""

    valid: bool
    outcome: str
    status: str | None
    answer: str
    citations: tuple[str, ...]
    attempts: int | None = None
    latency_seconds: float | None = None
    input_tokens: int | None = None
    output_tokens: int | None = None
    total_tokens: int | None = None
    source_count: int = 0


def parse_input(raw) -> AnswerRequest:
    return AnswerRequest.model_validate(raw)


def _patterns(values, label: str) -> tuple[str, ...]:
    if not isinstance(values, list):
        raise ValueError(f"{label} must be a list")
    for value in values:
        if not isinstance(value, str) or not value:
            raise ValueError(f"{label} must contain nonempty regexes")
        try:
            re.compile(value)
        except re.error as exc:
            raise ValueError(f"invalid regex: {value}") from exc
    return tuple(values)


def parse_expected_with(request: AnswerRequest, raw) -> AnswerExpectation:
    if not isinstance(raw, dict) or set(raw) != EXPECTED_KEYS:
        raise ValueError(f"expected requires exactly {sorted(EXPECTED_KEYS)}")
    if raw["status"] not in STATUSES:
        raise ValueError("unknown expected status")
    labels = tuple(source.label for source in request.sources)
    supporting = tuple(raw["supporting"])
    required = tuple(raw["required_citations"])
    if not set(supporting) <= set(labels) or not set(required) <= set(supporting):
        raise ValueError("citations must be request labels; required within supporting")
    required_patterns = _patterns(raw["required_patterns"], "required_patterns")
    forbidden = _patterns(raw["forbidden_patterns"], "forbidden_patterns")
    if raw["status"] == "answered" and not (required and required_patterns):
        raise ValueError("answered cases need required citations and patterns")
    if raw["status"] == "insufficient_evidence" and (required or required_patterns):
        raise ValueError("insufficient-evidence cases require no citation or pattern")
    return AnswerExpectation(
        raw["status"],
        supporting,
        required,
        required_patterns,
        forbidden,
        labels,
        tuple(sorted(claim_classes(" ".join(s.text for s in request.sources)))),
    )
