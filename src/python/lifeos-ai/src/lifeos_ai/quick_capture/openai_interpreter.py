"""AI-009 I1: the OpenAI adapter of Quick Capture (prepared, not selectable: config.py).

It reuses the Assistant adapter's transport layer (`ResponsesCaller`: HTTPS transport with retries
inside the deadline, spend guard, concurrency limit) and envelope reading (`response_text`), and
its settings (`OpenAIAssistantSettings`: model, region, budgets, prices, effort, timeouts,
attempts, output tokens). It can share one caller with the Assistant adapter, so both draw on the
same budget and the same concurrency slots.

Every request is the Assistant's: `store: false`, no remote state, tool, identifier or media, a
frozen prompt per role (prompt.py) and strict structured output over the role's contract schema
(integer `enum` dropped as for the Assistant). Each role has its own deadline (ADR-015 R-5,
proposed: router 25 s, specialist 35 s, inside the 90 s turn lease).

The answer is held to the role's contract; an invalid one is refused whole (never repaired) with
the Assistant's error mapping: refusal -> 502 interpretation_refused; truncated, empty, extra
items, invalid JSON, schema or rule violation -> 502 interpretation_failed; timeout, rate limit,
5xx, credentials, budget, concurrency, deadline -> 503 provider_unavailable.

Logs: one line per call with outcome, role, identity, attempts, latency and token counts. Never
the message, segment, spans, categories, output, provider error text or key.
"""

import asyncio
import json
import logging
import time
from collections.abc import Callable

from pydantic import ValidationError

from lifeos_ai.assistant.budget import SpendGuard
from lifeos_ai.assistant.interpreter import InterpretationFailed, InterpretationRefused
from lifeos_ai.assistant.openai_interpreter import (
    OpenAIAssistantSettings,
    ProviderOutcome,
    ResponsesCaller,
    Usage,
    provider_output_schema,
    response_text,
)
from lifeos_ai.assistant.openai_transport import ResponsesTransport
from lifeos_ai.assistant.schema import ProtocolViolation
from lifeos_ai.errors import ProviderUnavailable
from lifeos_ai.quick_capture.prompt import DEFAULT_PROMPTS, build_input
from lifeos_ai.quick_capture.router import (
    ROUTER_JSON_SCHEMA,
    RouteRequest,
    RouterOutput,
    validate_route,
)
from lifeos_ai.quick_capture.specialists import (
    FINANCE_JSON_SCHEMA,
    HYDRATION_JSON_SCHEMA,
    MEAL_JSON_SCHEMA,
    FinanceInterpretation,
    FinanceInterpretRequest,
    HydrationInterpretation,
    HydrationInterpretRequest,
    MealInterpretation,
    MealInterpretRequest,
    SpecialistInterpretation,
    SpecialistRequest,
    validate_specialist,
)

ROUTER_DEADLINE_SECONDS = 25.0
SPECIALIST_DEADLINE_SECONDS = 35.0

# Per role: the strict schema sent to the provider and the model its answer is read with.
_OUTPUTS = {
    "router": (provider_output_schema(ROUTER_JSON_SCHEMA), RouterOutput),
    "finance": (provider_output_schema(FINANCE_JSON_SCHEMA), FinanceInterpretation),
    "hydration": (provider_output_schema(HYDRATION_JSON_SCHEMA), HydrationInterpretation),
    "meal": (provider_output_schema(MEAL_JSON_SCHEMA), MealInterpretation),
}
PROVIDER_OUTPUT_SCHEMAS: dict[str, dict] = {role: schema for role, (schema, _) in _OUTPUTS.items()}

logger = logging.getLogger("lifeos_ai.quick_capture")


def role_of(request: RouteRequest | SpecialistRequest) -> str:
    if isinstance(request, RouteRequest):
        return "router"
    if isinstance(request, FinanceInterpretRequest):
        return "finance"
    if isinstance(request, HydrationInterpretRequest):
        return "hydration"
    if isinstance(request, MealInterpretRequest):
        return "meal"
    raise TypeError("not a Quick Capture request")


