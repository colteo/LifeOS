"""AI-001: the weekly-review request boundary and the insights output contract."""

import json

import pytest
from pydantic import ValidationError

from lifeos_ai.weekly_review.schema import (
    MAX_CATEGORIES,
    MAX_ITEM_LENGTH,
    MAX_ITEMS,
    MAX_NAME_LENGTH,
    MAX_SUMMARY_LENGTH,
    OUTPUT_SCHEMA,
    InterpretWeeklyReviewRequest,
    InterpretWeeklyReviewResponse,
    WeeklyReviewInsights,
)
from tests.weekly_review_fixtures import (
    EMPTY_INSIGHTS,
    EMPTY_WEEK,
    TYPICAL_WEEK,
    VALID_INSIGHTS,
    week,
)

# ---- Request ----


def test_typical_and_empty_weeks_are_valid_requests():
    typical = InterpretWeeklyReviewRequest.model_validate(TYPICAL_WEEK)
    empty = InterpretWeeklyReviewRequest.model_validate(EMPTY_WEEK)

    assert typical.finance.currencies[0].net_flow == -132.4
    assert typical.gym.workouts[1].day == "Thursday"
    assert empty.nutrition.days == []


@pytest.mark.parametrize(
    "field", ["user_id", "review_id", "week_end_date", "time_zone_id", "generated_at_utc"]
)
def test_request_rejects_identifiers_dates_and_any_other_data(field):
    # The privacy boundary: only the week's figures may be sent.
    with pytest.raises(ValidationError):
        InterpretWeeklyReviewRequest.model_validate({**TYPICAL_WEEK, field: "x"})


def test_nested_sections_reject_unknown_fields_too():
    data = week()
    data["gym"]["workouts"][0]["date"] = "2026-09-28"

    with pytest.raises(ValidationError):
        InterpretWeeklyReviewRequest.model_validate(data)


@pytest.mark.parametrize(
    "mutate",
    [
        lambda d: d["finance"]["currencies"][0].update(currency="eur"),
        lambda d: d["finance"]["currencies"][0].update(expenses=-1),
        lambda d: d["finance"]["currencies"][0].update(income="50"),
        lambda d: d["finance"]["currencies"][0]["expense_categories"].extend(
            [{"name": "x", "amount": 1}] * MAX_CATEGORIES
        ),
        lambda d: d["gym"]["workouts"][0].update(day="2026-09-28"),
        lambda d: d["gym"]["workouts"][0].update(workout_name="x" * (MAX_NAME_LENGTH + 1)),
        lambda d: d["gym"]["workouts"][0].update(workout_name="  "),
        lambda d: d["gym"].update(completed_sets=True),
        lambda d: d["nutrition"].update(days_with_meals=8),
        lambda d: d["nutrition"].update(analyzed_calories_kcal=float("nan")),
        lambda d: d.pop("gym"),
    ],
)
def test_request_rejects_invalid_or_unbounded_figures(mutate):
    data = week()
    mutate(data)

    with pytest.raises(ValidationError):
        InterpretWeeklyReviewRequest.model_validate(data)


# ---- Output ----


def parse(**changes) -> WeeklyReviewInsights:
    return WeeklyReviewInsights.model_validate_json(json.dumps({**VALID_INSIGHTS, **changes}))


def test_valid_and_empty_insights_parse():
    assert parse().attention[1].startswith("1 of 5 meals")
    assert WeeklyReviewInsights.model_validate(EMPTY_INSIGHTS).wins == []


def test_text_is_trimmed():
    assert parse(summary="  A quiet week.  ").summary == "A quiet week."


@pytest.mark.parametrize("field", ["summary", "wins", "attention", "patterns", "next_week_focus"])
def test_every_field_is_required(field):
    with pytest.raises(ValidationError):
        WeeklyReviewInsights.model_validate(
            {key: value for key, value in VALID_INSIGHTS.items() if key != field}
        )


@pytest.mark.parametrize(
    "changes",
    [
        # Counts and lengths.
        {"wins": ["A fact."] * (MAX_ITEMS + 1)},
        {"next_week_focus": ["A fact."] * (MAX_ITEMS + 1)},
        {"summary": "x" * (MAX_SUMMARY_LENGTH + 1)},
        {"patterns": ["x" * (MAX_ITEM_LENGTH + 1)]},
        {"summary": ""},
        {"summary": "   "},
        {"attention": [""]},
        # Types and unknown fields.
        {"wins": "You trained twice."},
        {"wins": [3]},
        {"summary": None},
        {"confidence": 0.9},
        {"markdown": "# Week"},
        # Plain single-line text only: no free-form markdown blob.
        {"summary": "Line one.\nLine two."},
        {"summary": "# Your week"},
        {"wins": ["- You trained twice."]},
        {"wins": ["You **trained** twice."]},
        {"patterns": ["See [details](https://example.test)."]},
        {"attention": ["Use `code`."]},
    ],
)
def test_invalid_output_is_rejected(changes):
    with pytest.raises(ValidationError):
        parse(**changes)


def test_limits_are_inclusive():
    insights = parse(
        summary="x" * MAX_SUMMARY_LENGTH,
        wins=["y" * MAX_ITEM_LENGTH] * MAX_ITEMS,
    )

    assert len(insights.summary) == MAX_SUMMARY_LENGTH
    assert len(insights.wins) == MAX_ITEMS


def test_negative_numbers_in_text_are_not_mistaken_for_markdown():
    assert parse(attention=["-132.4 EUR net flow this week."]).attention


def test_the_provider_schema_matches_the_contract_fields():
    assert OUTPUT_SCHEMA["additionalProperties"] is False
    assert set(OUTPUT_SCHEMA["required"]) == set(WeeklyReviewInsights.model_fields)
    assert set(OUTPUT_SCHEMA["properties"]) == set(WeeklyReviewInsights.model_fields)


def test_the_response_is_versioned_and_attributed():
    assert set(InterpretWeeklyReviewResponse.model_fields) == {
        "output_version",
        "provider",
        "model",
        "prompt_version",
        "insights",
    }
    with pytest.raises(ValidationError):
        InterpretWeeklyReviewResponse(
            output_version=2, provider="p", model="m", prompt_version="v", insights=parse()
        )
