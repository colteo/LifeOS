"""AI-004: Gemini Developer API embeddings over plain HTTPS (no SDK), the journal embedding adapter.

Same transport policy as the Groq capabilities (groq_chat.py, ADR-011): finite timeout, a bounded
number of attempts, retries only for timeouts, transport errors, 429 and 5xx, Retry-After honoured
up to a cap (above it the request gives up as unavailable). Every response is validated strictly:
exactly one vector per input, in input order, exactly `dimensions` finite numbers. Nothing partial
is ever returned.

One logical request = one `batchEmbedContents` call with one independent request (one `Content`
with one text part) per input. gemini-embedding-2 aggregates the parts of a single `Content` into
ONE embedding, so inputs are never sent as parts of one content: N chunks always give N embeddings.

gemini-embedding-2 has no task-type field; retrieval asymmetry is expressed in the text, as the
model's documentation recommends. That formatting is provider-specific, so it lives here only: the
production chunk text, embedding input and citations are unchanged (QUERY_FORMAT, DOCUMENT_FORMAT).

Logs: one line per logical request with the result, outcome code, identity, purpose, item count,
attempts, latency and the provider's token count. Never the input text, a vector value or the key.
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

GEMINI_BASE_URL = "https://generativelanguage.googleapis.com/v1beta"
DEFAULT_EMBEDDING_MODEL = "gemini-embedding-2"
EMBEDDING_DIMENSIONS = 1536

# Asymmetric retrieval formatting of gemini-embedding-2 ("search result" task). An indexed text is
# the chunker's embedding input, which already carries the entry title ("<title>\n\n<chunk>"); the
# title is not repeated or invented as separate metadata, hence the documented "title: none" form.
QUERY_FORMAT = "task: search result | query: {text}"
DOCUMENT_FORMAT = "title: none | text: {text}"
_FORMATS = {"query": QUERY_FORMAT, "index": DOCUMENT_FORMAT}

# Transient: rate limiting and server-side failures. Anything else is final.
RETRYABLE_STATUS = frozenset({429, 500, 502, 503, 504})

logger = logging.getLogger("lifeos_ai.journal_memory")


@dataclass(frozen=True)
class GeminiEmbeddingSettings:
    api_key: str
    model: str = DEFAULT_EMBEDDING_MODEL
    dimensions: int = EMBEDDING_DIMENSIONS
    base_url: str = GEMINI_BASE_URL
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


class GeminiJournalEmbedder:
    provider = "google"
    configured = True

    def __init__(
        self,
        settings: GeminiEmbeddingSettings,
        *,
        client: httpx.AsyncClient | None = None,
        sleep: Callable[[float], Awaitable[None]] = asyncio.sleep,
    ):
        if not settings.api_key.strip():
            raise ValueError("GEMINI_API_KEY is required")
        if settings.max_attempts < 1:
            raise ValueError("max_attempts must be at least 1")
        self.model = settings.model
        self.dimensions = settings.dimensions
        self._settings = settings
        self._client = client or httpx.AsyncClient(
            base_url=settings.base_url, timeout=settings.timeout_seconds
        )
        self._sleep = sleep

    def request_body(self, texts: list[str], *, purpose: str) -> dict:
        template = _FORMATS[purpose]
        return {
            "requests": [
                {
                    "model": f"models/{self.model}",
                    "content": {"parts": [{"text": template.format(text=text)}]},
                    "outputDimensionality": self.dimensions,
                }
                for text in texts
            ]
        }

    async def embed(self, texts: list[str], *, purpose: str) -> Embeddings:
        if not texts:
            raise ValueError("at least one text is required")
        if purpose not in _FORMATS:
            raise ValueError("purpose must be index or query")
        started = time.monotonic()
        try:
            response, attempts = await self._send(self.request_body(texts, purpose=purpose))
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
        headers = {"x-goog-api-key": self._settings.api_key}
        path = f"models/{self.model}:batchEmbedContents"
        attempts = self._settings.max_attempts

        for attempt in range(1, attempts + 1):
            try:
                response = await self._client.post(
                    path, json=body, headers=headers, timeout=self._settings.timeout_seconds
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
                outcome = f"http_{response.status_code}"
                delay = self._retry_delay(response, attempt)

            if attempt == attempts or delay is None:
                raise _Failure(outcome, attempt, unavailable=True)
            await self._sleep(delay)

        raise AssertionError("unreachable")  # pragma: no cover

    def _vectors(self, response: httpx.Response, count: int) -> list[list[float]]:
        """Raises ValueError unless the body holds exactly one valid vector per input, in order."""
        try:
            data = response.json()["embeddings"]
        except (ValueError, KeyError, TypeError) as error:
            raise ValueError("malformed body") from error
        if not isinstance(data, list) or len(data) != count:
            raise ValueError("wrong number of embeddings")

        vectors = []
        for item in data:
            values = item.get("values") if isinstance(item, dict) else None
            if not isinstance(values, list) or len(values) != self.dimensions:
                raise ValueError("wrong dimensions")
            vector = []
            for value in values:
                if type(value) not in (int, float) or not math.isfinite(value):
                    raise ValueError("non-finite or non-numeric value")
                vector.append(float(value))
            vectors.append(vector)
        return vectors

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


def _final_failure(response: httpx.Response, attempt: int) -> _Failure:
    """A non-retryable HTTP status. Credential, permission, model or project problems make the
    provider unusable until reconfigured (unavailable); any other rejection is an invalid request.

    The Gemini API reports an invalid or expired key, and a project it cannot serve (e.g. billing or
    region), as 400 with a machine-readable reason/status: those are configuration, not the request.
    Only those codes are read from the body; no provider text is ever logged or returned.
    """
    status = response.status_code
    if status in (401, 403, 404):
        return _Failure(f"http_{status}", attempt, unavailable=True)
    if status == 400 and _is_configuration_error(response):
        return _Failure("http_400_configuration", attempt, unavailable=True)
    return _Failure(f"http_{status}", attempt, unavailable=False)


def _is_configuration_error(response: httpx.Response) -> bool:
    try:
        error = response.json()["error"]
        details = error.get("details") or []
        reasons = {detail.get("reason") for detail in details if isinstance(detail, dict)}
        status = error.get("status")
    except (ValueError, KeyError, TypeError, AttributeError):
        return False
    return status == "FAILED_PRECONDITION" or any(
        isinstance(reason, str) and reason.startswith("API_KEY_") for reason in reasons
    )


def _input_tokens(response: httpx.Response) -> int | None:
    try:
        usage = response.json().get("usageMetadata")
    except (ValueError, AttributeError):
        return None
    if not isinstance(usage, dict):
        return None
    value = usage.get("promptTokenCount")
    return value if type(value) is int and value >= 0 else None
