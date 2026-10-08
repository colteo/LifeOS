"""AI-004: OpenAI Embeddings API over plain HTTPS (no SDK), the V1 journal embedding adapter.

Same transport policy as the Groq capabilities (groq_chat.py, ADR-011): finite timeout, a bounded
number of attempts, retries only for timeouts, transport errors, 429 and 5xx, Retry-After honoured
up to a cap (above it the request gives up as unavailable). Every response is validated strictly:
one vector per input, each index exactly once, exactly `dimensions` finite numbers. Nothing partial
is ever returned.

Logs: one line per logical request with the result, outcome code, identity, purpose, item count,
attempts, latency and the provider's token count. Never the input text or a vector value.
"""

import asyncio
import logging
import math
import time
from collections.abc import Awaitable, Callable
from dataclasses import dataclass
from datetime import UTC, datetime
from email.utils import parsedate_to_datetime

import httpx

from lifeos_ai.journal_memory.embedding import EmbeddingFailed, Embeddings, ProviderUnavailable

OPENAI_BASE_URL = "https://api.openai.com/v1"
DEFAULT_EMBEDDING_MODEL = "text-embedding-3-small"
EMBEDDING_DIMENSIONS = 1536

# Transient: rate limiting and server-side failures. Anything else is final.
RETRYABLE_STATUS = frozenset({429, 500, 502, 503, 504})

logger = logging.getLogger("lifeos_ai.journal_memory")


@dataclass(frozen=True)
class OpenAIEmbeddingSettings:
    api_key: str
    model: str = DEFAULT_EMBEDDING_MODEL
    dimensions: int = EMBEDDING_DIMENSIONS
    base_url: str = OPENAI_BASE_URL
    timeout_seconds: float = 15.0
    max_attempts: int = 3
    max_retry_wait_seconds: float = 4.0


class _Failure(Exception):
    def __init__(
        self, outcome: str, attempts: int, *, unavailable: bool, tokens: int | None = None
    ):
        super().__init__(outcome)
        self.outcome = outcome
        self.attempts = attempts
        self.unavailable = unavailable
        self.tokens = tokens


class OpenAIJournalEmbedder:
    provider = "openai"
    configured = True

    def __init__(
        self,
        settings: OpenAIEmbeddingSettings,
        *,
        client: httpx.AsyncClient | None = None,
        sleep: Callable[[float], Awaitable[None]] = asyncio.sleep,
    ):
        if not settings.api_key.strip():
            raise ValueError("OPENAI_API_KEY is required")
        if settings.max_attempts < 1:
            raise ValueError("max_attempts must be at least 1")
        self.model = settings.model
        self.dimensions = settings.dimensions
        self._settings = settings
        self._client = client or httpx.AsyncClient(
            base_url=settings.base_url, timeout=settings.timeout_seconds
        )
        self._sleep = sleep

    def request_body(self, texts: list[str]) -> dict:
        return {
            "model": self.model,
            "input": texts,
            "dimensions": self.dimensions,
            "encoding_format": "float",
        }

    async def embed(self, texts: list[str], *, purpose: str) -> Embeddings:
        if not texts:
            raise ValueError("at least one text is required")
        started = time.monotonic()
        try:
            response, attempts = await self._send(self.request_body(texts))
            tokens = _input_tokens(response)
            try:
                vectors = self._vectors(response, len(texts))
            except ValueError:
                raise _Failure(
                    "invalid_output", attempts, unavailable=False, tokens=tokens
                ) from None
        except _Failure as failure:
            result = "unavailable" if failure.unavailable else "invalid"
            self._log(
                result,
                failure.outcome,
                purpose,
                len(texts),
                failure.attempts,
                started,
                failure.tokens,
            )
            if failure.unavailable:
                raise ProviderUnavailable(f"provider unavailable ({failure.outcome})") from None
            raise EmbeddingFailed("provider output is not valid embeddings") from None

        self._log("success", "embedded", purpose, len(texts), attempts, started, tokens)
        return Embeddings(vectors, attempts, tokens)

    async def aclose(self) -> None:
        await self._client.aclose()

    async def _send(self, body: dict) -> tuple[httpx.Response, int]:
        headers = {"Authorization": f"Bearer {self._settings.api_key}"}
        attempts = self._settings.max_attempts

        for attempt in range(1, attempts + 1):
            try:
                response = await self._client.post(
                    "embeddings", json=body, headers=headers, timeout=self._settings.timeout_seconds
                )
            except httpx.TimeoutException:
                outcome, delay = "timeout", self._backoff(attempt)
            except httpx.TransportError:
                outcome, delay = "transport_error", self._backoff(attempt)
            else:
                if response.status_code == 200:
                    return response, attempt
                if response.status_code not in RETRYABLE_STATUS:
                    # 400: the request was rejected (e.g. too long). Credential, permission or model
                    # problems (401/403/404) make the provider unusable until reconfigured.
                    raise _Failure(
                        f"http_{response.status_code}",
                        attempt,
                        unavailable=response.status_code != 400,
                    )
                outcome = f"http_{response.status_code}"
                delay = self._retry_delay(response, attempt)

            if attempt == attempts or delay is None:
                raise _Failure(outcome, attempt, unavailable=True)
            await self._sleep(delay)

        raise AssertionError("unreachable")  # pragma: no cover

    def _vectors(self, response: httpx.Response, count: int) -> list[list[float]]:
        """Raises ValueError unless the body holds exactly one valid vector per input."""
        try:
            data = response.json()["data"]
        except (ValueError, KeyError, TypeError) as error:
            raise ValueError("malformed body") from error
        if not isinstance(data, list) or len(data) != count:
            raise ValueError("wrong number of embeddings")

        vectors: list[list[float] | None] = [None] * count
        for item in data:
            if not isinstance(item, dict):
                raise ValueError("malformed item")
            index, values = item.get("index"), item.get("embedding")
            if type(index) is not int or not 0 <= index < count or vectors[index] is not None:
                raise ValueError("bad index")
            if not isinstance(values, list) or len(values) != self.dimensions:
                raise ValueError("wrong dimensions")
            vector = []
            for value in values:
                if type(value) not in (int, float) or not math.isfinite(value):
                    raise ValueError("non-finite or non-numeric value")
                vector.append(float(value))
            vectors[index] = vector
        return vectors  # type: ignore[return-value]  # every index was filled exactly once

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

    def _log(
        self,
        result: str,
        outcome: str,
        purpose: str,
        items: int,
        attempts: int,
        started: float,
        tokens: int | None,
    ) -> None:
        logger.info(
            "journal embedding result=%s outcome=%s purpose=%s provider=%s model=%s dimensions=%d "
            "items=%d attempts=%d seconds=%.3f input_tokens=%s",
            result,
            outcome,
            purpose,
            self.provider,
            self.model,
            self.dimensions,
            items,
            attempts,
            time.monotonic() - started,
            "-" if tokens is None else tokens,
        )


def _input_tokens(response: httpx.Response) -> int | None:
    try:
        usage = response.json().get("usage")
    except (ValueError, AttributeError):
        return None
    if not isinstance(usage, dict):
        return None
    value = usage.get("prompt_tokens")
    return value if type(value) is int and value >= 0 else None
