"""AI-007 F3-B1: the OpenAI adapter of LifeOS Assistant (Responses API, plain HTTPS, no SDK).

Selected only by explicit configuration (`LIFEOS_AI_ASSISTANT_PROVIDER=openai`, config.py); the
default stays `disabled`. Release settings (owner decisions, 2026-10-26): model `gpt-6-luna`, prompt
`assistant-finance-interpret-v2`.

Every request is stateless, private and read-only:
- `store: false` always sent explicitly (the Responses API stores by default);
- no `previous_response_id`, `conversation`, `background`, `tools`, `tool_choice`, `include`,
  `metadata`, `user`, `safety_identifier` or `prompt_cache_key`: no remote history, no tool, no
  write capability, no LifeOS identifier; text input only (no file, audio or image);
- a frozen prompt version (prompt.py; production configuration selects
  `assistant-finance-interpret-v2`, the evaluation runner the experiment's) and strict structured
  output over
  `PROVIDER_OUTPUT_SCHEMA`, the canonical schema mapped to the documented Structured Outputs subset;
- the response id is never read or kept; the model that answered must be the configured one.

The answer is then held to the request's protocol (v1, or v2 since AI-008: `parse_provider_output`)
and validated again by .NET. A protocol-2 request is answered with `prompt_version_v2`
(`assistant-interpret-v3` by default) and the v2 output schema; a protocol-1 request keeps the
configured prompt and the v1 schema, unchanged.

Failures keep a distinct outcome code in the log and map to the canonical HTTP errors of
docs/contracts/assistant-interpretation/v1 (unchanged):
- refusal (a `refusal` part, or `incomplete` for `content_filter`) -> InterpretationRefused (502);
- truncated (`incomplete`/`max_output_tokens`), empty, extra items, invalid JSON, schema or protocol
  violation -> InterpretationFailed (502);
- timeout, rate limit, quota, 5xx, connection, credentials, permissions, rejected request or model,
  budget exhausted, no concurrency slot, deadline -> ProviderUnavailable (503).

Logs: one line per interpretation with outcome, identity, attempts, latency and token counts.
Never the message, categories, output, provider error text or key.
"""

import asyncio
import copy
import json
import logging
import re
import time
from collections.abc import Callable
from dataclasses import dataclass, field
from decimal import Decimal

from pydantic import ValidationError

from lifeos_ai.assistant import schema_v2
from lifeos_ai.assistant.budget import BudgetExhausted, Prices, Reservation, SpendGuard
from lifeos_ai.assistant.interpreter import InterpretationFailed, InterpretationRefused
from lifeos_ai.assistant.openai_transport import (
    HttpResponsesTransport,
    ResponsesTransport,
    TransportFailure,
)
from lifeos_ai.assistant.prompt import (
    PROMPT_PROTOCOLS,
    PROMPT_TEXTS,
    PROMPT_V3_VERSION,
    PROMPT_VERSION,
    build_input,
)
from lifeos_ai.assistant.schema import (
    INTERPRETATION_JSON_SCHEMA,
    AssistantInterpretation,
    InterpretRequest,
    ProtocolViolation,
    validate_interpretation,
)
from lifeos_ai.errors import ProviderUnavailable
from lifeos_ai.journal_memory.openai_http import usage_count

SCHEMA_NAME = "assistant_interpretation"

# OpenAI hosts by region (data-controls guide: EU regional processing at eu.api.openai.com needs
# an approved project; "global" is the default host). No other host can receive the key.
REGION_BASE_URLS = {
    "global": "https://api.openai.com/v1",
    "eu": "https://eu.api.openai.com/v1",
}
REASONING_EFFORTS = ("none", "minimal", "low", "medium", "high")
# The suffix of a dated snapshot of the configured model, e.g. "-2026-10-01" (optional).
_SNAPSHOT = r"(-\d{4}-\d{2}-\d{2})?"

logger = logging.getLogger("lifeos_ai.assistant")


