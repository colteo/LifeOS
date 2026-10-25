"""AI-007 F3-B1: the replaceable HTTPS transport of the assistant's OpenAI adapter (no SDK).

`ResponsesTransport` is the seam: the adapter needs only "send this Responses body before this
deadline and give me the JSON of the first 200, or a classified failure". Tests replace it (or the
httpx transport underneath) with scripted fakes; nothing here runs at import or start-up.

Why not the journal's `openai_http.OpenAITransport`: the assistant runs inside a .NET turn bounded
by `AssistantAi:TimeoutSeconds` (default 45 s) within a 90 s lease, so every retry must fit one
total deadline, and each failure must keep its own outcome (timeout, rate limit, quota, server
error...). Changing the journal transport would change Journal RAG behaviour approved by ADR-013.

Policy (OpenAI error-codes and rate-limits guides, read 2026-10-25):
- each attempt is bounded as a whole (not only per connect/read/write phase) by
  min(per-attempt timeout, time left before the deadline);
- retried, within the deadline: timeouts and HTTP 408, connection errors, 429 rate limits, 500,
  502, 503, 504 — exponential backoff with jitter, `Retry-After` honoured as a minimum; a wait that
  would not leave time for another attempt gives up instead;
- never retried: 400, 401, 403, 404, other 4xx, and 429 quota/spend/billing codes (retrying them
  cannot succeed);
- only the error `code`/`param` fields of a failure body are read; provider text, request bodies
  and the key are never logged or returned.
"""

import asyncio
import math
import random
import time
from collections.abc import Awaitable, Callable
from datetime import UTC, datetime
from email.utils import parsedate_to_datetime
from typing import Protocol

import httpx

RETRYABLE_STATUS = frozenset({408, 429, 500, 502, 503, 504})

# 429 codes that need the account to change (credits, spend or usage limits): final, not retried.
QUOTA_CODES = frozenset(
    {
        "insufficient_quota",
        "credit_balance_exhausted",
        "organization_spend_limit_exceeded",
        "project_spend_limit_exceeded",
        "organization_usage_limit_exceeded",
    }
)

# Backoff: base * 2^(attempt-1), scaled by a random factor in [0.5, 1.0], capped.
BACKOFF_BASE_SECONDS = 0.5
BACKOFF_CAP_SECONDS = 4.0
# An attempt is started only if at least this much of the deadline is left.
MIN_ATTEMPT_SECONDS = 1.0


class TransportFailure(Exception):
    """No usable 200 response. `outcome` is a short code (never provider text); `retryable`
    tells whether the condition was transient (all attempts or the deadline were used up)."""

    def __init__(self, outcome: str, attempts: int, *, retryable: bool):
        super().__init__(outcome)
        self.outcome = outcome
        self.attempts = attempts
        self.retryable = retryable


class ResponsesTransport(Protocol):
    async def create_response(self, body: dict, *, deadline: float) -> tuple[dict, int]:
        """The JSON object of the first 200 response and its attempt number.

        `deadline` is an absolute `time.monotonic()` value. Raises TransportFailure."""
        ...

    async def aclose(self) -> None: ...


