"""AI-007 F3-B1: the LIVE evaluation runner of `assistant_finance` (OpenAI). NOT RUN in
F3-B1.

Sends the frozen SYNTHETIC dataset to OpenAI through the production adapter. It is
separate from `evaluate` on purpose and refuses to send anything unless every condition
holds:

1. `--execute` is given (otherwise: a dry run that prints the request count and the cost
   estimate and sends nothing; no key is read);
2. the dataset is the frozen synthetic v1 (hash checked against gates.DATASET_SHA256)
   and no case carries a scripted response;
3. `--max-cost-usd` is given and the worst-case cost of the whole run fits under it; the
   same cap is enforced again request by request by the adapter's spend guard (a stop is
   an execution error);
4. LIFEOS_AI_ASSISTANT_OPENAI_API_KEY is set in the environment (never a file or an
   argument).

Run it only after ADR-014 and the owner authorise a live test on synthetic data (AI-007
F3-B):

    uv run python -m lifeos_ai_evals.evaluators.assistant_finance.live_run \\
        --experiment experiments/assistant_finance/candidate-gpt-6-luna.json \\
        --max-cost-usd 0.50                # dry run: estimate only
    ... --max-cost-usd 0.50 --execute      # the live run

Output: the scored result (parsed synthetic interpretations, metrics, PROPOSED gates)
under the ignored results/ folder. Raw provider bodies, provider error text and the key
are never printed, logged or saved.
"""

import argparse
import json
import os
import sys
from dataclasses import replace
from decimal import Decimal, InvalidOperation
from pathlib import Path

import httpx
from lifeos_ai.assistant.budget import Prices, SpendGuard
from lifeos_ai.assistant.openai_interpreter import (
    REASONING_EFFORTS,
    REGION_BASE_URLS,
    OpenAIAssistantInterpreter,
    OpenAIAssistantSettings,
)
from lifeos_ai.assistant.openai_transport import HttpResponsesTransport
from lifeos_ai.assistant.prompt import PROMPT_VERSION
from lifeos_ai.assistant.schema import InterpretRequest

from lifeos_ai_evals.core.engine import evaluate
from lifeos_ai_evals.evaluators.assistant_finance import gates, plugin
from lifeos_ai_evals.evaluators.assistant_finance.systems import InterpreterPredictor

KEY_VARIABLE = "LIFEOS_AI_ASSISTANT_OPENAI_API_KEY"
RESULTS = Path(__file__).resolve().parents[4] / "results/assistant_finance"
# A typical-cost estimate (shown next to the upper bound): ~4 UTF-8 bytes per token and
# a short answer with low reasoning. Only the upper bound is enforced.
TYPICAL_BYTES_PER_TOKEN = 4
TYPICAL_OUTPUT_TOKENS = 600
EXPERIMENT_KEYS = {
    "experiment",
    "status",
    "role",
    "provider",
    "model",
    "region",
    "prompt_version",
    "reasoning_effort",
    "max_output_tokens",
    "max_attempts",
    "timeout_seconds",
    "deadline_seconds",
    "prices_usd_per_mtok",
}


class RunRefused(Exception):
    """A precondition does not hold: nothing was sent."""


class _EstimateOnly:
    """The transport of the estimate: it can never send anything."""

    async def create_response(self, body, *, deadline):
        raise AssertionError("the estimate never sends a request")

    async def aclose(self) -> None:
        return None


def load_experiment(path: Path) -> dict:
    try:
        data = json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise RunRefused("the experiment file is not readable JSON") from exc
    if not isinstance(data, dict) or set(data) != EXPERIMENT_KEYS:
        raise RunRefused("the experiment file must hold exactly the documented keys")
    if data["provider"] != "openai" or data["prompt_version"] != PROMPT_VERSION:
        raise RunRefused(
            "only the openai provider with the frozen prompt version is supported"
        )
    if (
        data["region"] not in REGION_BASE_URLS
        or data["reasoning_effort"] not in REASONING_EFFORTS
    ):
        raise RunRefused("unknown region or reasoning effort")
    model = data["model"]
    if (
        not isinstance(model, str)
        or not model
        or not all(c.isalnum() or c in "-._" for c in model)
    ):
        raise RunRefused("invalid model id")
    bounds = {
        "max_output_tokens": (256, 8192),
        "max_attempts": (1, 3),
        "timeout_seconds": (1, 60),
        "deadline_seconds": (1, 70),
    }
    for name, (low, high) in bounds.items():
        value = data[name]
        if type(value) is not int or not low <= value <= high:
            raise RunRefused(f"{name} must be an integer from {low} to {high}")
    prices = data["prices_usd_per_mtok"]
    try:
        data["_prices"] = Prices(Decimal(prices["input"]), Decimal(prices["output"]))
    except (KeyError, TypeError, InvalidOperation) as exc:
        raise RunRefused(
            "prices_usd_per_mtok needs input and output as decimal strings"
        ) from exc
    if data["_prices"].input_per_mtok <= 0 or data["_prices"].output_per_mtok <= 0:
        raise RunRefused("prices must be positive")
    return data


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


def frozen_dataset(case_ids: list[str]):
    dataset = plugin.load(plugin.default_dataset())
    if (dataset.name, dataset.version, dataset.sha256) != (
        gates.DATASET_NAME,
        gates.DATASET_VERSION,
        gates.DATASET_SHA256,
    ):
        raise RunRefused("only the frozen synthetic dataset v1 may be sent")
    if any("scripted_response" in case.input for case in dataset.cases):
        raise RunRefused("scripted pipeline cases are offline only")
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
        description="LIVE assistant_finance evaluation (synthetic)"
    )
    parser.add_argument("--experiment", type=Path, required=True)
    parser.add_argument("--max-cost-usd", required=True)
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
        experiment = load_experiment(args.experiment)
        dataset = frozen_dataset(args.case)

        # The estimate needs no key and builds no connection.
        offline = OpenAIAssistantInterpreter(
            settings_for(experiment, "estimate-only", cap), transport=_EstimateOnly()
        )
        numbers = estimate(dataset, offline, experiment)
        print(
            f"Experiment: {experiment['experiment']} ({experiment['model']}, "
            f"effort {experiment['reasoning_effort']}, region {experiment['region']})"
        )
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
        print("Gates: PROPOSED, NOT APPROVED")

        if not args.execute:
            print("DRY RUN: nothing was sent. Add --execute to run live.")
            return 0
        if numbers["upper_bound_usd"] > cap:
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
        system = InterpreterPredictor(
            interpreter,
            f"assistant-finance-live-{experiment['model']}",
            {
                "provider": "openai",
                "model": experiment["model"],
                "prompt_version": PROMPT_VERSION,
                "reasoning_effort": experiment["reasoning_effort"],
                "max_tokens": experiment["max_output_tokens"],
                "structured_output": "strict json_schema",
                "experiment": experiment["experiment"],
            },
        )
        try:
            result = evaluate("assistant_finance", dataset, system, plugin.scorer())
        finally:
            system.close()
        run = json.loads(result.to_json())
        spent_day, _ = guard.spent()
        run["metadata"]["acceptance"] = gates.acceptance(run)
        run["metadata"]["spend_estimate_usd"] = str(spent_day)
        run["metadata"]["case_filter"] = sorted(args.case)
        output = args.output or results / f"{experiment['experiment']}.json"
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
