"""Deterministic numeric grounding (weekly-review-grounding-v1).

A numeric claim is any digit sequence in the output text after user-typed names are
masked (quoting a name such as "5x5 Strength" is not a figure). A claim is grounded when
its absolute value equals an absolute value in the index built from the input:

- every numeric field (sign ignored: "a deficit of 132.4" vs net_flow -132.4);
- list lengths (currencies, categories, workouts, nutrition days);
- documented simple derivations: unfinished sets, unanalyzed meals, days without meals,
  seconds as minutes/hours, distinct workout days/programs, fully/partially completed
  workouts;
- the constant 7 (days in the week).

Formatting equivalents: thousands separators and trailing zeros (132.4 = 132.40 =
132.400), and rounding: a claim with fewer decimals than a source value is grounded when
it is that value rounded to the claim's precision (2650.5 -> 2650 or 2651; 1.9166 h ->
1.9). A claim followed by "%" is grounded only against derived percentages (sets
completed, meals analyzed, a category's share of its currency's expenses), never against
raw figures.

Not detected (stated limits): numbers written as words, wrong signs/units/currencies
attached to a correct number, a correct number attached to the wrong subject,
non-numeric hallucinations.
"""

import re
from decimal import Decimal

from lifeos_ai.weekly_review.schema import InterpretWeeklyReviewRequest

VERSION = "weekly-review-grounding-v1"
CONSTANTS = (Decimal(7),)
NUMBER = re.compile(r"(?<![\w.])(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?(?:\s?%)?")
NAME_MASK = "⁣"  # invisible separator: keeps sentence shape, contains no digit or letter


def names(source: dict) -> list[str]:
    found = set()
    for currency in source["finance"]["currencies"]:
        found.update(category["name"] for category in currency["expense_categories"])
    for workout in source["gym"]["workouts"]:
        found.update((workout["workout_name"], workout["program_name"]))
    return sorted(found, key=lambda name: (-len(name), name))


def mask_names(text: str, source: dict) -> str:
    for name in names(source):
        text = re.sub(re.escape(name), NAME_MASK, text, flags=re.IGNORECASE)
    return text


def _decimal(value) -> Decimal:
    return abs(Decimal(str(value)))


def _ratio(part, whole) -> list[Decimal]:
    return [_decimal(part) * 100 / _decimal(whole)] if whole else []


def index(source: dict) -> tuple[set[Decimal], set[Decimal]]:
    """(values, percentages) a claim may be grounded in."""
    values: set[Decimal] = set(CONSTANTS)
    percents: set[Decimal] = set()

    def leaves(node):
        if isinstance(node, dict):
            for child in node.values():
                leaves(child)
        elif isinstance(node, list):
            values.add(Decimal(len(node)))
            for child in node:
                leaves(child)
        elif isinstance(node, int | float) and not isinstance(node, bool):
            values.add(_decimal(node))

    leaves(source)
    for currency in source["finance"]["currencies"]:
        for category in currency["expense_categories"]:
            percents.update(_ratio(category["amount"], currency["expenses"]))

    gym = source["gym"]
    for item in [gym, *gym["workouts"]]:
        values.add(_decimal(item["prescribed_sets"] - item["completed_sets"]))
        percents.update(_ratio(item["completed_sets"], item["prescribed_sets"]))
    for seconds in [
        gym["total_duration_seconds"],
        *(w["duration_seconds"] for w in gym["workouts"]),
    ]:
        values.update((_decimal(seconds) / 60, _decimal(seconds) / 3600))
    workouts = gym["workouts"]
    values.add(Decimal(len({w["day"] for w in workouts})))
    values.add(Decimal(len({w["program_name"].casefold() for w in workouts})))
    values.add(
        Decimal(sum(w["completed_sets"] >= w["prescribed_sets"] for w in workouts))
    )
    values.add(
        Decimal(sum(w["completed_sets"] < w["prescribed_sets"] for w in workouts))
    )
    for day in workouts:
        values.add(Decimal(sum(w["day"] == day["day"] for w in workouts)))

    nutrition = source["nutrition"]
    values.add(Decimal(7 - nutrition["days_with_meals"]))
    for item in [nutrition, *nutrition["days"]]:
        values.add(_decimal(item["meal_count"] - item["analyzed_meal_count"]))
        percents.update(_ratio(item["analyzed_meal_count"], item["meal_count"]))
    return values, percents


def decimals(value: Decimal) -> int:
    exponent = value.normalize().as_tuple().exponent
    return max(0, -exponent) if isinstance(exponent, int) else 0


def matches(claim: Decimal, claim_decimals: int, candidates: set[Decimal]) -> bool:
    if claim in candidates:
        return True
    tolerance = Decimal(5) * Decimal(10) ** -(claim_decimals + 1)
    return any(
        decimals(value) > claim_decimals and abs(value - claim) <= tolerance
        for value in candidates
    )


def check(statements: list[tuple[str, str]], source: dict) -> list[dict]:
    """One record per numeric claim: field, matched text, percent flag, grounded."""
    values, percents = index(source)
    claims = []
    for label, text in statements:
        for match in NUMBER.finditer(mask_names(text, source)):
            raw = match.group(0)
            percent = raw.endswith("%")
            digits = raw.rstrip("%").strip().replace(",", "")
            claim_decimals = len(digits.split(".")[1]) if "." in digits else 0
            value = Decimal(digits)
            grounded = matches(value, claim_decimals, percents if percent else values)
            claims.append(
                {"field": label, "text": raw, "percent": percent, "grounded": grounded}
            )
    return claims


def source_of(request: InterpretWeeklyReviewRequest) -> dict:
    return request.model_dump(mode="json")
