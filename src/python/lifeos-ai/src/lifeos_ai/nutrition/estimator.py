"""Provider-neutral estimation port and its two failure kinds."""

from typing import Protocol

from lifeos_ai.nutrition.schema import EstimateMealRequest, NutritionEstimate


class EstimationError(Exception):
    """A failure the API reports as a stable code, never as provider details."""

    code = "estimation_error"


class ProviderUnavailable(EstimationError):
    """Not configured, unreachable, timed out, rate-limited or rejected credentials."""

    code = "provider_unavailable"


class EstimationFailed(EstimationError):
    """The provider answered, but without a usable estimate for this meal."""

    code = "estimation_failed"


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
