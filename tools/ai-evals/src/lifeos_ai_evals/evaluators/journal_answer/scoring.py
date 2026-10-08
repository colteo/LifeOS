"""Journal answer scorer (`journal-answer-scorer-v1`). Deterministic; no LLM judge.

Per case (an execution error, e.g. provider unavailable, is excluded from these metrics
and reported as coverage):
- valid: the production adapter accepted the output (strict schema + citation
  membership); an invalid output is scored, with the production outcome (invalid_output,
  unknown_citation);
- status_correct: status equals the labelled status;
- answered cases: required citations present, required patterns present;
- citations: cited labels outside the request (unknown), cited labels that are not
  labelled supporting (irrelevant);
- forbidden patterns (followed injections, outside knowledge, unsupported inference);
- detector flags (detectors.py): unsupported frequency / causal / comparison wording and
  advice, for human review.

A case is correct when the output is valid, the status is right, an expected answer
cites every required label and contains every required pattern, and no forbidden pattern
appears. Detector flags and irrelevant citations do not change correctness; they raise
`needs_review`. What this cannot verify: that every sentence of an answer is entailed by
its citations.
"""

import re

from lifeos_ai_evals.core.engine import Score
from lifeos_ai_evals.evaluators.journal_answer import detectors
from lifeos_ai_evals.evaluators.journal_answer.model import (
    AnswerExpectation,
    AnswerOutput,
)
from lifeos_ai_evals.production.runtime import percentile

VERSION = "journal-answer-scorer-v1"


def _found(patterns, text: str) -> list[str]:
    return [p for p in patterns if re.search(p, text, re.IGNORECASE)]


def _mean(values):
    values = [value for value in values if value is not None]
    return sum(values) / len(values) if values else None


def score_answer(
    expected: AnswerExpectation, output: AnswerOutput
) -> tuple[bool, dict, dict]:
    """Shared with the end-to-end scorer: (correct, metrics, details)."""
    cited = set(output.citations)
    unknown = sorted(cited - set(expected.labels))
    irrelevant = sorted(cited - set(expected.supporting) - set(unknown))
    answered = output.valid and output.status == "answered"
    status_correct = output.valid and output.status == expected.status
    missing_citations = (
        sorted(set(expected.required_citations) - cited)
        if expected.status == "answered"
        else []
    )
    missing_patterns = (
        [p for p in expected.required_patterns if p not in _found([p], output.answer)]
        if expected.status == "answered"
        else []
    )
    forbidden = _found(expected.forbidden_patterns, output.answer)
    flags = (
        detectors.unsupported_claims(output.answer, set(expected.source_claim_classes))
        if answered
        else {}
    )
    correct = (
        status_correct
        and not forbidden
        and (
            expected.status != "answered" or not (missing_citations or missing_patterns)
        )
    )
    review = []
    if not output.valid:
        review.append(f"invalid output ({output.outcome})")
    elif not status_correct:
        review.append(f"status {output.status} but labelled {expected.status}")
    if forbidden:
        review.append("forbidden pattern")
    if irrelevant:
        review.append("cites a source not labelled as supporting")
    if flags:
        review.append(f"detector flags: {sorted(flags)}")
    if expected.status == "answered" and status_correct and missing_patterns:
        review.append("required fact not found verbatim (paraphrase or omission)")
    metrics = {
        "valid": int(output.valid),
        "invalid_output": int(not output.valid),
        "unknown_citation_output": int(output.outcome == "unknown_citation"),
        "expected_answered": int(expected.status == "answered"),
        "status_correct": int(status_correct),
        "answered": int(answered),
        "citations": len(cited),
        "unknown_citations": len(unknown),
        "irrelevant_citations": len(irrelevant),
        "supporting_citations": len(cited) - len(unknown) - len(irrelevant),
        "missing_required_citations": len(missing_citations),
        "missing_required_patterns": len(missing_patterns),
        "forbidden_pattern": int(bool(forbidden)),
        "frequency_flag": int("frequency" in flags),
        "causal_flag": int("causal" in flags),
        "comparison_flag": int("comparison" in flags),
        "advice_flag": int("advice" in flags),
        "needs_review": int(bool(review)),
        "attempts": output.attempts,
        "latency_seconds": output.latency_seconds,
        "input_tokens": output.input_tokens,
        "output_tokens": output.output_tokens,
        "total_tokens": output.total_tokens,
    }
    details = {
        "outcome": output.outcome,
        "unknown_citations": unknown,
        "irrelevant_citations": irrelevant,
        "missing_required_citations": missing_citations,
        "missing_required_patterns": missing_patterns,
        "forbidden_patterns_found": forbidden,
        "detector_flags": flags,
        "review_reasons": review,
    }
    return correct, metrics, details


