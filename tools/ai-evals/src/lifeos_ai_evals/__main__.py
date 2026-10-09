import argparse
import importlib
import json
import re
import sys
from dataclasses import replace
from pathlib import Path

from lifeos_ai_evals.core.comparison import compare_runs, format_runs
from lifeos_ai_evals.core.engine import evaluate


def comparison_plugin(evaluator):
    """The evaluator's plugin when it offers acceptance reports (AI-003), else None."""
    if not isinstance(evaluator, str) or not re.fullmatch(
        r"[a-z][a-z0-9_]*", evaluator
    ):
        return None
    try:
        plugin = importlib.import_module(
            f"lifeos_ai_evals.evaluators.{evaluator}.plugin"
        )
    except ModuleNotFoundError:
        return None
    return plugin if hasattr(plugin, "acceptance") else None


def with_evaluator_reports(result: dict, plugin) -> dict:
    """Adds per-candidate acceptance gates (pass/fail per gate, never a winner)."""
    runs = result["runs"]
    comparisons = []
    for comparison in result["comparisons"]:
        candidate = runs[comparison["candidate_index"]]
        reports = {"acceptance": plugin.acceptance(runs[0], candidate)}
        # Optional, additive: a stricter promotion report (Action Agent, AI-003.1).
        if hasattr(plugin, "promotion"):
            reports["promotion"] = plugin.promotion(runs[0], candidate)
        comparisons.append({**comparison, **reports})
    return {
        **result,
        "comparisons": comparisons,
        "interpretation": getattr(plugin, "INTERPRETATION", result["interpretation"]),
    }


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="Local synthetic evaluation lab")
    commands = parser.add_subparsers(dest="command", required=True)
    command = commands.add_parser("evaluate")
    command.add_argument("evaluator", help="Evaluator module name, e.g. trip_detection")
    command.add_argument("--dataset", type=Path)
    command.add_argument("--output", type=Path)
    command.add_argument("--verbose", action="store_true")
    command.add_argument(
        "--case",
        action="append",
        default=[],
        help="Run only this case id (repeatable): a small-quota live smoke",
    )
    command.add_argument("--system", default=None)
    command.add_argument(
        "--variant",
        type=Path,
        help="Experiment configuration JSON (explicit live evaluation)",
    )
    comparison = commands.add_parser(
        "compare", help="Compare two or more exported JSON runs"
    )
    comparison.add_argument("runs", type=Path, nargs="+")
    comparison.add_argument("--output", type=Path)
    args = parser.parse_args(argv)
    try:
        if args.command == "compare":
            try:
                result = compare_runs(
                    [json.loads(path.read_text(encoding="utf-8")) for path in args.runs]
                )
            except (KeyError, TypeError, json.JSONDecodeError) as exc:
                raise ValueError("invalid evaluation result JSON") from exc
            plugin = comparison_plugin(result["runs"][0].get("evaluator"))
            if plugin is not None:
                result = with_evaluator_reports(result, plugin)
            if args.output:
                args.output.parent.mkdir(parents=True, exist_ok=True)
                args.output.write_text(
                    json.dumps(result, indent=2, allow_nan=False) + "\n",
                    encoding="utf-8",
                )
            note = getattr(plugin, "METRICS_NOTE", None)
            print(format_runs(result, note) if note else format_runs(result))
            for comparison in result["comparisons"]:
                reports = (("Gate", "acceptance"), ("Promotion gate", "promotion"))
                for label, key in reports:
                    for gate in comparison.get(key, {}).get("gates", []):
                        print(
                            f"{label} run {comparison['candidate_index']} "
                            f"{gate['gate']}: {gate['status'].upper()} "
                            f"({gate['rule']}) "
                            f"reference={gate['reference']} "
                            f"candidate={gate['candidate']} details={gate['details']}"
                        )
            return 1 if any(run["errors"] for run in result["runs"]) else 0
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
        if args.case:
            unknown = set(args.case) - {case.id for case in dataset.cases}
            if unknown:
                raise ValueError(f"unknown case ids: {sorted(unknown)}")
            # Same dataset identity, fewer cases: the export records the filter and
            # comparison rejects it against a full run (different case lists).
            dataset = replace(
                dataset,
                cases=tuple(case for case in dataset.cases if case.id in args.case),
            )
        if args.variant:
            if args.system:
                raise ValueError("select either --system or --variant")
            if not hasattr(plugin, "experiment"):
                raise ValueError("evaluator does not support experiment variants")
            system = plugin.experiment(args.variant)
        else:
            system = plugin.system(args.system) if args.system else plugin.system()
        try:
            result = evaluate(args.evaluator, dataset, system, plugin.scorer())
            if args.case:
                result = replace(
                    result,
                    metadata={**result.metadata, "case_filter": sorted(set(args.case))},
                )
            if hasattr(system, "experiment_metadata"):
                result = replace(
                    result,
                    metadata={**result.metadata, **system.experiment_metadata()},
                )
        finally:
            # AI-005: systems holding resources (a disposable database) release them.
            if hasattr(system, "close"):
                system.close()
        acceptance = None
        if hasattr(plugin, "run_acceptance"):
            # AI-005: absolute gates over this one run (no reference to compare with).
            acceptance = plugin.run_acceptance(json.loads(result.to_json()))
            result = replace(
                result, metadata={**result.metadata, "acceptance": acceptance}
            )
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
        if acceptance is not None:
            for gate in acceptance["gates"]:
                print(
                    f"Gate {gate['gate']}: {gate['status'].upper()} ({gate['rule']}) "
                    f"value={gate['value']} details={gate['details']}"
                )
            print(f"All gates pass: {acceptance['all_gates_pass']}")
        return 1 if result.errors else 0
    except (ValueError, OSError) as exc:
        # No path-bearing raw errors on the console.
        message = str(exc) if isinstance(exc, ValueError) else type(exc).__name__
        print(f"Evaluation configuration error: {message}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
