"""AI-004: the provider-neutral embedding port of the Journal Memory Layer and its failure kinds."""

from dataclasses import dataclass
from typing import Protocol

from lifeos_ai.errors import ProviderUnavailable, ServiceError

__all__ = [
    "EmbeddingFailed",
    "Embeddings",
    "JournalEmbedder",
    "ProviderUnavailable",
    "UnconfiguredEmbedder",
]


class EmbeddingFailed(ServiceError):
    """The provider answered, but not with valid embeddings (rejected, malformed, wrong size)."""

    code = "embedding_failed"
    status = 502


@dataclass(frozen=True)
class Embeddings:
    """One vector per input, in input order. `input_tokens`: None when the provider did not say."""

    vectors: list[list[float]]
    attempts: int
    input_tokens: int | None = None


class JournalEmbedder(Protocol):
    provider: str
    model: str
    dimensions: int

    @property
    def configured(self) -> bool: ...

    async def embed(self, texts: list[str], *, purpose: str) -> Embeddings:
        """`purpose` (index | query): which side of retrieval the texts are. A log label, and an
        adapter may use it for its provider's documented query/document input formatting; it never
        changes the texts' meaning, count or order (one vector per text, in input order)."""
        ...

    async def aclose(self) -> None: ...


class UnconfiguredEmbedder:
    """Used when no provider credentials are set: every embedding request is unavailable."""

    configured = False

    def __init__(self, *, provider: str, model: str, dimensions: int):
        self.provider = provider
        self.model = model
        self.dimensions = dimensions

    async def embed(self, texts: list[str], *, purpose: str) -> Embeddings:
        raise ProviderUnavailable("provider credentials are not configured")

    async def aclose(self) -> None:
        return None