class OpenAIQuickCaptureInterpreter:
    provider = "openai"
    configured = True

    def __init__(
        self,
        settings: OpenAIAssistantSettings,
        *,
        caller: ResponsesCaller | None = None,
        transport: ResponsesTransport | None = None,
        guard: SpendGuard | None = None,
        clock: Callable[[], float] = time.monotonic,
        router_deadline_seconds: float = ROUTER_DEADLINE_SECONDS,
        specialist_deadline_seconds: float = SPECIALIST_DEADLINE_SECONDS,
    ):
        if caller is not None and (transport is not None or guard is not None):
            raise ValueError("a shared caller already has its transport and guard")
        if not (0 < router_deadline_seconds and 0 < specialist_deadline_seconds):
            raise ValueError("deadlines must be positive")
        self.model = settings.model
        self.prompt_versions = dict(DEFAULT_PROMPTS)
        self._settings = settings
        # A shared caller belongs to whoever built it: closed by its owner, not here.
        self._owns_caller = caller is None
        self._caller = caller or ResponsesCaller(settings, transport=transport, guard=guard)
        self._clock = clock
        self._deadlines = {
            "router": router_deadline_seconds,
            "finance": specialist_deadline_seconds,
            "hydration": specialist_deadline_seconds,
            "meal": specialist_deadline_seconds,
        }

    def request_body(self, request: RouteRequest | SpecialistRequest) -> dict:
        role = role_of(request)
        return {
            "model": self.model,
            "input": build_input(request, self.prompt_versions[role]),
            "store": False,
            "reasoning": {"effort": self._settings.reasoning_effort},
            "max_output_tokens": self._settings.max_output_tokens,
            "text": {
                "format": {
                    "type": "json_schema",
                    "name": f"quick_capture_{role}",
                    "strict": True,
                    "schema": PROVIDER_OUTPUT_SCHEMAS[role],
                }
            },
        }

    async def route(self, request: RouteRequest) -> RouterOutput:
        def check(output: RouterOutput) -> None:
            validate_route(output, request.message, request.context.max_segments)

        return await self._call(request, check)

    async def interpret(self, request: SpecialistRequest) -> SpecialistInterpretation:
        def check(output: SpecialistInterpretation) -> None:
            validate_specialist(output, request.mode, request.segment, request.context_spans)

        return await self._call(request, check)

    async def aclose(self) -> None:
        if self._owns_caller:
            await self._caller.aclose()

    async def _call(self, request, check):
        role = role_of(request)
        started = self._clock()
        deadline = started + self._deadlines[role]
        usage = Usage()
        attempts = 0
        try:
            body, attempts, usage = await self._caller.send(
                self.request_body(request), deadline, self._clock
            )
            output = _decode(body, attempts, self.model, role, check)
        except ProviderOutcome as failure:
            self._log(failure.outcome, role, failure.attempts or attempts, started, usage)
            if failure.kind == "refused":
                raise InterpretationRefused("the provider refused to interpret") from None
            if failure.kind == "failed":
                raise InterpretationFailed(
                    "the provider output is not a valid interpretation"
                ) from None
            raise ProviderUnavailable(
                f"quick capture provider unavailable ({failure.outcome})"
            ) from None
        except asyncio.CancelledError:
            self._log("cancelled", role, attempts, started, usage)
            raise
        self._log("ok", role, attempts, started, usage)
        return output

    def _log(self, outcome: str, role: str, attempts: int, started: float, usage: Usage) -> None:
        # Diagnostics only: never the message, segment, spans, categories, output or provider text.
        logger.info(
            "quick_capture openai outcome=%s role=%s provider=%s model=%s prompt_version=%s "
            "attempts=%d seconds=%.3f %s",
            outcome,
            role,
            self.provider,
            self.model,
            self.prompt_versions[role],
            attempts,
            self._clock() - started,
            usage.log_fields(),
        )


def _decode(body: dict, attempts: int, model: str, role: str, check):
    """The role's output, held to its contract; a ProviderOutcome otherwise (never repaired)."""
    text = response_text(body, attempts, model)
    try:
        data = json.loads(text)
    except json.JSONDecodeError:
        raise ProviderOutcome("invalid_json", "failed", attempts) from None
    try:
        output = _OUTPUTS[role][1].model_validate(data)
    except ValidationError:
        raise ProviderOutcome("schema_invalid", "failed", attempts) from None
    try:
        check(output)
    except ProtocolViolation as violation:
        # The rule name only (e.g. segment_span_not_in_message), never a value.
        raise ProviderOutcome(f"protocol_violation:{violation.code}", "failed", attempts) from None
    return output