def provider_output_schema(canonical: dict = INTERPRETATION_JSON_SCHEMA) -> dict:
    """The canonical output schema restricted to the documented strict Structured Outputs subset.

    The Structured Outputs guide (read 2026-10-25) lists string/number/integer/boolean/object/
    array, `enum`, `anyOf`, nullable type arrays, and requires every property `required` and
    `additionalProperties: false`. It documents `enum` with string values (and `null` in a nullable
    union) but not an integer `enum`, so `protocol_version` is sent as a plain integer; protocol
    rule `protocol_version` still refuses anything but 1. No other difference.
    """
    schema = copy.deepcopy(canonical)
    schema["properties"]["protocol_version"] = {"type": "integer"}
    return schema


PROVIDER_OUTPUT_SCHEMA = provider_output_schema()
PROVIDER_OUTPUT_SCHEMA_V2 = provider_output_schema(schema_v2.INTERPRETATION_JSON_SCHEMA)


@dataclass(frozen=True)
class OpenAIAssistantSettings:
    """Validated by config.openai_assistant_settings_from_environment; no default for the key,
    the model, the region, the prices or the budgets (nothing is guessed)."""

    api_key: str = field(repr=False)
    model: str
    region: str
    daily_budget_usd: Decimal
    monthly_budget_usd: Decimal
    prices: Prices
    reasoning_effort: str = "low"
    # Per attempt; all attempts and waits fit `deadline_seconds`, which must stay below
    # AssistantAi:TimeoutSeconds on the .NET side (default 45 s, inside the 90 s turn lease).
    timeout_seconds: float = 20.0
    deadline_seconds: float = 40.0
    max_attempts: int = 2
    max_output_tokens: int = 2000
    max_concurrency: int = 2
    # A published prompt version (prompt.PROMPTS). Production configuration sets the release
    # version (config.ASSISTANT_RELEASE_PROMPT_VERSION, v2); the live evaluation runner sets it from
    # the experiment file (AI-007 F3-B2).
    prompt_version: str = PROMPT_VERSION
    # AI-008: the prompt of protocol-2 requests (a published prompt of protocol 2).
    prompt_version_v2: str = PROMPT_V3_VERSION

    @property
    def base_url(self) -> str:
        return REGION_BASE_URLS[self.region]


@dataclass(frozen=True)
class Usage:
    input_tokens: int | None = None
    cached_input_tokens: int | None = None
    output_tokens: int | None = None
    reasoning_tokens: int | None = None

    @classmethod
    def from_body(cls, body: dict) -> "Usage":
        usage = body.get("usage")
        return cls(
            usage_count(usage, "input_tokens"),
            usage_count(usage, "input_tokens_details", "cached_tokens"),
            usage_count(usage, "output_tokens"),
            usage_count(usage, "output_tokens_details", "reasoning_tokens"),
        )

    def log_fields(self) -> str:
        return " ".join(
            f"{name}={'-' if value is None else value}"
            for name, value in (
                ("input_tokens", self.input_tokens),
                ("cached_input_tokens", self.cached_input_tokens),
                ("output_tokens", self.output_tokens),
                ("reasoning_tokens", self.reasoning_tokens),
            )
        )


class _Outcome(Exception):
    """An internal, classified failure; mapped to a ServiceError at the boundary."""

    def __init__(self, outcome: str, kind: str, attempts: int = 0):
        super().__init__(outcome)
        self.outcome = outcome
        self.kind = kind  # "unavailable" | "failed" | "refused"
        self.attempts = attempts


