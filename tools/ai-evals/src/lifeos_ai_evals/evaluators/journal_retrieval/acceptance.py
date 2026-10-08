"""Journal retrieval acceptance gates (`journal-retrieval-acceptance-v1`).

Declared before any live run (docs/tasks/ai/AI-005.md section 9); never weakened after
results.

Quality minimums over the 32 answerable cases of the frozen v1 dataset:
- hit_rate@8 >= 0.90 (at most 3 of 32 missed): Ask sends the top 8 chunks to the answer
  model, so a question whose evidence is not among them cannot be answered. The
  tolerance exists because hard lexical, inflection and cross-language paraphrase cases
  are included on purpose;
- recall@8 >= 0.80: multi-evidence questions need most of their evidence spans in the
  context, not just one;
- mrr >= 0.60: on average the first relevant chunk is at rank 1 or 2, not buried among
  the other 7 passages.

Precision and unanswerable non-empty retrieval are DIAGNOSTICS, not gates: V1 has no
similarity threshold, so it always returns up to 8 rows. They are evidence for a future
AI-005.1 threshold experiment.
"""

from lifeos_ai_evals.journal_memory import gates as g
from lifeos_ai_evals.journal_memory.identity import RETRIEVAL_CONTROL

VERSION = "journal-retrieval-acceptance-v1"
THRESHOLDS = {"hit_rate@8": 0.90, "recall@8": 0.80, "mrr": 0.60}


def acceptance(run: dict) -> dict:
    metrics = run["aggregate_metrics"]
    retrieval_run = run["metadata"].get("retrieval_run") or {}
    database = retrieval_run.get("database") or {}
    critical_misses = g.cases_where(
        run,
        lambda case: (
            (case.get("score") or {}).get("metrics", {}).get("safety_critical")
            and not case["score"]["metrics"].get("hit@8")
        ),
    )
    critical_errors = g.cases_where(
        run,
        lambda case: case["status"] == "error" and case["expected"]["safety_critical"],
    )
    gates = [
        g.control_gate(run, RETRIEVAL_CONTROL),
        g.coverage_gate(run),
        g.gate(
            "production_function_verified",
            "the disposable database has exactly the repository migrations and the "
            "migration's search_journal_memory_v1; every case was ranked by it",
            database.get("function_verified") is True
            and retrieval_run.get("search_calls") == run["metadata"]["scored_cases"],
            {
                "function_verified": database.get("function_verified"),
                "search_calls": retrieval_run.get("search_calls"),
            },
        ),
        g.zero_gate(run, "cross_user_results", "no row of another user, ever"),
        g.zero_gate(run, "deleted_results", "no row of a hard-deleted entry"),
        g.zero_gate(run, "wrong_identity_results", "no row of another index identity"),
        g.zero_gate(
            run, "unknown_source_results", "every row is from the frozen corpus"
        ),
        g.zero_gate(run, "malformed_ranking", "ranking metadata is well formed"),
        g.gate(
            "safety_critical_evidence_in_top_8",
            "every safety-critical answerable case has a relevant chunk in the top 8",
            not critical_misses
            and not critical_errors
            and metrics.get("safety_critical_cases") is not None
            and metrics.get("safety_critical_hit@8")
            == metrics.get("safety_critical_cases"),
            {
                "cases": metrics.get("safety_critical_cases"),
                "hit@8": metrics.get("safety_critical_hit@8"),
            },
            critical_misses + critical_errors,
        ),
        g.minimum_gate(
            run,
            "hit_rate@8",
            THRESHOLDS["hit_rate@8"],
            "evidence reaches the Ask context",
        ),
        g.minimum_gate(
            run,
            "recall@8",
            THRESHOLDS["recall@8"],
            "most evidence spans reach the context",
        ),
        g.minimum_gate(run, "mrr", THRESHOLDS["mrr"], "relevant evidence ranks early"),
    ]
    diagnostics = {
        key: metrics.get(key)
        for key in (
            "unanswerable_nonempty_rate",
            "unanswerable_irrelevant_context",
            "unanswerable_mean_irrelevant_context",
            "precision@8",
            "precision@8_all_cases_diagnostic",
        )
    }
    return g.report(
        VERSION,
        gates,
        diagnostics,
        "Absolute gates over one run of the production control; no overall score. "
        "Unanswerable non-empty retrieval is a diagnostic: V1 has no similarity "
        "threshold.",
    )
