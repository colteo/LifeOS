"""One explicit live smoke test against the configured provider. Never run by pytest.

Usage (PowerShell, from src/python/lifeos-ai, with GROQ_API_KEY set):
    uv run python -m lifeos_ai.smoke

Estimates two synthetic meals, prints each estimate or failure code, and exits non-zero
if any failed.
"""

import asyncio
import sys

from lifeos_ai.config import nutrition_estimator_from_environment
from lifeos_ai.nutrition.estimator import EstimationError
from lifeos_ai.nutrition.schema import EstimateMealRequest

MEALS = (
    EstimateMealRequest(description="Pollo con le patate", meal_type="Lunch"),
    EstimateMealRequest(description="Yogurt greco, una banana e un caffè"),
)


async def main() -> int:
    estimator = nutrition_estimator_from_environment()
    print(
        f"provider={estimator.provider} model={estimator.model} prompt={estimator.prompt_version}"
    )
    if not estimator.configured:
        print("GROQ_API_KEY is not set.")
        return 2
    failures = 0
    try:
        for meal in MEALS:
            try:
                estimate = await estimator.estimate(meal)
            except EstimationError as failure:
                failures += 1
                print(f"- {meal.description!r}: FAILED {failure.code}")
            else:
                print(f"- {meal.description!r}: {estimate.model_dump_json()}")
    finally:
        await estimator.aclose()
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
