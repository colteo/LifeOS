"""AI-006 final acceptance gates (`journal-rag-openai-final-acceptance-v1`).

Frozen with the candidate configs BEFORE any live OpenAI run (docs/tasks/ai/AI-006.md
section 6); never changed after a result exists. Absolute gates over ONE exported run of
the OpenAI final candidate; there is no reference run and no overall or combined score.

Each report is the frozen AI-005 acceptance of that evaluator (same gates, same
thresholds, never weakened) with only its identity gates aimed at the AI-006 candidate
instead of the production control, plus:

- every evaluator: the run is on one of the frozen datasets (by name, version and
  canonical SHA-256): retrieval and answer on the AI-005 development set AND the
  AI-005.1 / AI-005.2 holdout, end to end on the AI-005 set;
- retrieval: ranked by the candidate's verified `search_journal_memory_v2` (one call per
  case), and no relevant evidence lost to an identity or filter bug: zero answerable
  cases that returned fewer rows than the Ask limit (8) while missing evidence;
- answer: zero answerable cases refused (`refused_when_answerable == 0`), so the
  candidate cannot pass by over-refusing.

AI-006 accepts the candidate only when all five reports (retrieval development and
holdout, answer development and holdout, end to end) pass every gate.
"""

from lifeos_ai_evals.evaluators.journal_answer import acceptance as answer_acceptance
from lifeos_ai_evals.evaluators.journal_answer.promotion import FROZEN_DATASETS
from lifeos_ai_evals.evaluators.journal_rag import acceptance as rag_acceptance
from lifeos_ai_evals.evaluators.journal_retrieval import (
    acceptance as retrieval_acceptance,
)
from lifeos_ai_evals.evaluators.journal_retrieval.promotion import DATASETS
from lifeos_ai_evals.journal_memory import gates as g
from lifeos_ai_evals.journal_memory.openai_final import (
    ANSWER_FINAL,
    RAG_FINAL,
    RETRIEVAL_FINAL,
)

VERSION = "journal-rag-openai-final-acceptance-v1"
ASK_LIMIT = 8

RETRIEVAL_DATASETS = {pinned: role for role, pinned in DATASETS.items()}
ANSWER_DATASETS = dict(FROZEN_DATASETS)
RAG_DATASETS = {
    (
        "synthetic-journal-rag",
        "1.0.0",
        "bb8596c403d614f5163259aa5e0f10c1a6cfbb1ae529032590bfd462274848c6",
    ): "development",
}


def is_candidate(run: dict, identity: dict) -> bool:
    return run["metadata"].get("experiment") == identity


def _candidate_gate(run: dict, identity: dict) -> dict:
    found = run["metadata"].get("experiment")
    return g.gate(
        "openai_final_candidate",
        "the run is the frozen AI-006 OpenAI final candidate "
        "(experiments/*/openai-final.json)",
        found == identity,
        None if found == identity else "not the AI-006 candidate",
    )


def _dataset_gate(run: dict, frozen: dict) -> tuple[dict, str | None]:
    dataset = run.get("dataset") or {}
    role = frozen.get(
        (dataset.get("name"), dataset.get("version"), dataset.get("sha256"))
    )
    gate = g.gate(
        "frozen_dataset",
        "the run is on a frozen AI-005 / AI-005.1 / AI-005.2 dataset, unchanged",
        role is not None,
        {"name": dataset.get("name"), "role": role},
    )
    return gate, role


def _replace(gates: list[dict], name: str, replacement: dict) -> list[dict]:
    names = [gate["gate"] for gate in gates]
    if names.count(name) != 1:
        raise ValueError(f"AI-005 acceptance drift: no single {name} gate")
    return [replacement if gate["gate"] == name else gate for gate in gates]


def _report(gates: list[dict], diagnostics: dict, role: str | None) -> dict:
    report = g.report(
        VERSION,
        gates,
        diagnostics,
        "AI-006 absolute gates over one run of the OpenAI final candidate; no overall "
        "score. Acceptance needs all five reports to pass every gate.",
    )
    return {**report, "dataset_role": role}


def retrieval(run: dict) -> dict:
    base = retrieval_acceptance.acceptance(run)
    gates = _replace(
        base["gates"], "production_control", _candidate_gate(run, RETRIEVAL_FINAL)
    )
    retrieval_run = run["metadata"].get("retrieval_run") or {}
    database = retrieval_run.get("database") or {}
    function = RETRIEVAL_FINAL["retrieval"]["function"]
    gates = _replace(
        gates,
        "production_function_verified",
        g.gate(
            "candidate_function_verified",
            f"the disposable database has exactly the repository migrations and the "
            f"migration's {function}; every case was ranked by it",
            database.get("function") == function
            and database.get("function_verified") is True
            and retrieval_run.get("search_calls") == run["metadata"]["scored_cases"],
            {
                "function": database.get("function"),
                "function_verified": database.get("function_verified"),
                "search_calls": retrieval_run.get("search_calls"),
            },
        ),
    )
    lost = g.cases_where(
        run,
        lambda case: (
            (metrics := (case.get("score") or {}).get("metrics") or {}).get(
                "answerable"
            )
            and metrics.get("result_count", ASK_LIMIT) < ASK_LIMIT
            and metrics.get("recall@8", 1.0) < 1.0
        ),
    )
    dataset_gate, role = _dataset_gate(run, RETRIEVAL_DATASETS)
    gates = [
        *gates,
        dataset_gate,
        g.gate(
            "no_evidence_lost_to_filtering",
            "no answerable case returned fewer than 8 rows while missing evidence "
            "(an identity or filter bug, not ranking)",
            not lost,
            len(lost),
            lost,
        ),
    ]
    return _report(gates, base["diagnostics"], role)


def answer(run: dict) -> dict:
    base = answer_acceptance.acceptance(run)
    gates = _replace(
        base["gates"], "production_control", _candidate_gate(run, ANSWER_FINAL)
    )
    refused = run["aggregate_metrics"].get("refused_when_answerable")
    dataset_gate, role = _dataset_gate(run, ANSWER_DATASETS)
    gates = [
        *gates,
        dataset_gate,
        g.gate(
            "zero_refused_when_answerable",
            "no answerable case refused: the candidate cannot pass by over-refusing",
            refused == 0,
            refused,
            g.cases_where(
                run,
                lambda case: (
                    (metrics := (case.get("score") or {}).get("metrics") or {}).get(
                        "expected_answered"
                    )
                    and metrics.get("valid")
                    and not metrics.get("answered")
                ),
            ),
        ),
    ]
    return _report(gates, base["diagnostics"], role)


def rag(run: dict) -> dict:
    base = rag_acceptance.acceptance(run)
    gates = _replace(
        base["gates"], "production_control", _candidate_gate(run, RAG_FINAL)
    )
    dataset_gate, role = _dataset_gate(run, RAG_DATASETS)
    return _report([*gates, dataset_gate], base["diagnostics"], role)
