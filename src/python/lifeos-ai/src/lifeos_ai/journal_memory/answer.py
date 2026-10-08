"""AI-004: the provider-neutral grounded-answer port of the Journal Memory Layer."""

from typing import Protocol

from lifeos_ai.errors import ProviderUnavailable, ServiceError
from lifeos_ai.journal_memory.schema import AnswerRequest, JournalAnswer

__all__ = ["AnswerFailed", "JournalAnswerer", "ProviderUnavailable", "UnconfiguredAnswerer"]


class AnswerFailed(ServiceError):
    """The provider answered, but not with a valid grounded answer (schema or citations)."""

    code = "answer_failed"
    status = 502


class JournalAnswerer(Protocol):
    provider: str
    model: str
    prompt_version: str

    @property
    def configured(self) -> bool: ...

    async def answer(self, request: AnswerRequest) -> JournalAnswer: ...

    async def aclose(self) -> None: ...


class UnconfiguredAnswerer:
    """Used when no provider credentials are set: every answer is unavailable."""

    configured = False

    def __init__(self, *, provider: str, model: str, prompt_version: str):
        self.provider = provider
        self.model = model
        self.prompt_version = prompt_version

    async def answer(self, request: AnswerRequest) -> JournalAnswer:
        raise ProviderUnavailable("provider credentials are not configured")

    async def aclose(self) -> None:
        return None