class OpenAIAssistantInterpreter:
    provider = "openai"
    configured = True

    def __init__(
        self,
        settings: OpenAIAssistantSettings,
        *,
        transport: ResponsesTransport | None = None,
        guard: SpendGuard | None = None,
        clock: Callable[[], float] = time.monotonic,
    ):
        if (
            settings.prompt_version not in PROMPT_TEXTS
            or settings.prompt_version_v2 not in PROMPT_TEXTS
        ):
            raise ValueError("unknown prompt version")
        if (
            PROMPT_PROTOCOLS[settings.prompt_version] != 1
            or PROMPT_PROTOCOLS[settings.prompt_version_v2] != 2
        ):
            raise ValueError("prompt version of the wrong protocol")
        self.model = settings.model
        self.prompt_version = settings.prompt_version
        self.prompt_version_v2 = settings.prompt_version_v2
        self._settings = settings
        # Building the transport opens no connection: nothing is sent before the first request.
        self._transport = transport or HttpResponsesTransport(
            api_key=settings.api_key,
            base_url=settings.base_url,
            timeout_seconds=settings.timeout_seconds,
            max_attempts=settings.max_attempts,
        )
        self._guard = guard or SpendGuard(
            daily_budget=settings.daily_budget_usd, monthly_budget=settings.monthly_budget_usd
        )
        self._slots = asyncio.Semaphore(settings.max_concurrency)
        self._clock = clock

    def _prompt_for(self, request) -> str:
        return self.prompt_version_v2 if request.protocol_version == 2 else self.prompt_version

    def request_body(self, request: InterpretRequest | schema_v2.InterpretRequest) -> dict:
        schema = (
            PROVIDER_OUTPUT_SCHEMA_V2 if request.protocol_version == 2 else PROVIDER_OUTPUT_SCHEMA
        )
        return {
            "model": self.model,
            "input": build_input(request, self._prompt_for(request)),
            "store": False,
            "reasoning": {"effort": self._settings.reasoning_effort},
            "max_output_tokens": self._settings.max_output_tokens,
            "text": {
                "format": {
                    "type": "json_schema",
                    "name": SCHEMA_NAME,
                    "strict": True,
                    "schema": schema,
                }
            },
        }

    def worst_case_cost(self, body: dict) -> Decimal:
        """Upper bound for one interpretation: every attempt billed with the body's UTF-8 size as
        input tokens (a token is at least one byte) and the full output allowance."""
        return self._attempt_bound(body) * self._settings.max_attempts

    def _attempt_bound(self, body: dict) -> Decimal:
        input_bound = len(json.dumps(body, ensure_ascii=False).encode("utf-8"))
        return self._settings.prices.cost(input_bound, self._settings.max_output_tokens)

    async def interpret(
        self, request: InterpretRequest | schema_v2.InterpretRequest
    ) -> AssistantInterpretation | schema_v2.AssistantInterpretation:
        started = self._clock()
        deadline = started + self._settings.deadline_seconds
        usage = Usage()
        attempts = 0
        prompt = self._prompt_for(request)
        try:
            body, attempts, usage = await self._send(self.request_body(request), deadline)
            interpretation = _interpretation(
                body, attempts, self.model, request.message, request.protocol_version
            )
        except _Outcome as failure:
            self._log(failure.outcome, failure.attempts or attempts, started, usage, prompt)
            if failure.kind == "refused":
                raise InterpretationRefused("the provider refused to interpret") from None
            if failure.kind == "failed":
                raise InterpretationFailed(
                    "the provider output is not a valid interpretation"
                ) from None
            raise ProviderUnavailable(
                f"assistant provider unavailable ({failure.outcome})"
            ) from None
        except asyncio.CancelledError:
            self._log("cancelled", attempts, started, usage, prompt)
            raise
        self._log("ok", attempts, started, usage, prompt)
        return interpretation

    async def aclose(self) -> None:
        await self._transport.aclose()

    async def _send(self, body: dict, deadline: float) -> tuple[dict, int, Usage]:
        try:
            await asyncio.wait_for(
                self._slots.acquire(), timeout=max(0.0, deadline - self._clock())
            )
        except TimeoutError:
            raise _Outcome("concurrency_limit", "unavailable") from None
        try:
            try:
                reservation = self._guard.reserve(self.worst_case_cost(body))
            except BudgetExhausted as exhausted:
                raise _Outcome(f"budget_exhausted_{exhausted.period}", "unavailable") from None
            try:
                response, attempts = await self._transport.create_response(body, deadline=deadline)
            except TransportFailure as failure:
                # Possibly billed (a timeout can still be charged): keep the whole reservation.
                self._guard.settle(reservation, None)
                raise _Outcome(failure.outcome, "unavailable", failure.attempts) from None
            except BaseException:
                self._guard.settle(reservation, None)
                raise
            usage = Usage.from_body(response)
            # The usage covers the answering attempt only; an earlier failed attempt (e.g. a
            # timeout) may still have been billed, so it keeps its worst case.
            earlier = self._attempt_bound(body) * (attempts - 1)
            self._settle(reservation, usage, earlier)
            return response, attempts, usage
        finally:
            self._slots.release()

    def _settle(self, reservation: Reservation, usage: Usage, earlier: Decimal) -> None:
        if usage.input_tokens is None or usage.output_tokens is None:
            self._guard.settle(reservation, None)
            return
        cost = self._settings.prices.cost(usage.input_tokens, usage.output_tokens)
        self._guard.settle(reservation, cost + earlier)

    def _log(
        self, outcome: str, attempts: int, started: float, usage: Usage, prompt_version: str
    ) -> None:
        # Diagnostics only: never the message, the categories, the output or provider text.
        logger.info(
            "assistant openai outcome=%s provider=%s model=%s prompt_version=%s attempts=%d "
            "seconds=%.3f %s",
            outcome,
            self.provider,
            self.model,
            prompt_version,
            attempts,
            self._clock() - started,
            usage.log_fields(),
        )


