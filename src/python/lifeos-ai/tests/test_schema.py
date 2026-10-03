import json

import pytest
from pydantic import ValidationError

from lifeos_ai.nutrition.schema import (
    MAX_DESCRIPTION_LENGTH,
    EstimateMealRequest,
    NutritionEstimate,
    ProviderNutritionOutput,
)
from tests.fakes import VALID_OUTPUT

# ---- Request ----


def test_request_trims_and_accepts_optional_meal_type():
    request = EstimateMealRequest.model_validate({"description": "  Pollo con le patate \n"})

    assert request.description == "Pollo con le patate"
    assert request.meal_type is None
    assert EstimateMealRequest(description="x", meal_type="Lunch").meal_type == "Lunch"


@pytest.mark.parametrize(
    "payload",
    [
        {},
        {"description": ""},
        {"description": "   "},
        {"description": "x" * (MAX_DESCRIPTION_LENGTH + 1)},
        {"description": 42},
        {"description": "Pasta", "meal_type": "Brunch"},
        {"description": "Pasta", "meal_type": "lunch"},
    ],
)
def test_request_rejects_invalid_input(payload):
    with pytest.raises(ValidationError):
        EstimateMealRequest.model_validate(payload)


@pytest.mark.parametrize("field", ["user_id", "meal_id", "date", "occurred_at_utc", "token"])
def test_request_rejects_any_other_data(field):
    # The privacy boundary: only the description and meal type may be sent.
    with pytest.raises(ValidationError):
        EstimateMealRequest.model_validate({"description": "Pasta", field: "x"})


def test_request_keeps_the_maximum_length():
    assert len(EstimateMealRequest(description="x" * MAX_DESCRIPTION_LENGTH).description) == 2000


# ---- Provider output ----


def parse(**changes):
    return ProviderNutritionOutput.model_validate_json(json.dumps({**VALID_OUTPUT, **changes}))


def test_valid_output_parses():
    output = parse()

    assert (output.calories_kcal, output.protein_grams, output.carbs_grams, output.fat_grams) == (
        620,
        52.5,
        58,
        20,
    )
    assert output.assumptions == ["about 180 g chicken breast", "about 250 g potatoes"]


def test_empty_assumptions_are_allowed_and_zero_is_a_valid_value():
    output = parse(assumptions=[], fat_grams=0)

    assert output.assumptions == []
    assert output.fat_grams == 0


@pytest.mark.parametrize("field", ["calories_kcal", "protein_grams", "carbs_grams", "fat_grams"])
@pytest.mark.parametrize("value", [-1, -0.1, "620", True, None, 1e9])
def test_values_must_be_finite_non_negative_bounded_numbers(field, value):
    with pytest.raises(ValidationError):
        parse(**{field: value})


@pytest.mark.parametrize("literal", ["NaN", "Infinity", "-Infinity"])
def test_non_finite_values_are_rejected(literal):
    raw = json.dumps(VALID_OUTPUT).replace('"calories_kcal": 620', f'"calories_kcal": {literal}')

    with pytest.raises(ValidationError):
        ProviderNutritionOutput.model_validate_json(raw)


@pytest.mark.parametrize(
    "field", ["status", "calories_kcal", "protein_grams", "carbs_grams", "fat_grams", "assumptions"]
)
def test_missing_fields_are_rejected(field):
    payload = {key: value for key, value in VALID_OUTPUT.items() if key != field}

    with pytest.raises(ValidationError):
        ProviderNutritionOutput.model_validate(payload)


@pytest.mark.parametrize(
    "changes",
    [
        {"status": "maybe"},
        {"assumptions": "about 180 g chicken"},
        {"assumptions": [""]},
        {"assumptions": ["x"] * 11},
        {"assumptions": ["x" * 301]},
        {"confidence": 0.9},
        {"fibre_grams": 4},
    ],
)
def test_malformed_output_is_rejected(changes):
    with pytest.raises(ValidationError):
        parse(**changes)


def test_estimate_has_only_calories_macros_and_assumptions():
    assert set(NutritionEstimate.model_fields) == {
        "calories_kcal",
        "protein_grams",
        "carbs_grams",
        "fat_grams",
        "assumptions",
    }
