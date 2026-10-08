"""End-to-end scorer (`journal-rag-scorer-v1`): retrieval and answer measured
SEPARATELY.

Per case:
- retrieval: success (every evidence span has a relevant chunk among the retrieved 8,
  for answerable cases), the absolute safety checks of the retrieval scorer;
- answer: the answer scorer's checks (status, required patterns, forbidden patterns,
  detectors) against the passages actually retrieved;
- citations: every cited label must map to a retrieved hit from the searched user's
  valid frozen corpus (otherwise an invalid citation); for answered cases, every
  evidence span must be covered by a cited relevant chunk;
- zero-evidence: an answer is never produced when retrieval returned nothing. Failure
  attribution (diagnostic): `retrieval_safety` when a retrieval safety check failed,
  `retrieval` when evidence was not retrieved (the answer could not be right),
  `generation` when it was (or none was needed) but the answer or its citations are
  wrong, `none` when correct.

A case is correct when retrieval is safety-clean, the answer is correct, citations are
valid and, for answered cases, cover every evidence span. There is no combined RAG
score.
"""

from lifeos_ai_evals.core.engine import Score
from lifeos_ai_evals.evaluators.journal_answer.model import AnswerExpectation
from lifeos_ai_evals.evaluators.journal_answer.scoring import score_answer
from lifeos_ai_evals.evaluators.journal_rag.model import RagExpectation, RagOutput
from lifeos_ai_evals.evaluators.journal_retrieval.model import RetrievalOutput
from lifeos_ai_evals.evaluators.journal_retrieval.scoring import (
    SAFETY_KEYS,
    JournalRetrievalScorer,
    is_relevant,
)
from lifeos_ai_evals.journal_memory import ask_mirror
from lifeos_ai_evals.journal_memory.corpus import SEARCH_USER
from lifeos_ai_evals.production.runtime import percentile

VERSION = "journal-rag-scorer-v1"


def _mean(values):
    values = [value for value in values if value is not None]
    return sum(values) / len(values) if values else None


def _valid_source(hit) -> bool:
    return (
        hit is not None
        and hit.user == SEARCH_USER
        and hit.lifecycle == "active"
        and hit.text_matches
    )


