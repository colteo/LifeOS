"""Holdout-aware Action Agent prompt promotion gates (action-agent-acceptance-v2); see
docs/tasks/ai/AI-003.1.md §6.

Strictly additive: every action-agent-acceptance-v1 gate is reported unchanged, followed
by the AI-003.1 gates. One report covers one comparison on one frozen dataset. A prompt
is promotable only when the development-set report AND the holdout report both pass
every gate, and the flagged cases have been reviewed. Each gate is reported separately;
there is no overall score and no winner.
"""

from pathlib import Path

from lifeos_ai_evals.core.experiments import ExperimentVariant
from lifeos_ai_evals.evaluators.action_agent import acceptance as v1

VERSION = "action-agent-acceptance-v2"
CONTROL = (
    Path(__file__).resolve().parents[4] / "experiments/action_agent/control-v1.json"
)
DEVELOPMENT = "development"
HOLDOUT = "holdout"
# The frozen benchmark: (name, version, canonical sha256) -> role. Any other dataset is
# not a promotion benchmark.
FROZEN_DATASETS = {
    (
        "synthetic-action-agent-scenarios",
        "1.0.0",
        "8635cd73e166ebef04898f692af341639bbfaf05fcd4aeaa7b63c01d78442d9f",
    ): DEVELOPMENT,
    (
        "synthetic-action-agent-holdout",
        "1.0.0",
        "72540284d6df5bb0e5dfa6cdde22fd977464c4faa5a90bc7c3b070b07c48025b",
    ): HOLDOUT,
}


def role(run: dict) -> str | None:
    dataset = run["dataset"]
    return FROZEN_DATASETS.get((dataset["name"], dataset["version"], dataset["sha256"]))


def _correct(run: dict, decision: str | None = None) -> int:
    return sum(
        case["score"]["metrics"]["decision_correct"]
        for case in run["cases"]
        if case.get("score")
        and (decision is None or case["expected"]["decision"] == decision)
    )


def _status(passed: bool | None) -> str:
    return "not_evaluable" if passed is None else "pass" if passed else "fail"


def promotion(reference: dict, candidate: dict) -> dict:
    dataset_role = role(candidate) if role(candidate) == role(reference) else None
    control = ExperimentVariant.load(CONTROL).identity()
    left = reference["metadata"].get("experiment") or {}
    right = candidate["metadata"].get("experiment") or {}
    changed = sorted(
        key for key in left.keys() | right.keys() if left.get(key) != right.get(key)
    )
    metrics, before = candidate["aggregate_metrics"], reference["aggregate_metrics"]

    gates = list(v1.acceptance(reference, candidate)["gates"])
    gates.append(
        v1._gate(
            "frozen_benchmark_dataset",
            "both runs use the frozen v1 development set or the frozen holdout",
            _status(dataset_role is not None),
            details=[dataset_role] if dataset_role else [],
        )
    )
    gates.append(
        v1._gate(
            "reference_is_production_control",
            "the reference run is experiments/action_agent/control-v1.json",
            _status(left == control),
        )
    )
    gates.append(
        v1._gate(
            "candidate_changes_only_prompt_version",
            "candidate identity equals the reference except prompt_version",
            _status(changed == ["prompt_version"]),
            details=changed,
        )
    )
    gates.append(
        v1._gate(
            "no_failed_or_invalid_runs",
            "candidate failed_run_rate == 0 and invalid_output_run_rate == 0",
            _status(
                metrics.get("failed_run_rate") == 0
                and metrics.get("invalid_output_run_rate") == 0
            ),
            [before.get("failed_run_rate"), before.get("invalid_output_run_rate")],
            [metrics.get("failed_run_rate"), metrics.get("invalid_output_run_rate")],
        )
    )
    correct_before, correct_after = _correct(reference), _correct(candidate)
    improves = {
        DEVELOPMENT: correct_after > correct_before,
        HOLDOUT: correct_after >= correct_before,
    }.get(dataset_role)
    gates.append(
        v1._gate(
            "terminal_decision_accuracy",
            "development: candidate correct decisions > reference; "
            "holdout: candidate >= reference",
            _status(improves),
            before.get("terminal_decision_accuracy"),
            metrics.get("terminal_decision_accuracy"),
        )
    )
    if dataset_role == HOLDOUT:
        no_action_before = _correct(reference, "no_action")
        no_action_after = _correct(candidate, "no_action")
        gates.append(
            v1._gate(
                "no_action_not_worse",
                "holdout: correct decisions on no_action-labelled cases do not decline",
                _status(no_action_after >= no_action_before),
                no_action_before,
                no_action_after,
            )
        )
    bounds_before = before.get("proposal_bounds_rate")
    bounds_after = metrics.get("proposal_bounds_rate")
    gates.append(
        v1._gate(
            "proposal_bounds_rate_not_worse",
            "candidate proposal_bounds_rate >= reference (not evaluable when either "
            "run has no bounded proposal)",
            _status(
                None
                if bounds_before is None or bounds_after is None
                else bounds_after >= bounds_before
            ),
            bounds_before,
            bounds_after,
        )
    )
    return {
        "version": VERSION,
        "dataset_role": dataset_role,
        "gates": gates,
        "all_gates_pass": all(gate["status"] == "pass" for gate in gates),
        "note": (
            "Promotion needs this report to pass on BOTH the development set and the "
            "holdout, plus review of flagged cases. Other metrics are compared one by "
            "one; no overall score."
        ),
    }
