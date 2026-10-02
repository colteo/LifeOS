import argparse
import importlib
import json
import re
import sys
from pathlib import Path

from lifeos_ai_evals.core.engine import evaluate


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="Local synthetic evaluation lab")
    commands = parser.add_subparsers(dest="command", required=True)
    command = commands.add_parser("evaluate")
    command.add_argument("evaluator", help="Evaluator module name, e.g. trip_detection")
    command.add_argument("--dataset", type=Path)
    command.add_argument("--output", type=Path)
    command.add_argument("--verbose", action="store_true")
    args = parser.parse_args(argv)
    try:
        if not re.fullmatch(r"[a-z][a-z0-9_]*", args.evaluator):
            raise ValueError("invalid evaluator name")
        module_name = f"lifeos_ai_evals.evaluators.{args.evaluator}.plugin"
        try:
            plugin = importlib.import_module(module_name)
        except ModuleNotFoundError as exc:
            if exc.name in {module_name, module_name.rsplit(".", 1)[0]}:
                raise ValueError(f"unknown evaluator: {args.evaluator}") from exc
            raise
        dataset = plugin.load(args.dataset or plugin.default_dataset())
        result = evaluate(args.evaluator, dataset, plugin.system(), plugin.scorer())
        if args.output:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(result.to_json() + "\n", encoding="utf-8")
        print(f"Evaluator: {result.evaluator}")
        print(f"System: {result.system['name']} v{result.system['version']}")
        print(f"Dataset: {dataset.name} v{dataset.version}")
        print(
            f"Cases: {result.case_count} | Scored: {result.metadata['scored_cases']} "
            f"| Incorrect: {result.failures} | Errors: {result.errors}"
        )
        for name, value in result.aggregate_metrics.items():
            display = f"{value:.4f}" if isinstance(value, float) else str(value)
            print(f"{name}: {display}")
        for case in result.cases:
            print(f"{case.status.upper()}: {case.id}")
            if case.error:
                print(f"  {case.error['stage']}: {case.error['type']}")
            if args.verbose:
                print(f"  {case.description}")
                if case.score:
                    print(f"  metrics: {json.dumps(case.score.metrics)}")
                if case.prediction:
                    for explanation in case.prediction.explanations:
                        print(f"  {explanation}")
        return 1 if result.errors else 0
    except (ValueError, OSError) as exc:
        # No path-bearing raw errors on the console.
        message = str(exc) if isinstance(exc, ValueError) else type(exc).__name__
        print(f"Evaluation configuration error: {message}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
