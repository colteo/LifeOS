"""Journal retrieval scorer (`journal-retrieval-scorer-v1`). Deterministic; no model
judge.

Relevance: a returned chunk is relevant when it is a labelled relevant chunk (corpus.py)
AND belongs to the searched user's active frozen corpus with exactly the frozen chunk
text.

Per answerable case (k in 1, 3, 8; "top k" = the first k returned rows):
- hit@k: some relevant chunk in the top k;
- recall@k: labelled evidence spans with a relevant chunk in the top k / evidence spans;
- precision@k: relevant rows in the top k / rows in the top k (None when nothing
  returned);
- reciprocal rank: 1 / rank of the first relevant row (0 when none is returned);
- arm attribution of relevant rows: vector-only, lexical-only, both (hybrid).

Unanswerable cases (V1 has NO similarity threshold, so a non-empty result is expected
behaviour, not a bug): nonempty flag and irrelevant-context count, diagnostics only.

Absolute safety checks on every case (hard failures): cross-user rows, deleted-entry
rows, foreign-identity rows, rows not from the frozen corpus (unknown entry or different
text), malformed ranking metadata (ranks not 1..n, a row with neither arm, component
ranks outside the candidate depth, scores inconsistent with the documented RRF formula
or increasing, duplicate chunks, more rows than the limit).

A case is correct when it is safety-clean and, if answerable, has a relevant row in the
top 8.
"""

import math

from lifeos_ai_evals.core.engine import Score
from lifeos_ai_evals.evaluators.journal_retrieval.model import (
    RetrievalExpectation,
    RetrievalOutput,
)
from lifeos_ai_evals.journal_memory.corpus import SEARCH_USER, ChunkRef
from lifeos_ai_evals.journal_memory.identity import RETRIEVAL
from lifeos_ai_evals.production.runtime import percentile

VERSION = "journal-retrieval-scorer-v1"
KS = (1, 3, 8)
SAFETY_KEYS = (
    "cross_user_results",
    "deleted_results",
    "wrong_identity_results",
    "unknown_source_results",
    "malformed_ranking",
)


def ranking_violations(output: RetrievalOutput) -> list[str]:
    hits = output.hits
    depth_vector = RETRIEVAL["vector_candidates"]
    depth_lexical = RETRIEVAL["lexical_candidates"]
    k = RETRIEVAL["rrf_k"]
    problems = []
    if len(hits) > output.limit:
        problems.append("more_rows_than_limit")
    if [hit.rank for hit in hits] != list(range(1, len(hits) + 1)):
        problems.append("ranks_not_contiguous")
    if len({(hit.entry, hit.ordinal) for hit in hits}) != len(hits):
        problems.append("duplicate_chunk")
    for hit in hits:
        if hit.vector_rank is None and hit.lexical_rank is None:
            problems.append(f"rank_{hit.rank}_no_arm")
            continue
        if hit.vector_rank is not None and not 1 <= hit.vector_rank <= depth_vector:
            problems.append(f"rank_{hit.rank}_vector_rank_out_of_depth")
        if hit.lexical_rank is not None and not 1 <= hit.lexical_rank <= depth_lexical:
            problems.append(f"rank_{hit.rank}_lexical_rank_out_of_depth")
        expected = sum(
            1.0 / (k + rank) for rank in (hit.vector_rank, hit.lexical_rank) if rank
        )
        if not math.isfinite(hit.score) or abs(hit.score - expected) > 1e-9:
            problems.append(f"rank_{hit.rank}_score_inconsistent")
    if any(a.score < b.score - 1e-12 for a, b in zip(hits, hits[1:], strict=False)):
        problems.append("scores_increase")
    return problems


def is_relevant(hit, relevant: frozenset[ChunkRef]) -> bool:
    return (
        hit.user == SEARCH_USER
        and hit.lifecycle == "active"
        and hit.text_matches
        and ChunkRef(hit.entry, hit.ordinal) in relevant
    )


def _mean(values):
    values = [value for value in values if value is not None]
    return sum(values) / len(values) if values else None


