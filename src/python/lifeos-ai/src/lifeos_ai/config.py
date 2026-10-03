"""Environment-only configuration. Secrets are never read from files or logged."""

import os
from collections.abc import Mapping

from lifeos_ai.nutrition.estimator import NutritionEstimator, UnconfiguredEstimator
from lifeos_ai.nutrition.groq import DEFAULT_MODEL, GroqNutritionEstimator, GroqSettings
from lifeos_ai.nutrition.prompt import PROMPT_VERSION

API_KEY_VARIABLE = "GROQ_API_KEY"
MODEL_VARIABLE = "LIFEOS_AI_NUTRITION_MODEL"


def nutrition_estimator_from_environment(
    environ: Mapping[str, str] = os.environ,
) -> NutritionEstimator:
    model = environ.get(MODEL_VARIABLE, "").strip() or DEFAULT_MODEL
    api_key = environ.get(API_KEY_VARIABLE, "").strip()
    if not api_key:
        return UnconfiguredEstimator(provider="groq", model=model, prompt_version=PROMPT_VERSION)
    return GroqNutritionEstimator(GroqSettings(api_key=api_key, model=model))
