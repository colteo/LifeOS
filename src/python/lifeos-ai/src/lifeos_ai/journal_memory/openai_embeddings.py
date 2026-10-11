"""AI-006: OpenAI embeddings over plain HTTPS (no SDK), the production journal embedding adapter
since AI-006.1.

The journal embedding default (LIFEOS_AI_JOURNAL_EMBEDDING_PROVIDER unset or `openai`, config.py);
the other provider stays selectable explicitly there. Transport policy: openai_http.py.

One logical request = one `POST /v1/embeddings` call whose `input` is the list of texts (one
independent input per chunk), with `dimensions` and `encoding_format=float` set explicitly. The
response is validated strictly: exactly one embedding per input, identified by its `index` (never by
array position): every index present exactly once, then restored to input order; exactly
`dimensions` finite numbers each. Nothing partial is ever returned.

The texts are the port's input unchanged: no query/document prefixes or task formatting (the model
has none). `purpose` is a log label only.

Logs: one line per logical request with the result, outcome code, identity, purpose, item count,
attempts, latency and the provider's token counts. Never the input text, a vector value or the key.
"""

import asyncio
import logging
import math
import time
from collections.abc import Awaitable, Callable
from dataclasses import dataclass

import httpx

from lifeos_ai.journal_memory.embedding import EmbeddingFailed, Embeddings, ProviderUnavailable
from lifeos_ai.journal_memory.openai_http import (
    OPENAI_BASE_URL,
    OpenAITransport,
    ProviderFailure,
    usage_count,
)

DEFAULT_EMBEDDING_MODEL = "text-embedding-3-small"
EMBEDDING_DIMENSIONS = 1536
PURPOSES = frozenset({"index", "query"})

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


@dataclass(frozen=True)
class EmbeddingUsage:
    """Token counts of one answered request; None when the provider did not report one."""

    prompt_tokens: int | None
    total_tokens: int | None

    @classmethod
    def from_response(cls, response: httpx.Response) -> "EmbeddingUsage":
        try:
            usage = response.json().get("usage")
        except (ValueError, AttributeError):
            usage = None
        return cls(usage_count(usage, "prompt_tokens"), usage_count(usage, "total_tokens"))


_NO_USAGE = EmbeddingUsage(None, None)


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
        self.model = settings.model
        self.dimensions = settings.dimensions
        self._transport = OpenAITransport(
            api_key=settings.api_key,
            base_url=settings.base_url,
            timeout_seconds=settings.timeout_seconds,
            max_attempts=settings.max_attempts,
            max_retry_wait_seconds=settings.max_retry_wait_seconds,
            client=client,
            sleep=sleep,
        )

    def request_body(self, texts: list[str]) -> dict:
        return {
            "model": self.model,
            "input": list(texts),
            "dimensions": self.dimensions,
            "encoding_format": "float",
        }

    async def embed(self, texts: list[str], *, purpose: str) -> Embeddings:
        if not texts:
            raise ValueError("at least one text is required")
        if purpose not in PURPOSES:
            raise ValueError("purpose must be index or query")
        started = time.monotonic()
        usage = _NO_USAGE
        try:
            response, attempts = await self._transport.post("embeddings", self.request_body(texts))
            usage = EmbeddingUsage.from_response(response)
            try:
                vectors = self._vectors(response, len(texts))
            except ValueError:
                raise ProviderFailure("invalid_output", attempts, unavailable=False) from None
        except ProviderFailure as failure:
            result = "unavailable" if failure.unavailable else "invalid"
            self._log(
                result, failure.outcome, purpose, len(texts), failure.attempts, started, usage
            )
            if failure.unavailable:
                raise ProviderUnavailable(f"provider unavailable ({failure.outcome})") from None
            raise EmbeddingFailed("provider output is not valid embeddings") from None

        self._log("success", "embedded", purpose, len(texts), attempts, started, usage)
        return Embeddings(vectors, attempts, usage.prompt_tokens)

    async def aclose(self) -> None:
        await self._transport.aclose()

    def _vectors(self, response: httpx.Response, count: int) -> list[list[float]]:
        """Raises ValueError unless the body holds exactly one valid vector per input index."""
        try:
            data = response.json()["data"]
        except (ValueError, KeyError, TypeError) as error:
            raise ValueError("malformed body") from error
        if not isinstance(data, list) or len(data) != count:
            raise ValueError("wrong number of embeddings")

        by_index: dict[int, list[float]] = {}
        for item in data:
            if not isinstance(item, dict):
                raise ValueError("malformed embedding item")
            index = item.get("index")
            if type(index) is not int or not 0 <= index < count:
                raise ValueError("missing or out-of-range index")
            if index in by_index:
                raise ValueError("duplicate index")
            by_index[index] = self._vector(item.get("embedding"))
        # count distinct in-range indexes for count items: every index 0..count-1 is present.
        return [by_index[index] for index in range(count)]

    def _vector(self, values) -> list[float]:
        if not isinstance(values, list) or len(values) != self.dimensions:
            raise ValueError("wrong dimensions")
        vector = []
        for value in values:
            if type(value) not in (int, float) or not math.isfinite(value):
                raise ValueError("non-finite or non-numeric value")
            vector.append(float(value))
        return vector

    def _log(
        self,
        result: str,
        outcome: str,
        purpose: str,
        items: int,
        attempts: int,
        started: float,
        usage: EmbeddingUsage,
    ) -> None:
        logger.info(
            "journal embedding result=%s outcome=%s purpose=%s provider=%s model=%s dimensions=%d "
            "items=%d attempts=%d seconds=%.3f input_tokens=%s total_tokens=%s",
            result,
            outcome,
            purpose,
            self.provider,
            self.model,
            self.dimensions,
            items,
            attempts,
            time.monotonic() - started,
            "-" if usage.prompt_tokens is None else usage.prompt_tokens,
            "-" if usage.total_tokens is None else usage.total_tokens,
        )
