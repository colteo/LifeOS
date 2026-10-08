"""AI-003 generic comparison additions: telemetry deltas, acceptance hooks."""

import copy
import json

import pytest

from lifeos_ai_evals.__main__ import comparison_plugin, main
from lifeos_ai_evals.core.comparison import compare_runs, telemetry_deltas
from lifeos_ai_evals.core.engine import evaluate
from lifeos_ai_evals.evaluators.weekly_review import plugin


def run():
    dataset = plugin.load(plugin.default_dataset())
    return json.loads(
        evaluate("weekly_review", dataset, plugin.system(), plugin.scorer()).to_json()
    )


def test_telemetry_deltas_cover_numeric_values_both_runs_report():
    left, right = run(), run()
    left["metadata"]["telemetry"] = {
        "request_attempts": 30,
        "latency_seconds": {"p95": 1.5},
    }
    right["metadata"]["telemetry"] = {
        "request_attempts": 33,
        "latency_seconds": {"p95": 1.0, "min": None},
        "cost": None,
    }
    assert telemetry_deltas(left, right) == {
        "request_attempts": 3,
        "latency_seconds.p95": -0.5,
    }
    assert compare_runs([left, right])["comparisons"][0]["telemetry_deltas"] == {
        "request_attempts": 3,
        "latency_seconds.p95": -0.5,
    }


def test_only_evaluators_with_acceptance_add_reports():
    assert comparison_plugin("trip_detection") is None
    assert comparison_plugin("weekly_review") is not None
    assert comparison_plugin("../x") is None
    assert comparison_plugin("missing_evaluator") is None


@pytest.mark.parametrize(
    "mutate",
    [
        lambda r: r["dataset"].update(sha256="other"),
        lambda r: r["metadata"]["scorer"].update(
            scorer_version="weekly-review-scorer-v2"
        ),
        lambda r: r["cases"].reverse(),
    ],
)
def test_incompatible_runs_are_rejected(tmp_path, mutate, capsys):
    reference = run()
    candidate = copy.deepcopy(reference)
    mutate(candidate)
    paths = []
    for name, value in (("a", reference), ("b", candidate)):
        path = tmp_path / f"{name}.json"
        path.write_text(json.dumps(value), encoding="utf-8")
        paths.append(str(path))
    assert main(["compare", *paths]) == 2
    assert "comparison requires identical" in capsys.readouterr().err


def test_case_filter_runs_a_subset_and_cannot_be_compared_with_a_full_run(
    tmp_path, capsys
):
    subset, full = tmp_path / "subset.json", tmp_path / "full.json"
    case = ["--case", "completely-empty-week"]
    assert main(["evaluate", "weekly_review", *case, "--output", str(subset)]) == 0
    assert main(["evaluate", "weekly_review", "--output", str(full)]) == 0
    exported = json.loads(subset.read_text(encoding="utf-8"))
    assert exported["case_count"] == 1
    assert exported["metadata"]["case_filter"] == ["completely-empty-week"]
    assert main(["compare", str(subset), str(full)]) == 2
    assert main(["evaluate", "weekly_review", "--case", "nope"]) == 2
    assert "unknown case ids" in capsys.readouterr().err
