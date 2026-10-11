"""AI-005.1 retrieval promotion gates (`journal-retrieval-promotion-v1`).

Declared and frozen with the holdout, BEFORE any retrieval-v2 design or live run
(docs/tasks/ai/AI-005.1.md section 6); never changed after results.

Compares two exported runs of `journal_retrieval` on the SAME frozen dataset: the
reference must be the AI-004 production control (`journal-retrieval-v1`), the candidate
must differ from it only by its retrieval policy (`journal-retrieval-v2`). Both runs
use the frozen `journal-retrieval-scorer-v1`. Each gate is pass / fail; `all_gates_pass`
only says whether every gate held. There is no overall retrieval score.

Every dataset (development = the AI-005 v1 set, holdout = holdout-v1):
- both runs complete (no --case filter, zero execution errors) and ranked by their own
  verified SQL function, one call per case;
- candidate security counters (cross-user, deleted, wrong identity, unknown source,
  malformed ranking) are all 0;
- every safety-critical answerable case has relevant evidence in the candidate's top 8.

Development (the AI-005 absolute gates are not weakened):
- candidate hit_rate@8 >= 0.90, recall@8 >= 0.80, mrr >= 0.60;
- every safety-critical case the control misses is hit by the candidate;
- no material regression against the control: hit_rate@8 and recall@8 lose at most one
  answerable case's worth (1 / answerable cases), mrr at most 0.02.

Holdout:
- candidate hit_rate@8 >= control and recall@8 >= control (no tolerance);
- mrr at most 0.02 below the control;
- unanswerable noise not worse: mean irrelevant rows AND non-empty rate <= control;
- no evidence lost to filtering: zero answerable cases where the candidate returned
  fewer rows than the limit AND has lower recall@8 than the control.

Noise guard: fewer rows on unanswerable questions is never a reason to promote by
itself; it only counts when every answerable and safety gate also passes
(`all_gates_pass`).
"""

from lifeos_ai_evals.journal_memory.identity import RETRIEVAL_CONTROL

VERSION = "journal-retrieval-promotion-v1"
CANDIDATE_RETRIEVAL_VERSION = "journal-retrieval-v2"
CANDIDATE_FUNCTION = "search_journal_memory_v2"

# (name, version, canonical LF SHA-256) of the two frozen datasets.
DATASETS = {
    "development": (
        "synthetic-journal-retrieval",
        "1.0.0",
        "fda741ed8cfcf8cc254cbda0a0c67b335f249f14fe42e1ae436a3b1fe8cfdca5",
    ),
    "holdout": (
        "synthetic-journal-retrieval-holdout",
        "1.0.0",
        "d5ec8a99c1730bd3c1b9b34b4185b2c8b1042c519ab85dfe5777372c4d4d328c",
    ),
}
# The AI-005 journal-retrieval-acceptance-v1 minimums, unchanged.
DEVELOPMENT_MINIMUMS = {"hit_rate@8": 0.90, "recall@8": 0.80, "mrr": 0.60}
MRR_MATERIAL_REGRESSION = 0.02
SAFETY_KEYS = (
    "cross_user_results",
    "deleted_results",
    "wrong_identity_results",
    "unknown_source_results",
    "malformed_ranking",
)
_EPSILON = 1e-12


def _gate(name, rule, passed, reference=None, candidate=None, details=None) -> dict:
    return {
        "gate": name,
        "rule": rule,
        "status": "pass" if passed else "fail",
        "reference": reference,
        "candidate": candidate,
        "details": details or [],
    }


def dataset_role(run: dict) -> str | None:
    identity = run.get("dataset") or {}
    key = (identity.get("name"), identity.get("version"), identity.get("sha256"))
    return next((role for role, pinned in DATASETS.items() if pinned == key), None)


def _metrics(run: dict) -> dict:
    return run.get("aggregate_metrics") or {}


def _case_metrics(run: dict) -> dict[str, dict]:
    return {
        case["id"]: (case.get("score") or {}).get("metrics") or {}
        for case in run["cases"]
    }


def _complete(run: dict) -> bool:
    return (
        "case_filter" not in run["metadata"]
        and run["errors"] == 0
        and run["metadata"].get("scored_cases") == run["case_count"]
    )


def _function_verified(run: dict) -> tuple[bool, dict]:
    experiment = run["metadata"].get("experiment") or {}
    expected = (experiment.get("retrieval") or {}).get("function")
    retrieval_run = run["metadata"].get("retrieval_run") or {}
    database = retrieval_run.get("database") or {}
    value = {
        "function": database.get("function"),
        "function_verified": database.get("function_verified"),
        "search_calls": retrieval_run.get("search_calls"),
    }
    passed = (
        expected is not None
        and database.get("function") == expected
        and database.get("function_verified") is True
        and retrieval_run.get("search_calls") == run["metadata"].get("scored_cases")
    )
    return passed, value


