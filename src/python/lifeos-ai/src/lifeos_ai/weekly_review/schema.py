"""AI-001: the weekly-review interpretation contract (request, provider output, response).

The request is the deterministic figures of ONE saved LifeOS weekly review, already computed by
.NET: no identifiers, no dates (days are weekday names), no time zone. Unknown fields are
rejected, so nothing else can be sent by mistake. The output is a small, versioned structure of
short plain-text statements; every bound here is repeated by the .NET Domain before anything is
stored.
"""

from typing import Annotated, Literal

from pydantic import AfterValidator, BaseModel, ConfigDict, Field, StringConstraints

# ---- Output contract (version 1) ----

OUTPUT_VERSION = 1
MAX_SUMMARY_LENGTH = 400
MAX_ITEM_LENGTH = 200
MAX_ITEMS = 3

# Markdown/list markers: the canonical format is plain sentences, never a formatted blob.
_MARKDOWN_PREFIXES = ("#", "- ", "* ", "> ", "+ ")
_MARKDOWN_FRAGMENTS = ("**", "__", "`", "](")


def _plain_single_line(text: str) -> str:
    if any(ord(char) < 32 or ord(char) == 127 for char in text):
        raise ValueError("text must be a single plain line")
    if text.startswith(_MARKDOWN_PREFIXES) or any(f in text for f in _MARKDOWN_FRAGMENTS):
        raise ValueError("text must not contain markdown")
    return text


Summary = Annotated[
    str,
    StringConstraints(strip_whitespace=True, min_length=1, max_length=MAX_SUMMARY_LENGTH),
    AfterValidator(_plain_single_line),
]
Statement = Annotated[
    str,
    StringConstraints(strip_whitespace=True, min_length=1, max_length=MAX_ITEM_LENGTH),
    AfterValidator(_plain_single_line),
]


class WeeklyReviewInsights(BaseModel):
    """The model's structured output, validated before it can leave the service."""

    model_config = ConfigDict(extra="forbid")

    summary: Summary
    wins: list[Statement] = Field(max_length=MAX_ITEMS)
    attention: list[Statement] = Field(max_length=MAX_ITEMS)
    patterns: list[Statement] = Field(max_length=MAX_ITEMS)
    next_week_focus: list[Statement] = Field(max_length=MAX_ITEMS)


class InterpretWeeklyReviewResponse(BaseModel):
    """The service's answer: the insights plus the identity needed to attribute them."""

    model_config = ConfigDict(extra="forbid")

    output_version: Literal[1]
    provider: str
    model: str
    prompt_version: str
    insights: WeeklyReviewInsights


# Strict structured-output schema sent to the provider. Lengths, counts and the plain-text rule
# stay in Pydantic (WeeklyReviewInsights), which validates every response regardless of provider.
_STATEMENTS = {"type": "array", "items": {"type": "string"}}
OUTPUT_SCHEMA = {
    "type": "object",
    "additionalProperties": False,
    "required": ["summary", "wins", "attention", "patterns", "next_week_focus"],
    "properties": {
        "summary": {"type": "string"},
        "wins": _STATEMENTS,
        "attention": _STATEMENTS,
        "patterns": _STATEMENTS,
        "next_week_focus": _STATEMENTS,
    },
}

# ---- Request (the saved snapshot, minimised) ----

MAX_NAME_LENGTH = 100
MAX_CURRENCIES = 10
MAX_CATEGORIES = 12
MAX_WORKOUTS = 28
MAX_MONEY = 1e12
MAX_COUNT = 100_000
MAX_SECONDS = 1e9
MAX_NUTRITION = 1e7

Weekday = Literal["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"]
Name = Annotated[
    str, StringConstraints(strip_whitespace=True, min_length=1, max_length=MAX_NAME_LENGTH)
]


def _number(minimum: float, maximum: float):
    # Strict: a JSON number only (no strings, no booleans), finite and bounded.
    return Field(strict=True, ge=minimum, le=maximum, allow_inf_nan=False)


def _count(maximum: int = MAX_COUNT):
    return Field(strict=True, ge=0, le=maximum)


class _Data(BaseModel):
    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)


class ExpenseCategory(_Data):
    name: Name
    amount: float = _number(0, MAX_MONEY)


class CurrencySummary(_Data):
    currency: str = Field(pattern=r"^[A-Z]{3}$")
    expenses: float = _number(0, MAX_MONEY)
    income: float = _number(0, MAX_MONEY)
    net_flow: float = _number(-MAX_MONEY, MAX_MONEY)
    expense_categories: list[ExpenseCategory] = Field(max_length=MAX_CATEGORIES)


class FinanceSummary(_Data):
    currencies: list[CurrencySummary] = Field(max_length=MAX_CURRENCIES)


class Workout(_Data):
    day: Weekday
    workout_name: Name
    program_name: Name
    duration_seconds: int = _count(int(MAX_SECONDS))
    completed_sets: int = _count()
    prescribed_sets: int = _count()


class GymSummary(_Data):
    completed_workouts: int = _count()
    total_duration_seconds: int = _count(int(MAX_SECONDS))
    completed_sets: int = _count()
    prescribed_sets: int = _count()
    workouts: list[Workout] = Field(max_length=MAX_WORKOUTS)


class NutritionDay(_Data):
    day: Weekday
    meal_count: int = _count()
    analyzed_meal_count: int = _count()
    calories_kcal: float = _number(0, MAX_NUTRITION)
    protein_grams: float = _number(0, MAX_NUTRITION)
    carbs_grams: float = _number(0, MAX_NUTRITION)
    fat_grams: float = _number(0, MAX_NUTRITION)


class NutritionSummary(_Data):
    days_with_meals: int = _count(7)
    meal_count: int = _count()
    analyzed_meal_count: int = _count()
    fully_analyzed_days: int = _count(7)
    analyzed_calories_kcal: float = _number(0, MAX_NUTRITION)
    analyzed_protein_grams: float = _number(0, MAX_NUTRITION)
    analyzed_carbs_grams: float = _number(0, MAX_NUTRITION)
    analyzed_fat_grams: float = _number(0, MAX_NUTRITION)
    days: list[NutritionDay] = Field(max_length=7)


class InterpretWeeklyReviewRequest(_Data):
    """Everything the interpreter may know: one week's deterministic figures."""

    finance: FinanceSummary
    gym: GymSummary
    nutrition: NutritionSummary
