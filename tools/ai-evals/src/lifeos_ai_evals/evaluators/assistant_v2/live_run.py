"""AI-008 G1/G2: the LIVE evaluation runner of `assistant_v2` (OpenAI, protocol 2).

The same preconditions as `assistant_finance.live_run`, whose experiment checks, cap
ceiling and key variable it reuses; it refuses to send anything unless every condition
holds:

1. `--execute` is given (otherwise: a dry run that prints the request count and the cost
   estimate and sends nothing; no key is read);
2. the dataset is one of the four frozen synthetic ones, chosen with `--dataset`
   (`finance-v1`, the default, `finance-holdout-v1`, `hydration-meal-v1`,
   `hydration-meal-holdout-v1`; hash checked against gates.frozen_datasets());
3. the experiment's prompt is a published protocol-2 prompt (`assistant-interpret-v3`),
   sent as the interpreter's `prompt_version_v2`;
4. `--max-cost-usd` is given, at most MAX_CAP_USD, and the worst-case cost of the whole
   run fits under it; the same cap is enforced again request by request by the adapter's
   spend guard (a stop is an execution error);
5. LIFEOS_AI_ASSISTANT_OPENAI_API_KEY is set in the environment (never a file or an
   argument).

Run it only with the owner's explicit go (runbook Part I, G1/G2):

    uv run python -m lifeos_ai_evals.evaluators.assistant_v2.live_run \\
        --experiment experiments/assistant_v2/candidate-gpt-6-luna.json \\
        --dataset finance-v1 --max-cost-usd 0.25      # dry run: estimate only
    ... --max-cost-usd 0.25 --execute                 # the live run

Output: the scored result under the ignored results/ folder. Raw provider bodies,
provider error text and the key are never printed, logged or saved.
"""

import argparse
import json
import os
import sys
from dataclasses import replace
from decimal import Decimal, InvalidOperation
from pathlib import Path

import httpx
from lifeos_ai.assistant.budget import SpendGuard
from lifeos_ai.assistant.openai_interpreter import (
    OpenAIAssistantInterpreter,
    OpenAIAssistantSettings,
)
from lifeos_ai.assistant.openai_transport import HttpResponsesTransport
from lifeos_ai.assistant.prompt import PROMPT_PROTOCOLS, PROMPTS
from lifeos_ai.assistant.schema_v2 import InterpretRequest

from lifeos_ai_evals.core.engine import evaluate
from lifeos_ai_evals.evaluators.assistant_finance.live_run import (
    KEY_VARIABLE,
    MAX_CAP_USD,
    TYPICAL_BYTES_PER_TOKEN,
    TYPICAL_OUTPUT_TOKENS,
    RunRefused,
    _EstimateOnly,
)
from lifeos_ai_evals.evaluators.assistant_finance.live_run import (
    load_experiment as load_finance_experiment,
)
from lifeos_ai_evals.evaluators.assistant_v2 import adapt, gates, plugin
from lifeos_ai_evals.evaluators.assistant_v2.systems import V2Predictor

RESULTS = Path(__file__).resolve().parents[4] / "results/assistant_v2"
# --dataset -> (loader, suffix of the result file).
DATASETS = {
    "finance-v1": (lambda: adapt.finance_dataset("v1"), "-finance-v1"),
    "finance-holdout-v1": (
        lambda: adapt.finance_dataset("holdout-v1"),
        "-finance-holdout-v1",
    ),
    "hydration-meal-v1": (
        lambda: plugin.load(plugin.default_dataset()),
        "-hydration-meal-v1",
    ),
    "hydration-meal-holdout-v1": (
        lambda: plugin.load(plugin.holdout_dataset()),
        "-hydration-meal-holdout-v1",
    ),
}


def load_experiment(path: Path) -> dict:
    experiment = load_finance_experiment(path)
    if PROMPT_PROTOCOLS[experiment["prompt_version"]] != 2:
        raise RunRefused("the experiment's prompt must be a protocol-2 prompt")
    return experiment


def settings_for(
    experiment: dict, api_key: str, cap: Decimal
) -> OpenAIAssistantSettings:
    return OpenAIAssistantSettings(
        api_key=api_key,
        model=experiment["model"],
        region=experiment["region"],
        daily_budget_usd=cap,
        monthly_budget_usd=cap,
        prices=experiment["_prices"],
        reasoning_effort=experiment["reasoning_effort"],
        timeout_seconds=float(experiment["timeout_seconds"]),
        deadline_seconds=float(experiment["deadline_seconds"]),
        max_attempts=experiment["max_attempts"],
        max_output_tokens=experiment["max_output_tokens"],
        max_concurrency=1,
        prompt_version_v2=experiment["prompt_version"],
    )


def estimate(
    dataset, interpreter: OpenAIAssistantInterpreter, experiment: dict
) -> dict:
    upper = Decimal(0)
    typical = Decimal(0)
    for case in dataset.cases:
        body = interpreter.request_body(
            InterpretRequest.model_validate(case.input["request"])
        )
        upper += interpreter.worst_case_cost(body)
        size = len(json.dumps(body, ensure_ascii=False).encode("utf-8"))
        typical += experiment["_prices"].cost(
            size // TYPICAL_BYTES_PER_TOKEN, TYPICAL_OUTPUT_TOKENS
        )
    return {
        "cases": len(dataset.cases),
        "requests_min": len(dataset.cases),
        "requests_max": len(dataset.cases) * experiment["max_attempts"],
        "upper_bound_usd": upper,
        "typical_usd": typical,
    }


