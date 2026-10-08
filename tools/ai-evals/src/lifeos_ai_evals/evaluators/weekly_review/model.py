"""Case payloads. Inputs are validated by the PRODUCTION request model, so a dataset
case is always a request the production endpoint would accept (drift between dataset and
contract fails loading)."""

import re
from dataclasses import dataclass
from typing import Any

from lifeos_ai.weekly_review.schema import (
    InterpretWeeklyReviewRequest,
    WeeklyReviewInsights,
)

FIELDS = ("summary", "wins", "attention", "patterns", "next_week_focus")


@dataclass(frozen=True)
class Expectations:
    """Author labels. Everything else the scorer needs is derived from `source` (the
    input).

    forbidden_patterns: case-specific regular expressions (case-insensitive) whose
    presence means the output followed an injected instruction or took a misleading name
    as fact.
    """

    forbidden_patterns: tuple[str, ...] = ()
    source: dict | None = None


@dataclass(frozen=True)
class InsightsOutput:
    """What a system produced. `valid` is False when the production contract rejected
    it."""

    valid: bool
    failure: str | None = None
    summary: str = ""
    wins: tuple[str, ...] = ()
    attention: tuple[str, ...] = ()
    patterns: tuple[str, ...] = ()
    next_week_focus: tuple[str, ...] = ()

    @classmethod
    def from_insights(cls, insights: WeeklyReviewInsights) -> "InsightsOutput":
        return cls(
            True,
            None,
            insights.summary,
            tuple(insights.wins),
            tuple(insights.attention),
            tuple(insights.patterns),
            tuple(insights.next_week_focus),
        )

    @classmethod
    def invalid(cls, failure: str) -> "InsightsOutput":
        return cls(False, failure)

    def statements(self) -> list[tuple[str, str]]:
        """(field label, text) for every statement, summary first."""
        items = [("summary", self.summary)] if self.summary else []
        for name in FIELDS[1:]:
            items.extend(
                (f"{name}[{index}]", text)
                for index, text in enumerate(getattr(self, name))
            )
        return items


def parse_input(value: Any) -> InterpretWeeklyReviewRequest:
    if not isinstance(value, dict):
        raise ValueError("input must be a weekly review request object")
    return InterpretWeeklyReviewRequest.model_validate(value)


def parse_expected(value: Any) -> Expectations:
    if not isinstance(value, dict) or set(value) != {"forbidden_patterns"}:
        raise ValueError("expected requires exactly forbidden_patterns")
    patterns = value["forbidden_patterns"]
    if not isinstance(patterns, list) or any(
        not isinstance(item, str) or not item.strip() for item in patterns
    ):
        raise ValueError("forbidden_patterns must be a list of nonempty strings")
    for pattern in patterns:
        re.compile(pattern)
    return Expectations(tuple(patterns))
