"""Shared live runtime for production-package evaluators: settings, telemetry, pacing.

`ObservingTransport` wraps the HTTP transport handed to the production Groq client. It
records, per provider attempt: latency, HTTP status (or the exception type), provider
token counts and the names of any tool calls in the answer. It never stores prompts,
message content, arguments or response bodies. Retries stay the production transport's
policy (lifeos_ai.groq_chat).
"""

import json
import math
import os
import time
from collections.abc import Callable
from dataclasses import dataclass, field

import httpx
from lifeos_ai.groq_chat import GROQ_BASE_URL, GroqSettings

# Exactly the production request settings a variant may change. Everything else in the
# request body (messages, schema, tools, tool_choice) comes from the production adapter.
GENERATION_KEYS = frozenset(
    {"temperature", "max_completion_tokens", "reasoning_effort", "include_reasoning"}
)
TOKEN_KEYS = ("input_tokens", "output_tokens", "total_tokens")
PROVIDER_TOKEN_KEYS = {
    "input_tokens": "prompt_tokens",
    "output_tokens": "completion_tokens",
    "total_tokens": "total_tokens",
}

# Lab runtime (not experiment identity): the production per-attempt timeout and attempt
# count, but a longer Retry-After cap so free-tier rate limits wait instead of failing a
# case, and a pause between cases to spread quota. Recorded in each run's system
# configuration.
RUNTIME = {
    "timeout_seconds": 15.0,
    "max_attempts": 3,
    "max_retry_wait_seconds": 60.0,
    "inter_case_delay_seconds": 3.0,
}


def validate_generation_settings(settings) -> dict:
    if not isinstance(settings, dict) or set(settings) != GENERATION_KEYS:
        raise ValueError(
            f"generation_settings requires exactly {sorted(GENERATION_KEYS)}"
        )
    temperature = settings["temperature"]
    if type(temperature) not in (int, float) or not 0 <= temperature <= 2:
        raise ValueError("temperature must be between zero and two")
    tokens = settings["max_completion_tokens"]
    if type(tokens) is not int or not 1 <= tokens <= 65_536:
        raise ValueError("max_completion_tokens must be a positive integer")
    if settings["reasoning_effort"] not in ("low", "medium", "high"):
        raise ValueError("reasoning_effort must be low, medium or high")
    if settings["include_reasoning"] is not False:
        raise ValueError(
            "include_reasoning must be false: reasoning traces are never collected"
        )
    return dict(settings)


def apply_generation_settings(body: dict, settings: dict) -> dict:
    """The production body with the variant's generation settings (identical for the
    control)."""
    missing = GENERATION_KEYS - set(body)
    if missing:
        raise ValueError(
            f"production request body lacks {sorted(missing)}; adapter drift"
        )
    return {**body, **settings}


def replace_system_prompt(body: dict, prompt: str) -> dict:
    messages = list(body["messages"])
    if not messages or messages[0].get("role") != "system":
        raise ValueError(
            "production request body has no leading system message; adapter drift"
        )
    messages[0] = {**messages[0], "content": prompt}
    return {**body, "messages": messages}


def api_key_from_environment() -> str:
    key = os.environ.get("GROQ_API_KEY", "").strip()
    if not key:
        raise ValueError("GROQ_API_KEY is required for live evaluation")
    return key


def groq_settings(api_key: str, model: str, generation: dict) -> GroqSettings:
    return GroqSettings(
        api_key=api_key,
        model=model,
        base_url=GROQ_BASE_URL,
        timeout_seconds=RUNTIME["timeout_seconds"],
        max_attempts=RUNTIME["max_attempts"],
        max_retry_wait_seconds=RUNTIME["max_retry_wait_seconds"],
        max_completion_tokens=generation["max_completion_tokens"],
    )


@dataclass
class Attempt:
    latency_seconds: float
    status: str
    usage: dict | None = None
    tool_names: tuple[str, ...] = ()