def _interpretation(
    body: dict, attempts: int, model: str, message: str, protocol_version: int = 1
) -> AssistantInterpretation | schema_v2.AssistantInterpretation:
    def failed(outcome: str, kind: str = "failed") -> _Outcome:
        return _Outcome(outcome, kind, attempts)

    answered = body.get("model")
    if not isinstance(answered, str) or not re.fullmatch(re.escape(model) + _SNAPSHOT, answered):
        # A different model answered: never accepted silently. Only the configured id or one of
        # its dated snapshots ("<model>-YYYY-MM-DD") is the same model; "gpt-6" configured and
        # "gpt-6-luna" answering is another model.
        raise failed("model_mismatch", "unavailable")

    status = body.get("status")
    if status == "incomplete":
        details = body.get("incomplete_details")
        reason = details.get("reason") if isinstance(details, dict) else None
        if reason == "content_filter":
            raise failed("refusal_content_filter", "refused")
        if reason == "max_output_tokens":
            raise failed("incomplete_max_output_tokens")
        raise failed("incomplete")
    if status == "failed":
        raise failed("response_failed", "unavailable")
    if status != "completed":
        raise failed("unexpected_status")

    output = body.get("output")
    if not isinstance(output, list):
        raise failed("empty_output")
    parts = []
    for item in output:
        if not isinstance(item, dict):
            raise failed("unexpected_output_item")
        if item.get("type") == "reasoning":
            continue  # never read
        if item.get("type") != "message" or item.get("role") != "assistant":
            raise failed("unexpected_output_item")  # e.g. a tool call: none was offered
        content = item.get("content")
        if not isinstance(content, list):
            raise failed("unexpected_output_item")
        parts.extend(content)

    if any(isinstance(part, dict) and part.get("type") == "refusal" for part in parts):
        raise failed("refusal", "refused")
    if not parts:
        raise failed("empty_output")
    if len(parts) != 1 or not isinstance(parts[0], dict) or parts[0].get("type") != "output_text":
        raise failed("unexpected_output_item")
    text = parts[0].get("text")
    if not isinstance(text, str) or not text.strip():
        raise failed("empty_output")

    try:
        data = json.loads(text)
    except json.JSONDecodeError:
        raise failed("invalid_json") from None
    output, validate = (
        (schema_v2.AssistantInterpretation, schema_v2.validate_interpretation)
        if protocol_version == 2
        else (AssistantInterpretation, validate_interpretation)
    )
    try:
        interpretation = output.model_validate(data)
    except ValidationError:
        raise failed("schema_invalid") from None
    try:
        validate(interpretation, message)
    except ProtocolViolation as violation:
        # The rule name only (e.g. amount_span_not_in_message), never a value.
        raise failed(f"protocol_violation:{violation.code}") from None
    return interpretation