def frozen_dataset(case_ids: list[str], which: str = "finance-v1"):
    dataset = DATASETS[which][0]()
    identity = gates.frozen_datasets().get((dataset.name, dataset.version))
    if identity is None or dataset.sha256 != identity[0]:
        raise RunRefused("only a frozen synthetic dataset may be sent")
    if case_ids:
        unknown = set(case_ids) - {case.id for case in dataset.cases}
        if unknown:
            raise RunRefused(f"unknown case ids: {sorted(unknown)}")
        dataset = replace(
            dataset, cases=tuple(c for c in dataset.cases if c.id in case_ids)
        )
    return dataset


def main(
    argv=None,
    *,
    environ=os.environ,
    http_transport: httpx.AsyncBaseTransport | None = None,
    results: Path = RESULTS,
) -> int:
    parser = argparse.ArgumentParser(
        description="LIVE assistant_v2 evaluation (synthetic, protocol 2)"
    )
    parser.add_argument("--experiment", type=Path, required=True)
    parser.add_argument("--max-cost-usd", required=True)
    parser.add_argument("--dataset", choices=sorted(DATASETS), default="finance-v1")
    parser.add_argument("--case", action="append", default=[])
    parser.add_argument("--output", type=Path)
    parser.add_argument(
        "--execute",
        action="store_true",
        help="actually send the synthetic dataset to OpenAI (default: dry run)",
    )
    args = parser.parse_args(argv)
    try:
        try:
            cap = Decimal(args.max_cost_usd)
        except InvalidOperation:
            cap = Decimal(0)
        if not cap.is_finite() or cap <= 0:
            raise RunRefused("--max-cost-usd must be a positive amount")
        if cap > MAX_CAP_USD:
            raise RunRefused(
                f"--max-cost-usd is above the runner's ceiling of {MAX_CAP_USD} USD"
            )
        experiment = load_experiment(args.experiment)
        dataset = frozen_dataset(args.case, args.dataset)

        # The estimate needs no key and builds no connection.
        offline = OpenAIAssistantInterpreter(
            settings_for(experiment, "estimate-only", cap), transport=_EstimateOnly()
        )
        numbers = estimate(dataset, offline, experiment)
        print(
            f"Experiment: {experiment['experiment']} ({experiment['model']}, "
            f"effort {experiment['reasoning_effort']}, region {experiment['region']})"
        )
        print(f"Prompt: {offline.prompt_version_v2} (protocol 2)")
        print(
            f"Dataset: {dataset.name} v{dataset.version} "
            f"sha256 {dataset.sha256[:12]}... (synthetic)"
        )
        print(
            f"Requests: {numbers['requests_min']} (at most {numbers['requests_max']} "
            "with retries)"
        )
        print(
            f"Estimated cost: typical ~{numbers['typical_usd']:.4f} USD, "
            f"upper bound {numbers['upper_bound_usd']:.4f} USD; cap {cap} USD"
        )
        print(f"Gates: {gates.GATES_VERSION} ({gates.STATUS})")
        fits = numbers["upper_bound_usd"] <= cap
        print(
            "Cap check: upper bound within the cap"
            if fits
            else "Cap check: upper bound EXCEEDS the cap; --execute would be refused"
        )

        if not args.execute:
            print("DRY RUN: nothing was sent. Add --execute to run live.")
            return 0
        if not fits:
            raise RunRefused("the worst-case cost exceeds --max-cost-usd")
        api_key = environ.get(KEY_VARIABLE, "").strip()
        if not api_key:
            raise RunRefused(f"{KEY_VARIABLE} is not set")

        settings = settings_for(experiment, api_key, cap)
        client = None
        if http_transport is not None:
            client = httpx.AsyncClient(
                transport=http_transport, base_url=settings.base_url
            )
        transport = HttpResponsesTransport(
            api_key=api_key,
            base_url=settings.base_url,
            timeout_seconds=settings.timeout_seconds,
            max_attempts=settings.max_attempts,
            client=client,
        )
        guard = SpendGuard(daily_budget=cap, monthly_budget=cap)
        interpreter = OpenAIAssistantInterpreter(
            settings, transport=transport, guard=guard
        )
        system = V2Predictor(
            interpreter,
            f"assistant-v2-live-{experiment['model']}",
            {
                "provider": "openai",
                "model": experiment["model"],
                "prompt_version": interpreter.prompt_version_v2,
                "prompt_sha256": PROMPTS[interpreter.prompt_version_v2],
                "reasoning_effort": experiment["reasoning_effort"],
                "max_tokens": experiment["max_output_tokens"],
                "structured_output": "strict json_schema",
                "experiment": experiment["experiment"],
            },
        )
        try:
            result = evaluate("assistant_v2", dataset, system, plugin.scorer())
        finally:
            system.close()
        run = json.loads(result.to_json())
        spent_day, _ = guard.spent()
        run["metadata"]["acceptance"] = gates.acceptance(run)
        run["metadata"]["spend_estimate_usd"] = str(spent_day)
        run["metadata"]["case_filter"] = sorted(args.case)
        run["metadata"]["adapter_version"] = adapt.ADAPTER_VERSION
        suffix = DATASETS[args.dataset][1]
        output = args.output or results / f"{experiment['experiment']}{suffix}.json"
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(
            json.dumps(run, indent=2, allow_nan=False) + "\n", encoding="utf-8"
        )
        print(
            f"Cases: {run['case_count']} | Incorrect: {run['failures']} | "
            f"Errors: {run['errors']} | Estimated spend: {spent_day:.4f} USD"
        )
        for gate in run["metadata"]["acceptance"]["gates"]:
            print(
                f"Gate {gate['gate']}: {gate['status'].upper()} ({gate['rule']}) "
                f"value={gate['value']}"
            )
        print(f"Result written to {output.name} (results/ is not committed)")
        return 1 if run["errors"] else 0
    except RunRefused as refused:
        print(f"Live run refused: {refused}. Nothing was sent.", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
