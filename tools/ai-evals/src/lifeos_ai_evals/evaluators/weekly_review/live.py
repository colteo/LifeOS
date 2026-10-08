"""Live Weekly Review predictor over the PRODUCTION interpreter
(lifeos_ai.weekly_review.groq).

The production adapter builds the request (prompt, strict json_schema, settings), sends
it with the production transport policy and validates the answer with the production
contract. This wrapper only selects the experiment variant: model, a registered prompt
version and generation settings. For the control variant the request body is
byte-for-byte the production body (tested).

Outcomes: valid insights -> scored; InterpretationFailed
(rejected/malformed/out-of-contract output) -> scored as an invalid output;
ProviderUnavailable -> execution error (excluded from quality metrics, reported as
coverage).
"""

import asyncio
import time

import httpx
from lifeos_ai.weekly_review.groq import GroqWeeklyReviewInterpreter
from lifeos_ai.weekly_review.interpreter import InterpretationFailed
from lifeos_ai.weekly_review.prompt import PROMPT_VERSION, SYSTEM_PROMPT
from lifeos_ai.weekly_review.schema import InterpretWeeklyReviewRequest

from lifeos_ai_evals.core.engine import Prediction
from lifeos_ai_evals.evaluators.weekly_review.model import InsightsOutput
from lifeos_ai_evals.production import runtime

CONTEXT_VERSION = "weekly-review-request-v1"
OUTPUT_CONTRACT = "weekly-review-output-v1"

# Registered prompt versions. Production prompts are imported, never copied. A candidate
# prompt is added here as a new immutable version (see docs/tasks/ai/AI-003.md).
PROMPTS = {PROMPT_VERSION: SYSTEM_PROMPT}


class VariantInterpreter(GroqWeeklyReviewInterpreter):
    """The production interpreter with an explicit prompt version and generation
    settings."""

    def __init__(self, settings, *, prompt_version: str, generation: dict, **kwargs):
        super().__init__(settings, **kwargs)
        self.prompt_version = prompt_version
        self._generation = generation

    def request_body(self, request: InterpretWeeklyReviewRequest) -> dict:
        body = super().request_body(request)
        body = runtime.replace_system_prompt(body, PROMPTS[self.prompt_version])
        return runtime.apply_generation_settings(body, self._generation)


class LiveWeeklyReviewPredictor:
    name = "lifeos-ai-weekly-review"
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
        if variant.context_version != CONTEXT_VERSION:
            raise ValueError(f"unsupported context version: {variant.context_version}")
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
            "adapter": "lifeos_ai.weekly_review.groq.GroqWeeklyReviewInterpreter",
        }

    def interpreter(self) -> VariantInterpreter:
        kwargs = {"client": runtime.client(self.telemetry, self._transport)}
        if self._sleep is not None:
            kwargs["sleep"] = self._sleep
        return VariantInterpreter(
            runtime.groq_settings(self._api_key, self._variant.model, self._generation),
            prompt_version=self._variant.prompt_version,
            generation=self._generation,
            **kwargs,
        )

    def predict(
        self, value: InterpretWeeklyReviewRequest
    ) -> Prediction[InsightsOutput]:
        if self._cases:
            self._case_sleep(runtime.RUNTIME["inter_case_delay_seconds"])
        self._cases += 1
        return asyncio.run(self._predict(value))

    async def _predict(
        self, value: InterpretWeeklyReviewRequest
    ) -> Prediction[InsightsOutput]:
        interpreter = self.interpreter()
        self.telemetry.logical_requests += 1
        try:
            insights = await interpreter.interpret(value)
        except InterpretationFailed:
            return Prediction(InsightsOutput.invalid("invalid_output"))
        finally:
            await interpreter.aclose()
        return Prediction(InsightsOutput.from_insights(insights))

    def experiment_metadata(self) -> dict:
        return {
            "reproducibility": "experiment identity; probabilistic outputs",
            "telemetry": self.telemetry.summary(),
        }