def candidate_identity_problems(identity) -> list[str]:
    """The candidate must be the control with only its retrieval policy replaced."""
    if not isinstance(identity, dict):
        return ["no experiment identity"]
    problems = []
    if set(identity) != set(RETRIEVAL_CONTROL):
        problems.append("identity fields differ from the control")
    for key in sorted(set(RETRIEVAL_CONTROL) - {"system", "retrieval"}):
        if identity.get(key) != RETRIEVAL_CONTROL[key]:
            problems.append(f"{key} differs from the control")
    retrieval = identity.get("retrieval")
    if not isinstance(retrieval, dict):
        return [*problems, "no retrieval policy"]
    if retrieval.get("version") != CANDIDATE_RETRIEVAL_VERSION:
        problems.append(f"retrieval version is not {CANDIDATE_RETRIEVAL_VERSION}")
    if retrieval.get("function") != CANDIDATE_FUNCTION:
        problems.append(f"retrieval function is not {CANDIDATE_FUNCTION}")
    if retrieval.get("limit") != RETRIEVAL_CONTROL["retrieval"]["limit"]:
        problems.append("Ask context limit differs from the control")
    if retrieval == RETRIEVAL_CONTROL["retrieval"]:
        problems.append("retrieval policy equals the control")
    return problems


def _common_gates(reference: dict, candidate: dict) -> list[dict]:
    gates = []
    is_control = reference["metadata"].get("experiment") == RETRIEVAL_CONTROL
    gates.append(
        _gate(
            "reference_is_production_control",
            "run 0 is the frozen production control (journal-retrieval-v1)",
            is_control,
            None if is_control else "not the production control",
        )
    )
    problems = candidate_identity_problems(candidate["metadata"].get("experiment"))
    gates.append(
        _gate(
            "candidate_differs_only_by_retrieval_policy",
            "same embedding, chunking, database image and Ask limit as the control; "
            f"retrieval policy {CANDIDATE_RETRIEVAL_VERSION} = {CANDIDATE_FUNCTION}",
            not problems,
            details=problems,
        )
    )
    gates.append(
        _gate(
            "complete_coverage",
            "both runs: every case scored, zero execution errors, no --case filter",
            _complete(reference) and _complete(candidate),
            {
                "scored": reference["metadata"].get("scored_cases"),
                "errors": reference["errors"],
            },
            {
                "scored": candidate["metadata"].get("scored_cases"),
                "errors": candidate["errors"],
            },
        )
    )
    ref_ok, ref_value = _function_verified(reference)
    cand_ok, cand_value = _function_verified(candidate)
    gates.append(
        _gate(
            "policy_functions_verified",
            "each run was ranked only by its own migration-verified SQL function, "
            "one call per case",
            ref_ok and cand_ok,
            ref_value,
            cand_value,
        )
    )
    ref_metrics, cand_metrics = _metrics(reference), _metrics(candidate)
    for key in SAFETY_KEYS:
        gates.append(
            _gate(
                f"candidate_zero_{key}",
                f"candidate {key} = 0",
                cand_metrics.get(key) == 0,
                ref_metrics.get(key),
                cand_metrics.get(key),
            )
        )
    cand_cases = _case_metrics(candidate)
    misses = sorted(
        case_id
        for case_id, m in cand_cases.items()
        if m.get("safety_critical") and not m.get("hit@8")
    )
    critical = cand_metrics.get("safety_critical_cases")
    gates.append(
        _gate(
            "candidate_safety_critical_evidence_in_top_8",
            "every safety-critical answerable case has relevant evidence in the "
            "candidate's top 8",
            not misses
            and critical is not None
            and cand_metrics.get("safety_critical_hit@8") == critical,
            ref_metrics.get("safety_critical_hit@8"),
            cand_metrics.get("safety_critical_hit@8"),
            misses,
        )
    )
    return gates


def _at_least(value, threshold) -> bool:
    return value is not None and threshold is not None and value >= threshold - _EPSILON


def _development_gates(reference: dict, candidate: dict) -> list[dict]:
    ref, cand = _metrics(reference), _metrics(candidate)
    gates = [
        _gate(
            f"candidate_{key}_min",
            f"candidate {key} >= {minimum} (AI-005 absolute gate, unchanged)",
            _at_least(cand.get(key), minimum),
            ref.get(key),
            cand.get(key),
        )
        for key, minimum in DEVELOPMENT_MINIMUMS.items()
    ]
    ref_cases, cand_cases = _case_metrics(reference), _case_metrics(candidate)
    control_misses = sorted(
        case_id
        for case_id, m in ref_cases.items()
        if m.get("safety_critical") and not m.get("hit@8")
    )
    unfixed = [c for c in control_misses if not cand_cases.get(c, {}).get("hit@8")]
    gates.append(
        _gate(
            "candidate_fixes_control_safety_critical_misses",
            "every safety-critical case the control misses in its top 8 is hit by the "
            "candidate",
            not unfixed,
            control_misses,
            [c for c in control_misses if c not in unfixed],
            unfixed,
        )
    )
    answerable = cand.get("answerable_cases") or 0
    one_case = 1.0 / answerable if answerable else None
    for key in ("hit_rate@8", "recall@8"):
        floor = (
            ref.get(key) - one_case if ref.get(key) is not None and one_case else None
        )
        gates.append(
            _gate(
                f"no_material_regression_{key}",
                f"candidate {key} >= control - 1/answerable (at most one answerable "
                "case's worth lost)",
                _at_least(cand.get(key), floor),
                ref.get(key),
                cand.get(key),
            )
        )
    gates.append(_mrr_gate(ref, cand))
    return gates


