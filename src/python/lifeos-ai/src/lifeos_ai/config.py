"""Environment-only configuration. Secrets are never read from files or logged."""

import os
from collections.abc import Mapping

from lifeos_ai.nutrition.estimator import NutritionEstimator, UnconfiguredEstimator
from lifeos_ai.nutrition.groq import DEFAULT_MODEL, GroqNutritionEstimator, GroqSettings
from lifeos_ai.nutrition.prompt import PROMPT_VERSION

API_KEY_VARIABLE = "GROQ_API_KEY"
MODEL_VARIABLE = "LIFEOS_AI_NUTRITION_MODEL"

# PROD-AI-001: the secret shared with the LifeOS API (NutritionAi__ServiceKey on the .NET side).
SERVICE_KEY_VARIABLE = "LIFEOS_AI_SERVICE_KEY"
MIN_SERVICE_KEY_LENGTH = 32


class ServiceKeyError(RuntimeError):
    """The service key is missing or too weak. The message names the variable, never a value."""


def nutrition_estimator_from_environment(
    environ: Mapping[str, str] = os.environ,
) -> NutritionEstimator:
    model = environ.get(MODEL_VARIABLE, "").strip() or DEFAULT_MODEL
    api_key = environ.get(API_KEY_VARIABLE, "").strip()
    if not api_key:
        return UnconfiguredEstimator(provider="groq", model=model, prompt_version=PROMPT_VERSION)
    return GroqNutritionEstimator(GroqSettings(api_key=api_key, model=model))


def validate_service_key(key: str | None) -> str:
    """Returns the key; raises ServiceKeyError so the service cannot start unauthenticated."""
    key = (key or "").strip()
    if not key:
        raise ServiceKeyError(f"{SERVICE_KEY_VARIABLE} is required.")
    if len(key) < MIN_SERVICE_KEY_LENGTH or not all("!" <= char <= "~" for char in key):
        raise ServiceKeyError(
            f"{SERVICE_KEY_VARIABLE} must be at least {MIN_SERVICE_KEY_LENGTH} visible ASCII "
            "characters without spaces."
        )
    return key


def service_key_from_environment(environ: Mapping[str, str] = os.environ) -> str:
    return validate_service_key(environ.get(SERVICE_KEY_VARIABLE))
