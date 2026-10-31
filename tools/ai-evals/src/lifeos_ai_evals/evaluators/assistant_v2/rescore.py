"""AI-008 G1/G2: re-score a saved `assistant_v2` live run with the current adapter and
scorer, without sending anything.

The saved predictions (parsed, validated interpretations) are replayed against the
dataset the run used, rebuilt by its `--dataset` name; nothing reaches a provider and no
key is read. Used when an expectation was corrected after a run (a new
`adapt.ADAPTER_VERSION`), so the same model answers are scored again:

    uv run --offline python -m lifeos_ai_evals.evaluators.assistant_v2.rescore \\
        results/assistant_v2/<run>.json --dataset finance-holdout-v1
"""

import argparse
import json
import sys
from pathlib import Path

from lifeos_ai_evals.core.engine import Prediction, evaluate
from lifeos_ai_evals.evaluators.assistant_v2 import adapt, gates, plugin
from lifeos_ai_evals.evaluators.assistant_v2.live_run import DATASETS


def _key(request: dict) -> str:
    return json.dumps(request, sort_keys=True, ensure_ascii=False)


class _Replay:
    """Answers each case with the prediction the saved run recorded for it."""

    version = "1.0.0"

    def __init__(self, run: dict, dataset):
        self.name = run["system"]["name"]
        self.configuration = {**run["system"]["configuration"], "replayed": True}
        saved = {case["id"]: case["prediction"] for case in run["cases"]}
        self.predictions = {
            _key(case.input["request"]): saved[case.id] for case in dataset.cases
        }

    def predict(self, value: dict) -> Prediction:
        saved = self.predictions[_key(value["request"])]
        if saved is None:
            # An execution error of the live run stays one.
            raise RuntimeError("the saved run has no prediction for this case")
        return Prediction(saved["value"], tuple(saved.get("explanations", ())))


def rescore(run: dict, which: str) -> dict:
    dataset = DATASETS[which][0]()
    identity = (dataset.name, dataset.version, dataset.sha256)
    saved = run["dataset"]
    if identity != (saved["name"], saved["version"], saved["sha256"]):
        raise ValueError("the run was not made on this dataset")
    if [c.id for c in dataset.cases] != [c["id"] for c in run["cases"]]:
        raise ValueError("the run's cases differ from the dataset's")
    result = evaluate("assistant_v2", dataset, _Replay(run, dataset), plugin.scorer())
    rescored = json.loads(result.to_json())
    rescored["metadata"] = {
        **rescored["metadata"],
        "acceptance": gates.acceptance(rescored),
        "adapter_version": adapt.ADAPTER_VERSION,
        "rescored_from": {
            "adapter_version": run["metadata"].get("adapter_version"),
            "spend_estimate_usd": run["metadata"].get("spend_estimate_usd"),
        },
    }
    return rescored


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="Re-score a saved assistant_v2 run")
    parser.add_argument("run", type=Path)
    parser.add_argument("--dataset", choices=sorted(DATASETS), required=True)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args(argv)
    try:
        run = json.loads(args.run.read_text(encoding="utf-8"))
        rescored = rescore(run, args.dataset)
    except (OSError, ValueError, KeyError) as exc:
        print(f"Re-score refused: {exc}", file=sys.stderr)
        return 2
    output = args.output or args.run.with_name(
        f"{args.run.stem}-rescored-{adapt.ADAPTER_VERSION}.json"
    )
    output.write_text(
        json.dumps(rescored, indent=2, allow_nan=False) + "\n", encoding="utf-8"
    )
    print(
        f"Cases: {rescored['case_count']} | Incorrect: {rescored['failures']} | "
        f"Errors: {rescored['errors']} | adapter {adapt.ADAPTER_VERSION}"
    )
    for gate in rescored["metadata"]["acceptance"]["gates"]:
        print(
            f"Gate {gate['gate']}: {gate['status'].upper()} ({gate['rule']}) "
            f"value={gate['value']}"
        )
    print(f"Result written to {output.name}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
