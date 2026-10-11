"""AI-006: the OpenAI API over plain HTTPS (no SDK), shared by the two journal OpenAI adapters.

The journal transport policy (ADR-011): `Authorization: Bearer` with
OPENAI_API_KEY, a finite timeout, a bounded number of attempts, retries only for timeouts, transport
errors, 429 and 5xx, Retry-After honoured up to a cap (above it the request gives up as
unavailable). A 429 that reports an exhausted quota is not retried: it cannot succeed until the
account is changed.

Non-retryable statuses: credential, permission and model problems (401, 403, 404, and a 400 whose
machine-readable error code or parameter names the key or the model) make the provider unusable
until reconfigured (unavailable); any other 400/4xx is a rejected request (invalid). Only the error
`code`/`param`/`type` fields are read from a failure body; provider text is never logged or
returned. Callers parse and validate successful bodies themselves.
"""

import asyncio
import math
from collections.abc import Awaitable, Callable
from datetime import UTC, datetime
from email.utils import parsedate_to_datetime

import httpx

OPENAI_BASE_URL = "https://api.openai.com/v1"
API_KEY_VARIABLE = "OPENAI_API_KEY"

# Transient: rate limiting and server-side failures. Anything else is final.
RETRYABLE_STATUS = frozenset({429, 500, 502, 503, 504})

_CONFIGURATION_CODES = frozenset(
    {"invalid_api_key", "model_not_found", "unsupported_model", "insufficient_quota"}
)


class ProviderFailure(Exception):
    """No usable response. `unavailable`: try again later or reconfigure; otherwise the provider
    rejected the request or answered with something unusable. `outcome` is a short diagnostic code
    (never provider text)."""

    def __init__(self, outcome: str, attempts: int, *, unavailable: bool):
        super().__init__(outcome)
        self.outcome = outcome
        self.attempts = attempts
        self.unavailable = unavailable


class OpenAITransport:
    def __init__(
        self,
        *,
        api_key: str,
        base_url: str,
        timeout_seconds: float,
        max_attempts: int,
        max_retry_wait_seconds: float,
        client: httpx.AsyncClient | None = None,
        sleep: Callable[[float], Awaitable[None]] = asyncio.sleep,
    ):
        if not api_key.strip():
            raise ValueError(f"{API_KEY_VARIABLE} is required")
        if max_attempts < 1:
            raise ValueError("max_attempts must be at least 1")
        self._api_key = api_key
        self._timeout = timeout_seconds
        self._max_attempts = max_attempts
        self._max_retry_wait = max_retry_wait_seconds
        self._client = client or httpx.AsyncClient(base_url=base_url, timeout=timeout_seconds)
        self._sleep = sleep

    async def post(self, path: str, body: dict) -> tuple[httpx.Response, int]:
        """The first 200 response and its attempt number; raises ProviderFailure otherwise."""
        headers = {"Authorization": f"Bearer {self._api_key}"}

        for attempt in range(1, self._max_attempts + 1):
            try:
                response = await self._client.post(
                    path, json=body, headers=headers, timeout=self._timeout
                )
            except httpx.TimeoutException:
                outcome, delay = "timeout", self._backoff(attempt)
            except httpx.TransportError:
                outcome, delay = "transport_error", self._backoff(attempt)
            else:
                if response.status_code == 200:
                    return response, attempt
                if response.status_code not in RETRYABLE_STATUS:
                    raise _final_failure(response, attempt)
                if response.status_code == 429 and _error_code(response) == "insufficient_quota":
                    raise ProviderFailure("http_429_insufficient_quota", attempt, unavailable=True)
                outcome = f"http_{response.status_code}"
                delay = self._retry_delay(response, attempt)

            if attempt == self._max_attempts or delay is None:
                raise ProviderFailure(outcome, attempt, unavailable=True)
            await self._sleep(delay)

        raise AssertionError("unreachable")  # pragma: no cover

    async def aclose(self) -> None:
        await self._client.aclose()

    def _backoff(self, attempt: int) -> float:
        return min(float(2 ** (attempt - 1)), self._max_retry_wait)

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
        if not math.isfinite(delay) or delay > self._max_retry_wait:
            return None
        return max(0.0, delay)


def _final_failure(response: httpx.Response, attempt: int) -> ProviderFailure:
    status = response.status_code
    if status in (401, 403, 404):
        return ProviderFailure(f"http_{status}", attempt, unavailable=True)
    if status == 400 and _is_configuration_error(response):
        return ProviderFailure("http_400_configuration", attempt, unavailable=True)
    return ProviderFailure(f"http_{status}", attempt, unavailable=False)


def _error(response: httpx.Response) -> dict:
    try:
        error = response.json().get("error")
    except (ValueError, AttributeError):
        return {}
    return error if isinstance(error, dict) else {}


def _error_code(response: httpx.Response) -> str | None:
    code = _error(response).get("code")
    return code if isinstance(code, str) else None


def _is_configuration_error(response: httpx.Response) -> bool:
    error = _error(response)
    return (
        error.get("code") in _CONFIGURATION_CODES
        or error.get("param") == "model"
        or error.get("type") == "authentication_error"
    )


def usage_count(usage, key: str, detail: str | None = None) -> int | None:
    """A non-negative token count from an OpenAI `usage` object; None (unknown) when the provider
    did not report a valid one, never a guessed zero. `detail`: the count inside `<key>_details`."""
    if not isinstance(usage, dict):
        return None
    value = usage.get(key)
    if detail is not None:
        value = value.get(detail) if isinstance(value, dict) else None
    return value if type(value) is int and value >= 0 else None
