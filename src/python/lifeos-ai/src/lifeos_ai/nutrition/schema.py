"""The service's request/response contract and the provider output it accepts.

Bounds match the .NET Domain (MealEntry description, MealNutritionSnapshot values), so
anything this service returns can be persisted unchanged by LifeOS.
"""

from typing import Annotated, Literal

from pydantic import BaseModel, ConfigDict, Field, StringConstraints

MAX_DESCRIPTION_LENGTH = 2000
MAX_CALORIES_KCAL = 10000
MAX_MACRO_GRAMS = 1000
MAX_ASSUMPTIONS = 10
MAX_ASSUMPTION_LENGTH = 300

MealType = Literal["Breakfast", "Lunch", "Dinner", "Snack", "Other"]

Assumption = Annotated[
    str,
    StringConstraints(strip_whitespace=True, min_length=1, max_length=MAX_ASSUMPTION_LENGTH),
]


def _amount(maximum: float):
    # Strict: a JSON number only (no strings, no booleans); finite and non-negative.
    return Field(strict=True, ge=0, le=maximum, allow_inf_nan=False)


class EstimateMealRequest(BaseModel):
    """Everything the estimator may know about a meal: its text and optional type.

    Unknown fields are rejected, so identifiers, dates or other LifeOS data cannot be
    sent here by mistake.
    """

    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)

    description: str = Field(min_length=1, max_length=MAX_DESCRIPTION_LENGTH)
    meal_type: MealType | None = None


class NutritionEstimate(BaseModel):
    """One meal's estimate. Daily totals are never estimated: LifeOS sums meals itself."""

    model_config = ConfigDict(extra="forbid")

    calories_kcal: float = _amount(MAX_CALORIES_KCAL)
    protein_grams: float = _amount(MAX_MACRO_GRAMS)
    carbs_grams: float = _amount(MAX_MACRO_GRAMS)
    fat_grams: float = _amount(MAX_MACRO_GRAMS)
    assumptions: list[Assumption] = Field(max_length=MAX_ASSUMPTIONS)


class ProviderNutritionOutput(NutritionEstimate):
    """The model's structured output. "not_estimable" is an explicit failure, never zeros."""

    status: Literal["estimated", "not_estimable"]


# Strict structured-output schema sent to the provider. Range checks stay in Pydantic
# (ProviderNutritionOutput), which validates every response regardless of the provider.
OUTPUT_SCHEMA = {
    "type": "object",
    "additionalProperties": False,
    "required": [
        "status",
        "calories_kcal",
        "protein_grams",
        "carbs_grams",
        "fat_grams",
        "assumptions",
    ],
    "properties": {
        "status": {"type": "string", "enum": ["estimated", "not_estimable"]},
        "calories_kcal": {"type": "number"},
        "protein_grams": {"type": "number"},
        "carbs_grams": {"type": "number"},
        "fat_grams": {"type": "number"},
        "assumptions": {"type": "array", "items": {"type": "string"}},
    },
}
