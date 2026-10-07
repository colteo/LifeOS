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
class Completion:
    content: str
    attempts: int


class CompletionFailed(Exception):
    """No usable completion. `unavailable`: try again later; otherwise the provider rejected the
    request or answered with something that is not a complete message. `outcome` is a short
    diagnostic code (never provider text)."""

    def __init__(self, outcome: str, attempts: int, *, unavailable: bool):
        super().__init__(outcome)
        self.outcome = outcome
        self.attempts = attempts
        self.unavailable = unavailable


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
                    return Completion(self._content(response, attempt), attempt)
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
    def _content(response: httpx.Response, attempt: int) -> str:
        try:
            choice = response.json()["choices"][0]
            content = choice["message"]["content"]
            finished = choice.get("finish_reason") in (None, "stop")
        except (ValueError, KeyError, IndexError, TypeError) as error:
            raise CompletionFailed("invalid_output", attempt, unavailable=False) from error
        if not isinstance(content, str) or not finished:
            raise CompletionFailed("invalid_output", attempt, unavailable=False)
        return content

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
