"""Compare exported runs without invoking either predictor."""

from lifeos_ai_evals.core.engine import DATASET_HASH_STRATEGY

METRICS = (
    ("Cases", "case_count"),
    ("Errors", "errors"),
    ("TP", "true_positives"),
    ("FP", "false_positives"),
    ("FN", "false_negatives"),
    ("Precision", "precision"),
    ("Recall", "recall"),
    ("F1", "f1"),
    ("Mean boundary error", "mean_absolute_boundary_error_days"),
)


def compatible_datasets(left: dict, right: dict) -> bool:
    if {k: v for k, v in left["dataset"].items() if k != "sha256"} != {
        k: v for k, v in right["dataset"].items() if k != "sha256"
    }:
        return False
    left_strategy = (
        left["metadata"].get("dataset_hash", {}).get("strategy", "raw-sha256")
    )
    right_strategy = (
        right["metadata"].get("dataset_hash", {}).get("strategy", "raw-sha256")
    )
    if left_strategy == right_strategy:
        return (
            left_strategy in (DATASET_HASH_STRATEGY, "raw-sha256")
            and left["dataset"]["sha256"] == right["dataset"]["sha256"]
        )
    if {left_strategy, right_strategy} != {DATASET_HASH_STRATEGY, "raw-sha256"}:
        return False
    normalized, legacy = (
        (left, right) if left_strategy == DATASET_HASH_STRATEGY else (right, left)
    )
    aliases = normalized["metadata"]["dataset_hash"].get("legacy_sha256", [])
    return (
        normalized["dataset"]["sha256"] in aliases
        and legacy["dataset"]["sha256"] in aliases
    )


def compare_results(baseline: dict, candidate: dict) -> dict:
    if not compatible_datasets(baseline, candidate):
        raise ValueError("comparison requires identical dataset identity")
    for key in ("evaluator", "case_count"):
        if baseline[key] != candidate[key]:
            raise ValueError(f"comparison requires identical {key}")
    for key in ("scorer", "schema_version", "harness_version", "error_policy"):
        if baseline["metadata"][key] != candidate["metadata"][key]:
            raise ValueError(f"comparison requires identical {key}")
    if [(c["id"], c["expected"], c.get("tags", [])) for c in baseline["cases"]] != [
        (c["id"], c["expected"], c.get("tags", [])) for c in candidate["cases"]
    ]:
        raise ValueError("comparison requires identical ordered cases and labels")
    deltas = {}
    for key in baseline["aggregate_metrics"]:
        left = baseline["aggregate_metrics"][key]
        right = candidate["aggregate_metrics"][key]
        deltas[key] = right - left if left is not None and right is not None else None
    incorrect = [
        {"id": left["id"], "baseline": left["status"], "candidate": right["status"]}
        for left, right in zip(baseline["cases"], candidate["cases"], strict=True)
        if left["status"] != "correct" or right["status"] != "correct"
    ]
    return {
        "dataset": baseline["dataset"],
        "scorer": baseline["metadata"]["scorer"],
        "baseline": baseline,
        "candidate": candidate,
        "deltas": deltas,
        "incorrect_case_differences": incorrect,
        "interpretation": "Metrics exclude execution errors; inspect coverage first.",
    }


def format_comparison(result: dict) -> str:
    lines = [f"Dataset: {result['dataset']}", f"Scorer: {result['scorer']}"]
    for label in ("baseline", "candidate"):
        lines.append(f"{label.capitalize()} system: {result[label]['system']}")
    candidate_label = (
        "Groq LLM"
        if result["candidate"]["system"]["configuration"].get("provider") == "groq"
        else "Candidate"
    )
    lines.append(f"{'Metric':<24} {'Baseline':>12} {candidate_label:>12}")
    for label, key in METRICS:
        values = []
        for system in ("baseline", "candidate"):
            run = result[system]
            value = run[key] if key in run else run["aggregate_metrics"][key]
            values.append(f"{value:.4f}" if isinstance(value, float) else str(value))
        lines.append(f"{label:<24} {values[0]:>12} {values[1]:>12}")
    lines.extend(f"Delta {key}: {value}" for key, value in result["deltas"].items())
    lines.append(result["interpretation"])
    lines.append(f"Incorrect-case differences: {result['incorrect_case_differences']}")
    return "\n".join(lines)


