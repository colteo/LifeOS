"""Compare exported runs without invoking either predictor."""

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


def compare_results(baseline: dict, candidate: dict) -> dict:
    for key in ("evaluator", "dataset", "case_count"):
        if baseline[key] != candidate[key]:
            raise ValueError(f"comparison requires identical {key}")
    for key in ("scorer", "schema_version", "harness_version", "error_policy"):
        if baseline["metadata"][key] != candidate["metadata"][key]:
            raise ValueError(f"comparison requires identical {key}")
    if [(c["id"], c["expected"]) for c in baseline["cases"]] != [
        (c["id"], c["expected"]) for c in candidate["cases"]
    ]:
        raise ValueError("comparison requires identical ordered cases and labels")
    deltas = {}
    for key in ("f1", "mean_absolute_boundary_error_days"):
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