@dataclass
class Telemetry:
    """Attempts of one run. `logical_requests` counts production adapter calls (one per
    case for the weekly review, one per agent step); attempts beyond them are transport
    retries."""

    attempts: list[Attempt] = field(default_factory=list)
    logical_requests: int = 0

    def mark(self) -> int:
        return len(self.attempts)

    def since(self, mark: int) -> list[Attempt]:
        return self.attempts[mark:]

    def summary(self) -> dict:
        samples = sorted(attempt.latency_seconds for attempt in self.attempts)
        answered = [
            attempt for attempt in self.attempts if attempt.status.startswith("http_")
        ]
        statuses: dict[str, int] = {}
        for attempt in self.attempts:
            statuses[attempt.status] = statuses.get(attempt.status, 0) + 1
        tokens = {}
        for key in TOKEN_KEYS:
            counts = [
                attempt.usage[key]
                for attempt in answered
                if attempt.usage is not None and attempt.usage.get(key) is not None
            ]
            tokens[key] = {
                "responses": len(counts),
                "total": sum(counts) if counts else None,
            }
        with_usage = sum(attempt.usage is not None for attempt in answered)
        return {
            "logical_requests": self.logical_requests,
            "request_attempts": len(self.attempts),
            "retries": max(0, len(self.attempts) - self.logical_requests),
            "attempt_status_counts": dict(sorted(statuses.items())),
            "latency_seconds": {
                "scope": (
                    "one provider attempt, including failures; "
                    "excludes retry/pacing waits"
                ),
                "samples": len(samples),
                "min": samples[0] if samples else None,
                "mean": sum(samples) / len(samples) if samples else None,
                "p50": percentile(samples, 0.5),
                "p95": percentile(samples, 0.95),
                "max": samples[-1] if samples else None,
            },
            "token_usage": tokens,
            "usage_coverage": {
                "answered_attempts": len(answered),
                "attempts_with_usage": with_usage,
                "rate": with_usage / len(answered) if answered else None,
            },
            "cost": None,
            "cost_note": (
                "not computed: no versioned pricing source; report tokens instead"
            ),
        }


def percentile(sorted_samples: list[float], fraction: float) -> float | None:
    """Nearest-rank percentile; None without samples."""
    if not sorted_samples:
        return None
    rank = max(1, math.ceil(fraction * len(sorted_samples)))
    return sorted_samples[min(rank, len(sorted_samples)) - 1]


def provider_usage(body) -> dict | None:
    usage = body.get("usage") if isinstance(body, dict) else None
    if not isinstance(usage, dict):
        return None
    found = {}
    for key, provider_key in PROVIDER_TOKEN_KEYS.items():
        value = usage.get(provider_key)
        found[key] = value if type(value) is int and value >= 0 else None
    return found if any(value is not None for value in found.values()) else None


def tool_call_names(body) -> tuple[str, ...]:
    try:
        calls = body["choices"][0]["message"].get("tool_calls") or []
        return tuple(
            call["function"]["name"]
            if isinstance(call, dict)
            and isinstance(call.get("function"), dict)
            and isinstance(call["function"].get("name"), str)
            else "<malformed>"
            for call in calls
        )
    except (KeyError, IndexError, TypeError, AttributeError):
        return ()


class ObservingTransport(httpx.AsyncBaseTransport):
    """Records one Attempt per provider request; content passes through untouched.
    `usage` reads the provider's token counts from a decoded body (default: the
    OpenAI-compatible `usage` object of Groq)."""

    def __init__(
        self,
        telemetry: Telemetry,
        inner: httpx.AsyncBaseTransport | None = None,
        clock: Callable[[], float] = time.perf_counter,
        usage: Callable[[object], dict | None] = provider_usage,
    ):
        self._telemetry = telemetry
        self._inner = inner or httpx.AsyncHTTPTransport()
        self._clock = clock
        self._usage = usage

    async def handle_async_request(self, request: httpx.Request) -> httpx.Response:
        started = self._clock()
        try:
            response = await self._inner.handle_async_request(request)
            content = await response.aread()
        except Exception as exc:
            self._telemetry.attempts.append(
                Attempt(self._clock() - started, type(exc).__name__)
            )
            raise
        latency = self._clock() - started
        try:
            body = json.loads(content)
        except (ValueError, UnicodeDecodeError):
            body = None
        self._telemetry.attempts.append(
            Attempt(
                latency,
                f"http_{response.status_code}",
                self._usage(body),
                tool_call_names(body),
            )
        )
        # The same response, already read: httpx keeps the decoded body cached, so the
        # client's own read returns it. Rebuilding one from the decoded bytes with the
        # original Content-Encoding header would decode it a second time.
        return response

    async def aclose(self) -> None:
        await self._inner.aclose()


def client(telemetry: Telemetry, inner: httpx.AsyncBaseTransport | None = None):
    """The HTTP client handed to a production adapter (same base URL/timeout it would
    build)."""
    return httpx.AsyncClient(
        base_url=GROQ_BASE_URL,
        timeout=RUNTIME["timeout_seconds"],
        transport=ObservingTransport(telemetry, inner),
    )