class JournalRetrievalScorer:
    configuration = {
        "scorer": VERSION,
        "ks": list(KS),
        "relevance": "labelled source span -> production chunk map (corpus.py)",
        "unanswerable_nonempty": "diagnostic (V1 has no similarity threshold)",
        "retrieval_policy": dict(RETRIEVAL),
    }

    def score(
        self, expected: RetrievalExpectation, predicted: RetrievalOutput
    ) -> Score:
        hits = predicted.hits
        relevant = expected.relevant_chunks()
        flags = [is_relevant(hit, relevant) for hit in hits]
        safety = {
            "cross_user_results": sum(
                hit.user is not None and hit.user != SEARCH_USER for hit in hits
            ),
            "deleted_results": sum(hit.lifecycle == "deleted" for hit in hits),
            "wrong_identity_results": sum(
                hit.lifecycle == "foreign_identity" for hit in hits
            ),
            "unknown_source_results": sum(
                hit.entry is None or not hit.text_matches for hit in hits
            ),
        }
        violations = ranking_violations(predicted)
        safety["malformed_ranking"] = 1 if violations else 0
        metrics = {
            "answerable": int(expected.answerable),
            "safety_critical": int(expected.safety_critical),
            "result_count": len(hits),
            "relevant_results": sum(flags),
            "irrelevant_results": len(hits) - sum(flags),
            "embedding_seconds": predicted.embedding_seconds,
            "retrieval_seconds": predicted.retrieval_seconds,
            "embedding_input_tokens": predicted.embedding_input_tokens,
            **safety,
        }
        first = next((index + 1 for index, flag in enumerate(flags) if flag), None)
        if expected.answerable:
            for k in KS:
                top = hits[:k]
                found = sum(
                    any(
                        is_relevant(hit, frozenset(item.relevant_chunks)) for hit in top
                    )
                    for item in expected.evidence
                )
                metrics[f"hit@{k}"] = int(any(flags[:k]))
                metrics[f"recall@{k}"] = found / len(expected.evidence)
                metrics[f"precision@{k}"] = sum(flags[:k]) / len(top) if top else None
            metrics["reciprocal_rank"] = 1.0 / first if first else 0.0
            in_context = [hit for hit, flag in zip(hits, flags, strict=True) if flag]
            metrics["vector_only_relevant"] = sum(
                h.vector_rank is not None and h.lexical_rank is None for h in in_context
            )
            metrics["lexical_only_relevant"] = sum(
                h.lexical_rank is not None and h.vector_rank is None for h in in_context
            )
            metrics["hybrid_relevant"] = sum(
                h.vector_rank is not None and h.lexical_rank is not None
                for h in in_context
            )
        else:
            metrics["nonempty"] = int(bool(hits))
            metrics["irrelevant_context"] = len(hits)
        clean = not any(safety.values())
        correct = clean and (not expected.answerable or bool(metrics["hit@8"]))
        return Score(
            correct,
            metrics,
            {
                "first_relevant_rank": first,
                "ranking_violations": violations,
                "returned": [
                    {
                        "rank": hit.rank,
                        "entry": hit.entry,
                        "ordinal": hit.ordinal,
                        "relevant": flag,
                        "vector_rank": hit.vector_rank,
                        "lexical_rank": hit.lexical_rank,
                    }
                    for hit, flag in zip(hits, flags, strict=True)
                ],
            },
        )

    def aggregate(self, scores: list[Score]) -> dict:
        answerable = [s.metrics for s in scores if s.metrics["answerable"]]
        unanswerable = [s.metrics for s in scores if not s.metrics["answerable"]]
        result = {
            "scored_cases": len(scores),
            "answerable_cases": len(answerable),
            "unanswerable_cases": len(unanswerable),
            "correct_cases": sum(s.correct for s in scores),
        }
        for k in KS:
            result[f"hit_rate@{k}"] = _mean(m[f"hit@{k}"] for m in answerable)
        for k in KS:
            result[f"recall@{k}"] = _mean(m[f"recall@{k}"] for m in answerable)
        for k in KS:
            result[f"precision@{k}"] = _mean(m[f"precision@{k}"] for m in answerable)
        result["mrr"] = _mean(m["reciprocal_rank"] for m in answerable)
        result["safety_critical_cases"] = sum(m["safety_critical"] for m in answerable)
        result["safety_critical_hit@8"] = sum(
            m["hit@8"] for m in answerable if m["safety_critical"]
        )
        for key in ("vector_only_relevant", "lexical_only_relevant", "hybrid_relevant"):
            result[f"{key}_hits"] = sum(m[key] for m in answerable)
        result["unanswerable_nonempty_rate"] = _mean(
            m["nonempty"] for m in unanswerable
        )
        result["unanswerable_irrelevant_context"] = sum(
            m["irrelevant_context"] for m in unanswerable
        )
        result["unanswerable_mean_irrelevant_context"] = _mean(
            m["irrelevant_context"] for m in unanswerable
        )
        all_metrics = [s.metrics for s in scores]
        result["precision@8_all_cases_diagnostic"] = _mean(
            m["relevant_results"] / m["result_count"] if m["result_count"] else None
            for m in all_metrics
        )
        result["mean_result_count"] = _mean(m["result_count"] for m in all_metrics)
        result["min_result_count"] = (
            min(m["result_count"] for m in all_metrics) if all_metrics else None
        )
        for key in SAFETY_KEYS:
            result[key] = sum(m[key] for m in all_metrics)
        for key in ("embedding_seconds", "retrieval_seconds"):
            samples = sorted(m[key] for m in all_metrics)
            result[f"{key}_mean"] = _mean(samples)
            result[f"{key}_p50"] = percentile(samples, 0.5)
            result[f"{key}_p95"] = percentile(samples, 0.95)
            result[f"{key}_max"] = samples[-1] if samples else None
        tokens = [
            m["embedding_input_tokens"]
            for m in all_metrics
            if m["embedding_input_tokens"] is not None
        ]
        result["query_embedding_input_tokens"] = sum(tokens) if tokens else None
        return result
