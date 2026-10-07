"""Provider-neutral estimation port and its two failure kinds."""

from typing import Protocol

from lifeos_ai.errors import ProviderUnavailable, ServiceError
from lifeos_ai.nutrition.schema import EstimateMealRequest, NutritionEstimate

# The shared failure base and "unavailable" kind (lifeos_ai.errors), under their NUT-002 names.
EstimationError = ServiceError

__all__ = [
    "EstimationError",
    "EstimationFailed",
    "NutritionEstimator",
    "ProviderUnavailable",
    "UnconfiguredEstimator",
]


class EstimationFailed(ServiceError):
    """The provider answered, but without a usable estimate for this meal."""

    code = "estimation_failed"
    status = 502


class NutritionEstimator(Protocol):
    provider: str
    model: str
    prompt_version: str

    @property
    def configured(self) -> bool: ...

    async def estimate(self, request: EstimateMealRequest) -> NutritionEstimate: ...

    async def aclose(self) -> None: ...


class UnconfiguredEstimator:
    """Used when no provider credentials are set: every estimate is unavailable."""

    configured = False

    def __init__(self, *, provider: str, model: str, prompt_version: str):
        self.provider = provider
        self.model = model
        self.prompt_version = prompt_version

    async def estimate(self, request: EstimateMealRequest) -> NutritionEstimate:
        raise ProviderUnavailable("provider credentials are not configured")

    async def aclose(self) -> None:
        return None
