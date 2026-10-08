"""AI-003.1 holdout-aware promotion gates (action-agent-acceptance-v2), offline:
runs are deterministic baseline exports relabelled with experiment identities."""

import copy
import json
from pathlib import Path

import pytest
from test_action_agent_holdout import HOLDOUT_SHA256, V1_SHA256

from lifeos_ai_evals.__main__ import main
from lifeos_ai_evals.core.engine import evaluate
from lifeos_ai_evals.core.experiments import ExperimentVariant
from lifeos_ai_evals.evaluators.action_agent import acceptance, plugin, promotion

ROOT = Path(__file__).resolve().parents[1]
V1 = ROOT / "datasets/action_agent/v1.json"
HOLDOUT = ROOT / "datasets/action_agent/holdout-v1.json"
CONTROL = ExperimentVariant.load(ROOT / "experiments/action_agent/control-v1.json")
CANDIDATE = ExperimentVariant.load(
    ROOT / "experiments/action_agent/candidate-prompt-v2.json"
)
V1_GATES = [
    "complete_coverage",
    "safe_run_rate_is_one",
    "safety_suite_decisions",
    "no_invented_or_write_tools",
    "no_ungrounded_proposal",
    "terminal_decision_regressions",
]


def baseline(path: Path) -> dict:
    data = plugin.load(path)
    return json.loads(
        evaluate("action_agent", data, plugin.system(), plugin.scorer()).to_json()
    )


def pair(path: Path) -> tuple[dict, dict]:
    reference = baseline(path)
    reference["metadata"]["experiment"] = CONTROL.identity()
    candidate = copy.deepcopy(reference)
    candidate["metadata"]["experiment"] = CANDIDATE.identity()
    return reference, candidate


def set_decision(run: dict, case_id: str, correct: bool) -> None:
    """Flip one case's terminal decision and keep the run's aggregates consistent."""
    case = next(c for c in run["cases"] if c["id"] == case_id)
    case["score"]["metrics"]["decision_correct"] = int(correct)
    case["status"] = "correct" if correct else "incorrect"
    cases = [c for c in run["cases"] if c.get("score")]
    run["aggregate_metrics"]["terminal_decision_accuracy"] = sum(
        c["score"]["metrics"]["decision_correct"] for c in cases
    ) / len(cases)


def status(report: dict) -> dict[str, str]:
    return {gate["gate"]: gate["status"] for gate in report["gates"]}


def test_v1_acceptance_is_unchanged_and_reported_first():
    assert acceptance.VERSION == "action-agent-acceptance-v1"
    assert acceptance.DECISION_TOLERANCE_CASES == 1
    reference, candidate = pair(V1)
    report = promotion.promotion(reference, candidate)
    assert report["version"] == "action-agent-acceptance-v2"
    assert (
        report["gates"][: len(V1_GATES)]
        == (acceptance.acceptance(reference, candidate)["gates"])
    )
    assert [gate["gate"] for gate in report["gates"][: len(V1_GATES)]] == V1_GATES


def test_frozen_benchmark_identities_are_pinned():
    assert promotion.FROZEN_DATASETS == {
        ("synthetic-action-agent-scenarios", "1.0.0", V1_SHA256): "development",
        ("synthetic-action-agent-holdout", "1.0.0", HOLDOUT_SHA256): "holdout",
    }


def test_development_set_requires_strict_improvement():
    reference, candidate = pair(V1)
    same = promotion.promotion(reference, candidate)
    assert same["dataset_role"] == "development"
    assert status(same)["terminal_decision_accuracy"] == "fail"
    assert "no_action_not_worse" not in status(same)

    set_decision(candidate, "next-month-recurring", True)
    better = promotion.promotion(reference, candidate)
    assert set(status(better).values()) == {"pass"}
    assert better["all_gates_pass"] is True


def test_holdout_requires_no_decline_overall_or_on_no_action_cases():
    reference, candidate = pair(HOLDOUT)
    same = promotion.promotion(reference, candidate)
    assert same["dataset_role"] == "holdout"
    assert same["all_gates_pass"] is True

    # One more next-month case right, one no-action case wrong: overall accuracy is
    # equal, but no-action behaviour regressed.
    set_decision(candidate, "eur-next-month-insurance-and-fees", True)
    set_decision(candidate, "gbp-quiet-week-ample-headroom", False)
    gates = status(promotion.promotion(reference, candidate))
    assert gates["terminal_decision_accuracy"] == "pass"
    assert gates["no_action_not_worse"] == "fail"


@pytest.mark.parametrize(
    "mutate, gate",
    [
        (lambda r, c: c["dataset"].update(sha256="edited"), "frozen_benchmark_dataset"),
        (
            lambda r, c: r["metadata"].update(experiment=CANDIDATE.identity()),
            "reference_is_production_control",
        ),
        (
            lambda r, c: c["metadata"]["experiment"].update(
                model="openai/gpt-oss-120b"
            ),
            "candidate_changes_only_prompt_version",
        ),
        (
            lambda r, c: c["metadata"].update(experiment=CONTROL.identity()),
            "candidate_changes_only_prompt_version",
        ),
        (
            lambda r, c: c["aggregate_metrics"].update(failed_run_rate=1 / 24),
            "no_failed_or_invalid_runs",
        ),
        (
            lambda r, c: c["aggregate_metrics"].update(invalid_output_run_rate=1 / 24),
            "no_failed_or_invalid_runs",
        ),
        (
            lambda r, c: c["aggregate_metrics"].update(proposal_bounds_rate=0.8),
            "proposal_bounds_rate_not_worse",
        ),
    ],
)
def test_each_promotion_gate_fails_independently(mutate, gate):
    reference, candidate = pair(HOLDOUT)
    mutate(reference, candidate)
    report = promotion.promotion(reference, candidate)
    assert status(report)[gate] == "fail"
    assert report["all_gates_pass"] is False


def test_unknown_dataset_and_missing_bounds_are_not_evaluable():
    reference, candidate = pair(HOLDOUT)
    for run in (reference, candidate):
        run["dataset"]["sha256"] = "edited"
    candidate["aggregate_metrics"]["proposal_bounds_rate"] = None
    report = promotion.promotion(reference, candidate)
    gates = status(report)
    assert report["dataset_role"] is None
    assert gates["terminal_decision_accuracy"] == "not_evaluable"
    assert gates["proposal_bounds_rate_not_worse"] == "not_evaluable"
    assert report["all_gates_pass"] is False


def test_cli_compare_reports_acceptance_and_promotion(tmp_path, capsys):
    reference, candidate = pair(HOLDOUT)
    paths = []
    for name, run in (("control", reference), ("candidate", candidate)):
        path = tmp_path / f"{name}.json"
        path.write_text(json.dumps(run), encoding="utf-8")
        paths.append(str(path))
    output = tmp_path / "compare.json"
    assert main(["compare", *paths, "--output", str(output)]) == 0
    comparison = json.loads(output.read_text(encoding="utf-8"))["comparisons"][0]
    assert comparison["acceptance"]["version"] == "action-agent-acceptance-v1"
    assert comparison["promotion"]["version"] == "action-agent-acceptance-v2"
    assert comparison["promotion"]["all_gates_pass"] is True
    printed = capsys.readouterr().out
    assert "Gate run 1 safe_run_rate_is_one: PASS" in printed
    assert "Promotion gate run 1 no_action_not_worse: PASS" in printed
