"""Offline fakes for the AI-005 Journal RAG evaluators (no network, no key)."""

import json
import shutil

import httpx
import pytest
from lifeos_ai.journal_memory.embedding import Embeddings

from lifeos_ai_evals.journal_memory.database import Docker
from lifeos_ai_evals.journal_memory.embedders import DIMENSIONS, HashingEmbedder


class EmbeddingTransport(httpx.AsyncBaseTransport):
    """Answers OpenAI /embeddings requests with deterministic hashing vectors. Records
    bodies; `failures` are HTTP statuses returned first (e.g. a 429 to exercise
    retries)."""

    def __init__(self, *failures: int, tokens_per_input: int = 7):
        self.failures = list(failures)
        self.bodies: list[dict] = []
        self.tokens_per_input = tokens_per_input

    async def handle_async_request(self, request: httpx.Request) -> httpx.Response:
        body = json.loads(request.content)
        self.bodies.append(body)
        if self.failures:
            return httpx.Response(self.failures.pop(0), json={"error": "scripted"})
        tokens = self.tokens_per_input * len(body["input"])
        return httpx.Response(
            200,
            json={
                "data": [
                    {"index": i, "embedding": HashingEmbedder.vector(text)}
                    for i, text in enumerate(body["input"])
                ],
                "usage": {"prompt_tokens": tokens, "total_tokens": tokens},
            },
        )

    async def aclose(self) -> None:
        return None


def unit(index: int, other: int | None = None, weight: float = 0.0) -> list[float]:
    vector = [0.0] * DIMENSIONS
    vector[index] = 1.0
    if other is not None:
        vector[other] = weight
    return vector


class FixedEmbedder:
    """Maps known texts to fixed vectors; anything else to a far-away unit vector."""

    provider = "lab"
    model = "ai005-fixed-test-vectors"
    dimensions = DIMENSIONS
    configured = True

    def __init__(self, vectors: dict[str, list[float]]):
        self._vectors = vectors

    async def embed(self, texts, *, purpose):
        return Embeddings(
            [self._vectors.get(t, unit(DIMENSIONS - 1)) for t in texts], 1
        )

    async def aclose(self):
        return None


def requires_docker():
    if shutil.which("dotnet") is None or not Docker().available():
        pytest.skip(
            "Docker and dotnet are required for the disposable pgvector database"
        )