def _mrr_gate(ref: dict, cand: dict) -> dict:
    floor = (
        ref.get("mrr") - MRR_MATERIAL_REGRESSION if ref.get("mrr") is not None else None
    )
    return _gate(
        "no_material_regression_mrr",
        f"candidate mrr >= control mrr - {MRR_MATERIAL_REGRESSION}",
        _at_least(cand.get("mrr"), floor),
        ref.get("mrr"),
        cand.get("mrr"),
    )


def evidence_lost_to_filtering(reference: dict, candidate: dict) -> list[str]:
    """Answerable cases where the candidate returned fewer rows than the limit (so rows
    were filtered, not outranked) AND found fewer evidence spans in its top 8."""
    limit = RETRIEVAL_CONTROL["retrieval"]["limit"]
    ref_cases, cand_cases = _case_metrics(reference), _case_metrics(candidate)
    lost = []
    for case_id, cand in cand_cases.items():
        ref = ref_cases.get(case_id, {})
        if not cand.get("answerable") or cand.get("recall@8") is None:
            continue
        if (
            cand.get("result_count", limit) < limit
            and ref.get("recall@8") is not None
            and cand["recall@8"] < ref["recall@8"] - _EPSILON
        ):
            lost.append(case_id)
    return sorted(lost)


def _holdout_gates(reference: dict, candidate: dict) -> list[dict]:
    ref, cand = _metrics(reference), _metrics(candidate)
    gates = [
        _gate(
            f"{key}_not_below_control",
            f"candidate {key} >= control {key} (no tolerance)",
            _at_least(cand.get(key), ref.get(key)),
            ref.get(key),
            cand.get(key),
        )
        for key in ("hit_rate@8", "recall@8")
    ]
    gates.append(_mrr_gate(ref, cand))
    noise_keys = ("unanswerable_mean_irrelevant_context", "unanswerable_nonempty_rate")
    gates.append(
        _gate(
            "unanswerable_noise_not_worse",
            "candidate mean irrelevant rows and non-empty rate on unanswerable "
            "questions <= control",
            all(
                cand.get(key) is not None
                and ref.get(key) is not None
                and cand[key] <= ref[key] + _EPSILON
                for key in noise_keys
            ),
            {key: ref.get(key) for key in noise_keys},
            {key: cand.get(key) for key in noise_keys},
        )
    )
    lost = evidence_lost_to_filtering(reference, candidate)
    gates.append(
        _gate(
            "no_evidence_lost_to_filtering",
            "zero answerable cases where the candidate returned fewer rows than the "
            "limit and lower recall@8 than the control",
            not lost,
            None,
            len(lost),
            lost,
        )
    )
    return gates


def promotion(reference: dict, candidate: dict) -> dict:
    role = dataset_role(reference)
    if role is None or dataset_role(candidate) != role:
        gates = [
            _gate(
                "frozen_dataset",
                "both runs use the same pinned AI-005.1 dataset (development or "
                "holdout)",
                False,
                reference.get("dataset"),
                candidate.get("dataset"),
            )
        ]
    else:
        gates = _common_gates(reference, candidate)
        gates += (
            _development_gates(reference, candidate)
            if role == "development"
            else _holdout_gates(reference, candidate)
        )
    ref, cand = _metrics(reference), _metrics(candidate)
    diagnostics = {
        "dataset_role": role,
        "evidence_lost_to_filtering": evidence_lost_to_filtering(reference, candidate)
        if role
        else None,
        **{
            key: {"reference": ref.get(key), "candidate": cand.get(key)}
            for key in (
                "hit_rate@1",
                "hit_rate@3",
                "recall@1",
                "recall@3",
                "precision@8",
                "precision@8_all_cases_diagnostic",
                "mean_result_count",
                "min_result_count",
                "unanswerable_irrelevant_context",
            )
        },
    }
    return {
        "version": VERSION,
        "gates": gates,
        "all_gates_pass": all(item["status"] == "pass" for item in gates),
        "diagnostics": diagnostics,
        "note": (
            "Predeclared promotion gates, frozen before any live run; no overall "
            "retrieval score. Fewer rows on unanswerable questions only counts when "
            "every answerable and safety gate also passes."
        ),
    }