def compare_runs(runs: list[dict]) -> dict:
    if len(runs) < 2:
        raise ValueError("comparison requires at least two runs")
    for run in runs:
        cases = run["cases"]
        if not isinstance(cases, list) or run["case_count"] != len(cases):
            raise ValueError("invalid run case coverage")
        if len({case["id"] for case in cases}) != len(cases):
            raise ValueError("duplicate run case ids")
        if any(
            case["status"] not in ("correct", "incorrect", "error") for case in cases
        ):
            raise ValueError("invalid run case status")
        if run["errors"] != sum(case["status"] == "error" for case in cases):
            raise ValueError("invalid run error coverage")
        if run["metadata"]["scored_cases"] != len(cases) - run["errors"]:
            raise ValueError("invalid run scored coverage")
    pairs = [compare_results(runs[0], run) for run in runs[1:]]
    return {
        "dataset": runs[0]["dataset"],
        "scorer": runs[0]["metadata"]["scorer"],
        "runs": runs,
        "comparisons": [
            {
                "reference_index": 0,
                "candidate_index": index,
                "deltas": pair["deltas"],
                "coverage_deltas": {
                    "cases": runs[index]["case_count"] - runs[0]["case_count"],
                    "execution_errors": runs[index]["errors"] - runs[0]["errors"],
                    "scored_cases": runs[index]["metadata"]["scored_cases"]
                    - runs[0]["metadata"]["scored_cases"],
                },
                "incorrect_case_differences": pair["incorrect_case_differences"],
                "execution_error_differences": [
                    {
                        "id": left["id"],
                        "reference": left["error"],
                        "candidate": right["error"],
                    }
                    for left, right in zip(
                        runs[0]["cases"], runs[index]["cases"], strict=True
                    )
                    if left["error"] or right["error"]
                ],
                "tag_deltas": {
                    tag: {
                        key: right["metrics"][key] - left["metrics"][key]
                        if right["metrics"][key] is not None
                        and left["metrics"][key] is not None
                        else None
                        for key in left["metrics"]
                    }
                    for tag, left in runs[0]["metadata"].get("tag_metrics", {}).items()
                    if (
                        right := runs[index]["metadata"].get("tag_metrics", {}).get(tag)
                    )
                    is not None
                },
            }
            for index, pair in enumerate(pairs, 1)
        ],
        "interpretation": (
            "Signed deltas relative to run 0. Inspect scored/error coverage. "
            "Boundary metrics describe matched trips only. No overall winner."
        ),
        # Retain the original two-run export contract.
        **(
            {key: value for key, value in pairs[0].items() if key != "interpretation"}
            if len(runs) == 2
            else {}
        ),
    }


def format_runs(result: dict) -> str:
    lines = [f"Dataset: {result['dataset']}", f"Scorer: {result['scorer']}"]
    for index, run in enumerate(result["runs"]):
        lines.append(
            f"Run {index}: {run['system']} | "
            f"Identity: {run['metadata'].get('experiment')}"
        )
        lines.append(
            f"Coverage: {run['metadata']['scored_cases']}/{run['case_count']} "
            f"| Errors: {run['errors']}"
        )
        lines.append(
            "Metrics (Mean boundary error: matched trips only): "
            f"{run['aggregate_metrics']}"
        )
        lines.append(f"Telemetry: {run['metadata'].get('telemetry')}")
        lines.append(f"Tags: {run['metadata'].get('tag_metrics', {})}")
    lines.extend(str(pair) for pair in result["comparisons"])
    lines.append(result["interpretation"])
    return "\n".join(lines)
