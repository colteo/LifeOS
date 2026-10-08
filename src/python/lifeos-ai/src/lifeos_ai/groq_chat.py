"""Groq's OpenAI-compatible Chat Completions API over plain HTTPS (no SDK), shared by capabilities.

Owns the transport policy proven in AI-EVAL-002/003 and ADR-011: finite timeouts, a bounded number
of attempts, retries only for timeouts, transport errors, 429 and 5xx, Retry-After honoured up to a
cap. Each capability builds its own request body (prompt, strict json_schema) and validates the
returned content itself; this module never sees what the content means.
"""

import asyncio
import math
from collections.abc import Awaitable, Callable
from dataclasses import dataclass
from datetime import UTC, datetime
from email.utils import parsedate_to_datetime

import httpx

GROQ_BASE_URL = "https://api.groq.com/openai/v1"
DEFAULT_MODEL = "openai/gpt-oss-20b"

# Transient: rate limiting and server-side failures. Anything else is final.
RETRYABLE_STATUS = frozenset({429, 500, 502, 503, 504})


@dataclass(frozen=True)
class GroqSettings:
    api_key: str
    model: str = DEFAULT_MODEL
    base_url: str = GROQ_BASE_URL
    timeout_seconds: float = 15.0
    max_attempts: int = 3
    max_retry_wait_seconds: float = 4.0
    max_completion_tokens: int = 2048


@dataclass(frozen=True)
class TokenUsage:
    """Token counts the provider reported for one answered request (AI-003 telemetry).

    A count the provider did not report is None, never a guessed zero. Counts only: no content."""

    input_tokens: int | None
    output_tokens: int | None
    total_tokens: int | None

    @classmethod
    def from_response(cls, response: httpx.Response) -> "TokenUsage | None":
        try:
            usage = response.json().get("usage")
        except (ValueError, AttributeError):
            return None
        if not isinstance(usage, dict):
            return None

        def count(key: str) -> int | None:
            value = usage.get(key)
            return value if type(value) is int and value >= 0 else None

        found = cls(count("prompt_tokens"), count("completion_tokens"), count("total_tokens"))
        return None if found == cls(None, None, None) else found


@dataclass(frozen=True)
class Completion:
    content: str
    attempts: int
    usage: TokenUsage | None = None


@dataclass(frozen=True)
class MessageCompletion:
    message: dict
    attempts: int
    usage: TokenUsage | None = None


def usage_log_fields(usage: TokenUsage | None) -> str:
    """Log fragment for token counts; '-' when the provider did not report a count."""
    values = usage or TokenUsage(None, None, None)
    return " ".join(
        f"{name}={'-' if count is None else count}"
        for name, count in (
            ("input_tokens", values.input_tokens),
            ("output_tokens", values.output_tokens),
            ("total_tokens", values.total_tokens),
        )
    )


class CompletionFailed(Exception):
    """No usable completion. `unavailable`: try again later; otherwise the provider rejected the
    request or answered with something that is not a complete message. `outcome` is a short
    diagnostic code (never provider text). `usage`: token counts when the provider answered (e.g. a
    truncated message), otherwise None."""

    def __init__(
        self, outcome: str, attempts: int, *, unavailable: bool, usage: "TokenUsage | None" = None
    ):
        super().__init__(outcome)
        self.outcome = outcome
        self.attempts = attempts
        self.unavailable = unavailable
        self.usage = usage


