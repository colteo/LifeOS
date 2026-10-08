"""AI-005: embedders for the retrieval path.

- Live: the PRODUCTION `OpenAIJournalEmbedder` (request body, retries, validation),
  handed an HTTP client whose transport records attempts, latency and token counts
  (AI-003 telemetry). The key comes from OPENAI_API_KEY in the process environment only.
- Offline: `HashingEmbedder`, a deterministic lab-only feature-hashing embedder used by
  the offline sanity runs and tests. It is NOT a candidate: it exercises the real
  chunker, schema and SQL function without a provider, under its own (non-production)
  identity.
"""

import hashlib
import math
import os
import re

import httpx
from lifeos_ai.journal_memory.embedding import Embeddings
from lifeos_ai.journal_memory.openai_embeddings import (
    OPENAI_BASE_URL,
    OpenAIEmbeddingSettings,
    OpenAIJournalEmbedder,
)

from lifeos_ai_evals.production import runtime

DIMENSIONS = 1536

# Lab runtime for live embeddings (not identity): production timeout and attempts, a
# longer Retry-After cap so a rate limit waits instead of failing a case. Recorded in
# each export.
EMBEDDING_RUNTIME = {
    "timeout_seconds": 15.0,
    "max_attempts": 3,
    "max_retry_wait_seconds": 60.0,
}

_TOKEN = re.compile(r"\w+", re.UNICODE)


def openai_key_from_environment() -> str:
    key = os.environ.get("OPENAI_API_KEY", "").strip()
    if not key:
        raise ValueError("OPENAI_API_KEY is required for live retrieval evaluation")
    return key


def live_embedder(
    api_key: str,
    model: str,
    telemetry: runtime.Telemetry,
    transport: httpx.AsyncBaseTransport | None = None,
    sleep=None,
) -> OpenAIJournalEmbedder:
    client = httpx.AsyncClient(
        base_url=OPENAI_BASE_URL,
        timeout=EMBEDDING_RUNTIME["timeout_seconds"],
        transport=runtime.ObservingTransport(telemetry, transport),
    )
    kwargs = {"client": client}
    if sleep is not None:
        kwargs["sleep"] = sleep
    return OpenAIJournalEmbedder(
        OpenAIEmbeddingSettings(
            api_key=api_key,
            model=model,
            dimensions=DIMENSIONS,
            base_url=OPENAI_BASE_URL,
            timeout_seconds=EMBEDDING_RUNTIME["timeout_seconds"],
            max_attempts=EMBEDDING_RUNTIME["max_attempts"],
            max_retry_wait_seconds=EMBEDDING_RUNTIME["max_retry_wait_seconds"],
        ),
        **kwargs,
    )


class HashingEmbedder:
    """Signed feature hashing of lower-cased word tokens and their character 4-grams,
    L2-normalised. Deterministic, offline, lab-only."""

    provider = "lab"
    model = "ai005-hashing-embedding-v1"
    dimensions = DIMENSIONS
    configured = True

    def __init__(self):
        self.requests = 0

    async def embed(self, texts: list[str], *, purpose: str) -> Embeddings:
        self.requests += 1
        return Embeddings([self.vector(text) for text in texts], attempts=1)

    async def aclose(self) -> None:
        return None

    @classmethod
    def vector(cls, text: str) -> list[float]:
        values = [0.0] * DIMENSIONS
        for token in _TOKEN.findall(text.lower()):
            features = [("w", token, 1.0)]
            padded = f"#{token}#"
            features.extend(
                ("g", padded[index : index + 4], 0.35)
                for index in range(max(1, len(padded) - 3))
            )
            for kind, feature, weight in features:
                digest = hashlib.sha256(f"{kind}:{feature}".encode()).digest()
                index = int.from_bytes(digest[:4], "big") % DIMENSIONS
                sign = 1.0 if digest[4] & 1 else -1.0
                values[index] += sign * weight
        norm = math.sqrt(sum(value * value for value in values))
        if norm == 0.0:
            # Cosine distance is undefined for a zero vector; a fixed unit vector
            # instead.
            values[0] = 1.0
            return values
        return [value / norm for value in values]