class HttpResponsesTransport:
    """POST {base_url}/responses with a bounded number of attempts inside one deadline."""

    def __init__(
        self,
        *,
        api_key: str,
        base_url: str,
        timeout_seconds: float,
        max_attempts: int,
        client: httpx.AsyncClient | None = None,
        sleep: Callable[[float], Awaitable[None]] = asyncio.sleep,
        clock: Callable[[], float] = time.monotonic,
        jitter: Callable[[], float] = random.random,
    ):
        if not api_key.strip():
            raise ValueError("an API key is required")
        if max_attempts < 1:
            raise ValueError("max_attempts must be at least 1")
        self._api_key = api_key
        self._timeout = timeout_seconds
        self._max_attempts = max_attempts
        self._client = client or httpx.AsyncClient(base_url=base_url, timeout=timeout_seconds)
        self._sleep = sleep
        self._clock = clock
        self._jitter = jitter

    async def create_response(self, body: dict, *, deadline: float) -> tuple[dict, int]:
        headers = {"Authorization": f"Bearer {self._api_key}"}
        outcome = "deadline_exceeded"

        for attempt in range(1, self._max_attempts + 1):
            remaining = deadline - self._clock()
            if remaining < MIN_ATTEMPT_SECONDS:
                raise TransportFailure(outcome, attempt - 1, retryable=True)
            attempt_timeout = min(self._timeout, remaining)
            try:
                # httpx timeouts bound each phase (connect, write, each read) separately; the
                # outer one bounds the whole attempt, so the total deadline really holds.
                async with asyncio.timeout(attempt_timeout):
                    response = await self._client.post(
                        "responses", json=body, headers=headers, timeout=attempt_timeout
                    )
            except (httpx.TimeoutException, TimeoutError):
                outcome, delay = "timeout", self._backoff(attempt)
            except httpx.TransportError:
                outcome, delay = "connection_error", self._backoff(attempt)
            else:
                status = response.status_code
                if status == 200:
                    return _json_object(response, attempt), attempt
                if status not in RETRYABLE_STATUS:
                    raise _final_failure(response, attempt)
                if status == 429 and _error_field(response, "code") in QUOTA_CODES:
                    raise TransportFailure("quota_exhausted", attempt, retryable=False)
                outcome = {408: "timeout", 429: "rate_limited"}.get(
                    status, f"server_error_{status}"
                )
                delay = self._retry_delay(response, attempt)

            if attempt == self._max_attempts:
                raise TransportFailure(outcome, attempt, retryable=True)
            # Wait only if another attempt still fits the deadline afterwards.
            if delay is None or self._clock() + delay + MIN_ATTEMPT_SECONDS > deadline:
                raise TransportFailure(outcome, attempt, retryable=True)
            await self._sleep(delay)

        raise AssertionError("unreachable")  # pragma: no cover

    async def aclose(self) -> None:
        await self._client.aclose()

    def _backoff(self, attempt: int) -> float:
        delay = min(BACKOFF_BASE_SECONDS * 2 ** (attempt - 1), BACKOFF_CAP_SECONDS)
        return delay * (0.5 + 0.5 * self._jitter())

    def _retry_delay(self, response: httpx.Response, attempt: int) -> float | None:
        """Retry-After as a minimum (never shortened); None when it is unusable or too long."""
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
        if not math.isfinite(delay):
            return None
        return max(0.0, delay)


def _json_object(response: httpx.Response, attempt: int) -> dict:
    try:
        body = response.json()
    except ValueError:
        raise TransportFailure("malformed_response", attempt, retryable=False) from None
    if not isinstance(body, dict):
        raise TransportFailure("malformed_response", attempt, retryable=False)
    return body


def _final_failure(response: httpx.Response, attempt: int) -> TransportFailure:
    status = response.status_code
    if status == 401:
        return TransportFailure("unauthenticated", attempt, retryable=False)
    if status == 403:
        return TransportFailure("forbidden", attempt, retryable=False)
    if status == 404:
        return TransportFailure("not_found", attempt, retryable=False)
    if status == 400:
        # A rejected model, schema or parameter: a configuration defect, never the user's fault.
        param = _error_field(response, "param")
        if param == "model" or _error_field(response, "code") in ("model_not_found",):
            return TransportFailure("bad_request_model", attempt, retryable=False)
        return TransportFailure("bad_request", attempt, retryable=False)
    return TransportFailure(f"http_{status}", attempt, retryable=False)


def _error_field(response: httpx.Response, name: str) -> str | None:
    try:
        error = response.json().get("error")
    except (ValueError, AttributeError):
        return None
    value = error.get(name) if isinstance(error, dict) else None
    return value if isinstance(value, str) else None
