"""AI-005: absolute acceptance gates over ONE exported run (there is no candidate to
compare).

Each gate is reported separately as pass / fail. A filtered (`--case`) run, a run with
execution errors or a run of anything but the production control never passes. There is
no overall RAG score: `all_gates_pass` only says whether every predeclared gate held.
"""


def gate(name: str, rule: str, passed: bool, value=None, details=None) -> dict:
    return {
        "gate": name,
        "rule": rule,
        "status": "pass" if passed else "fail",
        "value": value,
        "details": details or [],
    }


def coverage_gate(run: dict) -> dict:
    filtered = "case_filter" in run["metadata"]
    scored = run["metadata"]["scored_cases"]
    return gate(
        "complete_coverage",
        "every case of the frozen dataset scored: no --case filter, zero "
        "execution errors",
        not filtered and run["errors"] == 0 and scored == run["case_count"],
        {"cases": run["case_count"], "scored": scored, "errors": run["errors"]},
        ["filtered run"] if filtered else [],
    )


def control_gate(run: dict, control: dict) -> dict:
    identity = run["metadata"].get("experiment")
    return gate(
        "production_control",
        "the run is the frozen AI-004 production control "
        "(experiments/*/control-v1.json)",
        identity == control,
        None if identity == control else "not the production control",
    )


def zero_gate(run: dict, key: str, rule: str) -> dict:
    value = run["aggregate_metrics"].get(key)
    return gate(f"zero_{key}", rule, value == 0, value)


def minimum_gate(run: dict, key: str, threshold: float, rule: str) -> dict:
    value = run["aggregate_metrics"].get(key)
    return gate(
        f"{key}_min",
        f"{key} >= {threshold}: {rule}",
        value is not None and value >= threshold,
        value,
    )


def cases_where(run: dict, predicate) -> list[str]:
    return [case["id"] for case in run["cases"] if predicate(case)]


def report(version: str, gates: list[dict], diagnostics: dict, note: str) -> dict:
    return {
        "version": version,
        "gates": gates,
        "all_gates_pass": all(item["status"] == "pass" for item in gates),
        "diagnostics": diagnostics,
        "note": note,
    }
