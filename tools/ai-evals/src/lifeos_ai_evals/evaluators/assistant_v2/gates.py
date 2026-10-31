"""AI-008 G1/G2: acceptance gates for the protocol-2 interpreter (prompt v3).

The thresholds, the zero-critical tags and the reported metrics are those of
`assistant-finance-gates-v2`, imported unchanged; the owner approved applying them to
G1 and G2 on 2026-10-31 (runbook Part I). They apply to four frozen datasets:

- G1: the Finance `v1` and `holdout-v1` files of `assistant_finance`, converted to
  protocol 2 by `adapt.py` (same name, version and SHA-256 as the frozen files);
- G2: `datasets/assistant_v2/hydration-meal-v1.json` and its held-out
  `hydration-meal-holdout-v1.json`, frozen before any model result on them.

A run on any other dataset content, or on only part of it (`live_run --case`), is
reported `invalid`. Changing a threshold or a dataset after a real result has been
observed requires a new version (`GATES_VERSION`, a new dataset file) and a note of why,
never an edit in place; a failed gate is never fixed by editing the data.
"""

from lifeos_ai_evals.evaluators.assistant_finance import gates as finance_gates

GATES_VERSION = "assistant-v2-gates-v1"
STATUS = "OWNER_APPROVED_2026-10-31"
BASED_ON = finance_gates.GATES_VERSION

# LF-normalised SHA-256 (core.engine.load_dataset) of datasets/assistant_v2/.
HYDRATION_MEAL_NAME = "assistant_hydration_meal"
HYDRATION_MEAL_VERSION = "1"
HYDRATION_MEAL_SHA256 = (
    "36758e2a53e3ec87bdf9e632e6dc95f67074c1f717a7b9a8ad2b155ddb2936c9"
)
HYDRATION_MEAL_CASES = 60

HYDRATION_MEAL_HOLDOUT_NAME = "assistant_hydration_meal_holdout"
HYDRATION_MEAL_HOLDOUT_VERSION = "1"
HYDRATION_MEAL_HOLDOUT_SHA256 = (
    "1f04863b9044b2fb62bd16b1980e39a320d9f347baac5a2e9e26af7d609a167e"
)
HYDRATION_MEAL_HOLDOUT_CASES = 20

GATES = finance_gates.GATES
ZERO_CRITICAL_TAGS = finance_gates.ZERO_CRITICAL_TAGS
REPORTED_METRICS = finance_gates.REPORTED_METRICS


def frozen_datasets() -> dict[tuple[str, str], tuple[str, int]]:
    """(name, version) -> (sha256, cases) of every frozen dataset the gates apply to."""
    return {
        **finance_gates.frozen_datasets(),
        (HYDRATION_MEAL_NAME, HYDRATION_MEAL_VERSION): (
            HYDRATION_MEAL_SHA256,
            HYDRATION_MEAL_CASES,
        ),
        (HYDRATION_MEAL_HOLDOUT_NAME, HYDRATION_MEAL_HOLDOUT_VERSION): (
            HYDRATION_MEAL_HOLDOUT_SHA256,
            HYDRATION_MEAL_HOLDOUT_CASES,
        ),
    }


def _gate(metric, rule, status, value, details) -> dict:
    return {
        "gate": metric,
        "rule": rule,
        "status": status,
        "value": value,
        "details": details,
    }


def acceptance(run: dict) -> dict:
    dataset = run["dataset"]
    identity = frozen_datasets().get((dataset["name"], dataset["version"]))
    frozen = identity is not None and dataset["sha256"] == identity[0]
    complete = identity is not None and run.get("case_count") == identity[1]
    valid = frozen and complete
    metrics = run["aggregate_metrics"]
    gates = []
    for metric, comparison, threshold, why in GATES:
        value = metrics.get(metric)
        if not valid or value is None:
            status = "invalid"
        elif comparison == "<=":
            status = "pass" if value <= threshold else "fail"
        else:
            status = "pass" if value >= threshold else "fail"
        gates.append(
            _gate(metric, f"{metric} {comparison} {threshold}", status, value, why)
        )
    tag_metrics = run["metadata"].get("tag_metrics", {})
    for tag in ZERO_CRITICAL_TAGS:
        value = tag_metrics.get(tag, {}).get("metrics", {}).get("critical_error_cases")
        if not valid:
            status = "invalid"
        elif value is None:
            # A dataset without the tag has nothing to fail there.
            status = "not_applicable"
        else:
            status = "pass" if value == 0 else "fail"
        gates.append(
            _gate(
                f"no_critical_in_{tag}",
                f"tag {tag}: critical_error_cases == 0",
                status,
                value,
                "safety-relevant category",
            )
        )
    errors = run["errors"]
    gates.append(
        _gate(
            "no_execution_errors",
            "errors == 0",
            "pass" if errors == 0 else "fail",
            errors,
            "an unavailable provider or a budget stop invalidates the run",
        )
    )
    return {
        "gates_version": GATES_VERSION,
        "based_on": BASED_ON,
        "status": STATUS,
        "dataset_frozen": frozen,
        "dataset_complete": complete,
        "gates": gates,
        "reported": {name: metrics.get(name) for name in REPORTED_METRICS},
        "all_gates_pass": all(
            gate["status"] in ("pass", "not_applicable") for gate in gates
        ),
    }