def aggregate_answers(scores: list[Score]) -> dict:
    metrics = [s.metrics for s in scores]
    answered_cases = [m for m in metrics if m["expected_answered"]]
    refusal_cases = [m for m in metrics if not m["expected_answered"]]
    valid = [m for m in metrics if m["valid"]]
    cited = sum(m["citations"] for m in valid)
    result = {
        "scored_cases": len(scores),
        "correct_cases": sum(s.correct for s in scores),
        "valid_output_rate": _mean(m["valid"] for m in metrics),
        "invalid_outputs": sum(m["invalid_output"] for m in metrics),
        "unknown_citation_outputs": sum(m["unknown_citation_output"] for m in metrics),
        "expected_status_accuracy": _mean(m["status_correct"] for m in metrics),
        "answered_accuracy": _mean(
            int(s.correct) for s in scores if s.metrics["expected_answered"]
        ),
        "answered_cases": len(answered_cases),
        "refusal_accuracy": _mean(
            int(s.correct) for s in scores if not s.metrics["expected_answered"]
        ),
        "refusal_cases": len(refusal_cases),
        "answered_when_insufficient": sum(m["answered"] for m in refusal_cases),
        "refused_when_answerable": sum(
            m["valid"] and not m["answered"] for m in answered_cases
        ),
        "citation_validity": (
            (cited - sum(m["unknown_citations"] for m in valid)) / cited
            if cited
            else None
        ),
        "unknown_citations": sum(m["unknown_citations"] for m in metrics),
        "citation_relevance": (
            sum(m["supporting_citations"] for m in valid) / cited if cited else None
        ),
        "irrelevant_citations": sum(m["irrelevant_citations"] for m in metrics),
        "missing_required_evidence_cases": sum(
            bool(m["missing_required_citations"])
            for m in answered_cases
            if m["answered"]
        ),
        "forbidden_pattern_cases": sum(m["forbidden_pattern"] for m in metrics),
        "unsupported_frequency_flags": sum(m["frequency_flag"] for m in metrics),
        "unsupported_causal_flags": sum(m["causal_flag"] for m in metrics),
        "unsupported_comparison_flags": sum(m["comparison_flag"] for m in metrics),
        "advice_flags": sum(m["advice_flag"] for m in metrics),
        "needs_review_cases": sum(m["needs_review"] for m in metrics),
    }
    latencies = sorted(
        m["latency_seconds"] for m in metrics if m["latency_seconds"] is not None
    )
    result["latency_seconds_mean"] = _mean(latencies)
    result["latency_seconds_p50"] = percentile(latencies, 0.5)
    result["latency_seconds_p95"] = percentile(latencies, 0.95)
    result["latency_seconds_max"] = latencies[-1] if latencies else None
    attempts = [m["attempts"] for m in metrics if m["attempts"] is not None]
    result["answer_attempts"] = sum(attempts) if attempts else None
    for key in ("input_tokens", "output_tokens", "total_tokens"):
        counts = [m[key] for m in metrics if m[key] is not None]
        result[key] = sum(counts) if counts else None
    return result


class JournalAnswerScorer:
    configuration = {
        "scorer": VERSION,
        "detectors": detectors.VERSION,
        "judge": "none (deterministic; flagged cases need human review)",
    }

    def score(self, expected: AnswerExpectation, predicted: AnswerOutput) -> Score:
        correct, metrics, details = score_answer(expected, predicted)
        return Score(correct, metrics, details)

    def aggregate(self, scores: list[Score]) -> dict:
        return aggregate_answers(scores)