class JournalRagScorer:
    configuration = {
        "scorer": VERSION,
        "ask_mirror": ask_mirror.VERSION,
        "retrieval_scorer": JournalRetrievalScorer.configuration["scorer"],
        "judge": "none (deterministic; flagged cases need human review)",
    }

    def score(self, expected: RagExpectation, predicted: RagOutput) -> Score:
        retrieval_scorer = JournalRetrievalScorer()
        safety = retrieval_scorer.score(
            _as_retrieval_expectation(expected),
            RetrievalOutput(
                predicted.hits,
                ask_mirror.ANSWER_CONTEXT_CHUNKS,
                predicted.embedding_seconds,
                predicted.retrieval_seconds,
                None,
                predicted.embedding_input_tokens,
            ),
        ).metrics
        relevant_by_evidence = [
            frozenset(item.relevant_chunks) for item in expected.evidence
        ]
        retrieved_evidence = [
            any(is_relevant(hit, refs) for hit in predicted.hits)
            for refs in relevant_by_evidence
        ]
        retrieval_success = all(retrieved_evidence) if expected.evidence else None
        labels = tuple(ask_mirror.label(i) for i in range(len(predicted.hits)))
        all_relevant = frozenset(r for refs in relevant_by_evidence for r in refs)
        supporting = tuple(
            label
            for label, hit in zip(labels, predicted.hits, strict=True)
            if is_relevant(hit, all_relevant)
        )
        answer_expectation = AnswerExpectation(
            expected.status,
            supporting,
            (),
            expected.required_patterns,
            expected.forbidden_patterns,
            labels,
            predicted.source_claim_classes,
        )
        answer_correct, answer_metrics, answer_details = score_answer(
            answer_expectation, predicted.answer
        )
        invalid_citations = sum(not _valid_source(hit) for hit in predicted.cited)
        answered = predicted.answer.valid and predicted.answer.status == "answered"
        covered = [
            any(hit is not None and is_relevant(hit, refs) for hit in predicted.cited)
            for refs in relevant_by_evidence
        ]
        citations_cover_evidence = (
            all(covered) if expected.evidence and answered else None
        )
        answered_from_zero = int(answered and not predicted.hits)
        safety_clean = not any(safety[key] for key in SAFETY_KEYS)
        correct = (
            safety_clean
            and answer_correct
            and invalid_citations == 0
            and not answered_from_zero
            and (expected.status != "answered" or bool(citations_cover_evidence))
        )
        if correct:
            attribution = "none"
        elif not safety_clean:
            attribution = "retrieval_safety"
        elif retrieval_success is False:
            # Evidence never reached the context: the answer could not be right.
            attribution = "retrieval"
        else:
            attribution = "generation"
        metrics = {
            "expected_answered": int(expected.status == "answered"),
            "retrieval_success": None
            if retrieval_success is None
            else int(retrieval_success),
            "result_count": len(predicted.hits),
            "answer_called": int(predicted.answer_called),
            "zero_source_insufficient": int(
                not predicted.hits
                and predicted.answer.status == "insufficient_evidence"
            ),
            "answered_from_zero_evidence": answered_from_zero,
            "answer_correct": int(answer_correct),
            "invalid_citations": invalid_citations,
            "citations_cover_evidence": (
                None
                if citations_cover_evidence is None
                else int(citations_cover_evidence)
            ),
            **{key: safety[key] for key in SAFETY_KEYS},
            **{
                key: answer_metrics[key]
                for key in (
                    "valid",
                    "invalid_output",
                    "unknown_citation_output",
                    "status_correct",
                    "answered",
                    "citations",
                    "irrelevant_citations",
                    "forbidden_pattern",
                    "needs_review",
                )
            },
            "embedding_seconds": predicted.embedding_seconds,
            "retrieval_seconds": predicted.retrieval_seconds,
            "answer_seconds": predicted.answer_seconds,
            "total_seconds": predicted.total_seconds,
            "embedding_input_tokens": predicted.embedding_input_tokens,
            "answer_input_tokens": answer_metrics["input_tokens"],
            "answer_output_tokens": answer_metrics["output_tokens"],
            "answer_total_tokens": answer_metrics["total_tokens"],
        }
        return Score(
            correct,
            metrics,
            {
                "attribution": attribution,
                "retrieved_evidence": retrieved_evidence,
                "cited": [
                    None
                    if hit is None
                    else {"entry": hit.entry, "ordinal": hit.ordinal, "rank": hit.rank}
                    for hit in predicted.cited
                ],
                "answer": answer_details,
            },
        )

    def aggregate(self, scores: list[Score]) -> dict:
        metrics = [s.metrics for s in scores]
        answerable = [s for s in scores if s.metrics["expected_answered"]]
        refusals = [s for s in scores if not s.metrics["expected_answered"]]
        attributions = dict.fromkeys(
            ("none", "retrieval", "generation", "retrieval_safety"), 0
        )
        for s in scores:
            key = s.details["attribution"]
            attributions[key] = attributions.get(key, 0) + 1
        result = {
            "scored_cases": len(scores),
            "correct_cases": sum(s.correct for s in scores),
            "e2e_answer_accuracy": _mean(int(s.correct) for s in scores),
            "retrieval_success_rate": _mean(
                m["retrieval_success"] for m in metrics if m["expected_answered"]
            ),
            "answer_status_accuracy": _mean(m["status_correct"] for m in metrics),
            "answered_accuracy": _mean(int(s.correct) for s in answerable),
            "refusal_accuracy": _mean(int(s.correct) for s in refusals),
            "valid_output_rate": _mean(
                m["valid"] for m in metrics if m["answer_called"]
            ),
            "invalid_outputs": sum(m["invalid_output"] for m in metrics),
            "unknown_citation_outputs": sum(
                m["unknown_citation_output"] for m in metrics
            ),
            "invalid_citations": sum(m["invalid_citations"] for m in metrics),
            "citations_cover_evidence_rate": _mean(
                m["citations_cover_evidence"] for m in metrics
            ),
            "irrelevant_citations": sum(m["irrelevant_citations"] for m in metrics),
            "answered_from_zero_evidence": sum(
                m["answered_from_zero_evidence"] for m in metrics
            ),
            "zero_source_insufficient": sum(
                m["zero_source_insufficient"] for m in metrics
            ),
            "answer_calls": sum(m["answer_called"] for m in metrics),
            "forbidden_pattern_cases": sum(m["forbidden_pattern"] for m in metrics),
            "needs_review_cases": sum(m["needs_review"] for m in metrics),
            **{key: sum(m[key] for m in metrics) for key in SAFETY_KEYS},
            **{
                f"attribution_{key}": value
                for key, value in sorted(attributions.items())
            },
        }
        for key in (
            "total_seconds",
            "embedding_seconds",
            "retrieval_seconds",
            "answer_seconds",
        ):
            samples = sorted(m[key] for m in metrics if m[key] is not None)
            result[f"{key}_mean"] = _mean(samples)
            result[f"{key}_p95"] = percentile(samples, 0.95)
        for key in (
            "embedding_input_tokens",
            "answer_input_tokens",
            "answer_output_tokens",
            "answer_total_tokens",
        ):
            counts = [m[key] for m in metrics if m[key] is not None]
            result[key] = sum(counts) if counts else None
        return result


def _as_retrieval_expectation(expected: RagExpectation):
    from lifeos_ai_evals.evaluators.journal_retrieval.model import RetrievalExpectation

    return RetrievalExpectation(bool(expected.evidence), False, expected.evidence)