class GroqChatCompletions:
    provider = "groq"

    def __init__(
        self,
        settings: GroqSettings,
        *,
        client: httpx.AsyncClient | None = None,
        sleep: Callable[[float], Awaitable[None]] = asyncio.sleep,
    ):
        if not settings.api_key.strip():
            raise ValueError("GROQ_API_KEY is required")
        if settings.max_attempts < 1:
            raise ValueError("max_attempts must be at least 1")
        self._settings = settings
        self._client = client or httpx.AsyncClient(
            base_url=settings.base_url, timeout=settings.timeout_seconds
        )
        self._sleep = sleep

    async def complete(self, body: dict) -> Completion:
        response, attempt = await self._send(body)
        usage = TokenUsage.from_response(response)
        return Completion(self._content(response, attempt, usage), attempt, usage)

    async def complete_message(self, body: dict) -> MessageCompletion:
        """AI-002: the whole assistant message (content and/or tool calls), for tool use."""
        response, attempt = await self._send(body)
        usage = TokenUsage.from_response(response)
        return MessageCompletion(self._message(response, attempt, usage), attempt, usage)

    async def _send(self, body: dict) -> tuple[httpx.Response, int]:
        headers = {"Authorization": f"Bearer {self._settings.api_key}"}
        attempts = self._settings.max_attempts

        for attempt in range(1, attempts + 1):
            try:
                response = await self._client.post(
                    "chat/completions",
                    json=body,
                    headers=headers,
                    timeout=self._settings.timeout_seconds,
                )
            except httpx.TimeoutException:
                outcome, delay = "timeout", self._backoff(attempt)
            except httpx.TransportError:
                outcome, delay = "transport_error", self._backoff(attempt)
            else:
                if response.status_code == 200:
                    return response, attempt
                if response.status_code not in RETRYABLE_STATUS:
                    # 400: the request or the generated output was rejected (e.g. schema
                    # validation). Credential or model problems make the provider unusable.
                    raise CompletionFailed(
                        f"http_{response.status_code}",
                        attempt,
                        unavailable=response.status_code != 400,
                    )
                outcome = f"http_{response.status_code}"
                delay = self._retry_delay(response, attempt)

            if attempt == attempts or delay is None:
                raise CompletionFailed(outcome, attempt, unavailable=True)
            await self._sleep(delay)

        raise AssertionError("unreachable")  # pragma: no cover

    async def aclose(self) -> None:
        await self._client.aclose()

    @staticmethod
    def _content(response: httpx.Response, attempt: int, usage: TokenUsage | None = None) -> str:
        try:
            choice = response.json()["choices"][0]
            content = choice["message"]["content"]
            finished = choice.get("finish_reason") in (None, "stop")
        except (ValueError, KeyError, IndexError, TypeError) as error:
            raise CompletionFailed(
                "invalid_output", attempt, unavailable=False, usage=usage
            ) from error
        if not isinstance(content, str) or not finished:
            raise CompletionFailed("invalid_output", attempt, unavailable=False, usage=usage)
        return content

    @staticmethod
    def _message(response: httpx.Response, attempt: int, usage: TokenUsage | None = None) -> dict:
        try:
            choice = response.json()["choices"][0]
            message = choice["message"]
            finished = choice.get("finish_reason") in (None, "stop", "tool_calls")
        except (ValueError, KeyError, IndexError, TypeError) as error:
            raise CompletionFailed(
                "invalid_output", attempt, unavailable=False, usage=usage
            ) from error
        if not isinstance(message, dict) or not finished:
            raise CompletionFailed("invalid_output", attempt, unavailable=False, usage=usage)
        return message

    def _backoff(self, attempt: int) -> float:
        return min(float(2 ** (attempt - 1)), self._settings.max_retry_wait_seconds)

    def _retry_delay(self, response: httpx.Response, attempt: int) -> float | None:
        # Honour Retry-After, but never wait longer than the cap: give up instead.
        header = response.headers.get("retry-after")
        if header is None:
            return self._backoff(attempt)
        try:
            delay = float(header)
        except ValueError:
            try:
                delay = (parsedate_to_datetime(header) - datetime.now(UTC)).total_seconds()
            except (TypeError, ValueError, OverflowError):
                return self._backoff(attempt)
        if not math.isfinite(delay) or delay > self._settings.max_retry_wait_seconds:
            return None
        return max(0.0, delay)
