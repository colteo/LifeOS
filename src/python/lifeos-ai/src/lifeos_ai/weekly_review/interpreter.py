"""Provider-neutral weekly-review interpretation port (AI-001) and its failure kinds."""

from typing import Protocol

from lifeos_ai.errors import ProviderUnavailable, ServiceError
from lifeos_ai.weekly_review.schema import InterpretWeeklyReviewRequest, WeeklyReviewInsights

__all__ = [
    "InterpretationFailed",
    "ProviderUnavailable",
    "UnconfiguredInterpreter",
    "WeeklyReviewInterpreter",
]


class InterpretationFailed(ServiceError):
    """The provider answered, but not with valid insights (rejected, malformed or out of bounds)."""

    code = "interpretation_failed"
    status = 502


class WeeklyReviewInterpreter(Protocol):
    provider: str
    model: str
    prompt_version: str

    @property
    def configured(self) -> bool: ...

    async def interpret(self, request: InterpretWeeklyReviewRequest) -> WeeklyReviewInsights: ...

    async def aclose(self) -> None: ...


class UnconfiguredInterpreter:
    """Used when no provider credentials are set: every interpretation is unavailable."""

    configured = False

    def __init__(self, *, provider: str, model: str, prompt_version: str):
        self.provider = provider
        self.model = model
        self.prompt_version = prompt_version

    async def interpret(self, request: InterpretWeeklyReviewRequest) -> WeeklyReviewInsights:
        raise ProviderUnavailable("provider credentials are not configured")

    async def aclose(self) -> None:
        return None
