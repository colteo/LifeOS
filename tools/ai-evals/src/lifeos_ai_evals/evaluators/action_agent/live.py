"""Live Action Agent predictor over the PRODUCTION step adapter
(lifeos_ai.action_agent.groq).

Each step is the production GroqActionAgent: production prompt, tool offering (terminal
tools only on the last step), native tool calling, transport policy and strict decision
validation. This wrapper selects model, registered prompt version and generation
settings; for the control variant the request body equals the production body (tested).
The loop around it mirrors .NET (dotnet_mirror.py). The transport observer records the
tool names the model emitted, including names production then rejects (invented or
unoffered tools), never arguments or content.
"""

import asyncio
import time

import httpx
from lifeos_ai.action_agent.groq import GroqActionAgent
from lifeos_ai.action_agent.prompt import PROMPT_VERSION, SYSTEM_PROMPT

from lifeos_ai_evals.core.engine import Prediction
from lifeos_ai_evals.evaluators.action_agent import dotnet_mirror as net
from lifeos_ai_evals.evaluators.action_agent.loop import run
from lifeos_ai_evals.evaluators.action_agent.model import RunTrace, Scenario
from lifeos_ai_evals.production import runtime

OUTPUT_CONTRACT = "action-agent-decision-v1"

# Registered prompt versions: production imported, never copied (see AI-003 §8).
PROMPTS = {PROMPT_VERSION: SYSTEM_PROMPT}


class VariantAgent(GroqActionAgent):
    """The production step adapter with an explicit prompt version and generation
    settings."""

    def __init__(self, settings, *, prompt_version: str, generation: dict, **kwargs):
        super().__init__(settings, **kwargs)
        self.prompt_version = prompt_version
        self._generation = generation

    def request_body(self, request) -> dict:
        body = super().request_body(request)
        body = runtime.replace_system_prompt(body, PROMPTS[self.prompt_version])
        return runtime.apply_generation_settings(body, self._generation)


class TelemetryNames:
    """Tool names in the last answered attempt of the current step (empty when none)."""

    def __init__(self, telemetry: runtime.Telemetry):
        self._telemetry = telemetry

    def mark(self) -> int:
        self._telemetry.logical_requests += 1
        return self._telemetry.mark()

    def names(self, mark: int) -> tuple[str, ...]:
        answered = [a for a in self._telemetry.since(mark) if a.status == "http_200"]
        return answered[-1].tool_names if answered else ()


class LiveActionAgentPredictor:
    name = "lifeos-ai-action-agent"
    version = "1.0.0"

    def __init__(
        self,
        variant,
        *,
        api_key: str | None = None,
        transport: httpx.AsyncBaseTransport | None = None,
        sleep=None,
        case_sleep=time.sleep,
    ):
        if variant.provider != "groq":
            raise ValueError("provider adapter unavailable; only groq is implemented")
        if variant.prompt_version not in PROMPTS:
            raise ValueError(f"unregistered prompt version: {variant.prompt_version}")
        if variant.context_version != net.TOOL_SCHEMA_VERSION:
            raise ValueError(
                f"unsupported tool schema version: {variant.context_version}"
            )
        if variant.structured_output != OUTPUT_CONTRACT:
            raise ValueError(
                f"unsupported output contract: {variant.structured_output}"
            )
        self._generation = runtime.validate_generation_settings(
            variant.generation_settings
        )
        # A fake transport is solely for offline tests; live runs require the
        # environment key.
        self._api_key = api_key or (
            "offline-test-key" if transport else runtime.api_key_from_environment()
        )
        self._variant = variant
        self._transport = transport
        self._sleep = sleep
        self._case_sleep = case_sleep
        self._cases = 0
        self.telemetry = runtime.Telemetry()
        self.identity = variant.identity()
        self.configuration = {
            **self.identity,
            "runtime": dict(runtime.RUNTIME),
            "loop": net.LOOP_VERSION,
            "max_steps": net.MAX_STEPS,
            "adapter": "lifeos_ai.action_agent.groq.GroqActionAgent",
        }

    def agent(self) -> VariantAgent:
        kwargs = {"client": runtime.client(self.telemetry, self._transport)}
        if self._sleep is not None:
            kwargs["sleep"] = self._sleep
        return VariantAgent(
            runtime.groq_settings(self._api_key, self._variant.model, self._generation),
            prompt_version=self._variant.prompt_version,
            generation=self._generation,
            **kwargs,
        )

    def predict(self, value: Scenario) -> Prediction[RunTrace]:
        if self._cases:
            self._case_sleep(runtime.RUNTIME["inter_case_delay_seconds"])
        self._cases += 1
        return Prediction(asyncio.run(self._predict(value)))

    async def _predict(self, value: Scenario) -> RunTrace:
        agent = self.agent()
        try:
            return await run(value, agent, TelemetryNames(self.telemetry))
        finally:
            await agent.aclose()

    def experiment_metadata(self) -> dict:
        return {
            "reproducibility": "experiment identity; probabilistic outputs",
            "telemetry": self.telemetry.summary(),
        }
