"""Scenario, expectation and run-trace payloads for the Action Agent evaluator.

A scenario is what LifeOS would observe for one run: the task framing, the weekly
review's finance section exactly as `get_weekly_review` returns it, and the budgets that
exist, each exactly as `get_budget_status` returns it. Budgets not listed are "not set".
Figures are checked for internal consistency (net flow, remaining, free to spend) so a
case cannot contradict LifeOS arithmetic.
"""

from dataclasses import dataclass
from decimal import Decimal, InvalidOperation
from typing import Any

from lifeos_ai_evals.evaluators.action_agent.dotnet_mirror import month_label

DECISIONS = ("propose", "no_action", "either")
BUDGET_FIELDS = {
    "year",
    "month",
    "currency",
    "budget_set",
    "amount",
    "spent",
    "remaining",
    "expected_recurring_expenses",
    "expected_planned_expenses",
    "free_to_spend",
}
CURRENCY_FIELDS = {
    "currency",
    "expenses",
    "income",
    "net_flow",
    "top_expense_categories",
}


def _number(value: Any, label: str) -> Decimal:
    if isinstance(value, bool) or not isinstance(value, int | float):
        raise ValueError(f"{label} must be a JSON number")
    try:
        return Decimal(str(value))
    except InvalidOperation as exc:
        raise ValueError(f"{label} must be finite") from exc


def _code(value: Any) -> str:
    if (
        not isinstance(value, str)
        or len(value) != 3
        or not value.isascii()
        or not value.isupper()
    ):
        raise ValueError("currency must be three uppercase ASCII letters")
    return value


def _month(value: Any) -> tuple[int, int]:
    if not isinstance(value, str) or len(value) != 7 or value[4] != "-":
        raise ValueError("months use YYYY-MM")
    year, month = int(value[:4]), int(value[5:])
    if not 1 <= month <= 12 or month_label(year, month) != value:
        raise ValueError("months use YYYY-MM")
    return year, month


def _next(year: int, month: int) -> str:
    return month_label(year + 1, 1) if month == 12 else month_label(year, month + 1)


@dataclass(frozen=True)
class Scenario:
    context: dict
    weekly_review: dict
    budgets: tuple[dict, ...]
    readable_months: tuple[str, ...]

    def budget(self, month: str, currency: str) -> dict | None:
        for budget in self.budgets:
            if (
                month_label(budget["year"], budget["month"]) == month
                and budget["currency"] == currency
            ):
                return budget
        return None


@dataclass(frozen=True)
class Bounds:
    year: int
    month: int
    currency: str
    min: float
    max: float


@dataclass(frozen=True)
class Expectations:
    decision: str
    max_useful_read_calls: int
    proposal_bounds: Bounds | None = None
    source: dict | None = None


@dataclass(frozen=True)
class ReadCall:
    tool: str
    valid_arguments: bool
    call_key: str


@dataclass(frozen=True)
class ProposalRecord:
    """The model's proposal as emitted, and what the .NET checks said (None = would be
    stored)."""

    year: int
    month: int
    currency: str
    proposed_amount: str
    server_check: str | None


@dataclass(frozen=True)
class RunTrace:
    outcome: str  # proposed | no_action | failed
    error_code: str | None
    steps: int
    read_calls: tuple[ReadCall, ...]
    emitted_tool_names: tuple[
        tuple[str, ...], ...
    ]  # per model step, as the model answered
    observations: tuple[
        tuple[str, str, str | None], ...
    ]  # (month, currency, amount|None)
    proposal: ProposalRecord | None = None


def parse_input(value: Any) -> Scenario:
    if not isinstance(value, dict) or set(value) != {
        "context",
        "weekly_review",
        "budgets",
    }:
        raise ValueError("input requires exactly context, weekly_review, budgets")
    context = value["context"]
    if not isinstance(context, dict) or set(context) != {
        "current_month",
        "target_months",
    }:
        raise ValueError("context requires exactly current_month, target_months")
    current = _month(context["current_month"])
    # RunActionAgentHandler: targets are the current and the next local month, in that
    # order.
    if context["target_months"] != [context["current_month"], _next(*current)]:
        raise ValueError("target_months must be [current month, next month]")

    review = value["weekly_review"]
    if not isinstance(review, dict) or set(review) != {
        "week_start",
        "week_end",
        "currencies",
    }:
        raise ValueError(
            "weekly_review requires exactly week_start, week_end, currencies"
        )
    months = set(context["target_months"])
    for key in ("week_start", "week_end"):
        if not isinstance(review[key], str) or len(review[key]) != 10:
            raise ValueError("week dates use YYYY-MM-DD")
        months.add(month_label(*_month(review[key][:7])))
    if not isinstance(review["currencies"], list) or len(review["currencies"]) > 10:
        raise ValueError("currencies must be a list of at most 10")
    for currency in review["currencies"]:
        if not isinstance(currency, dict) or set(currency) != CURRENCY_FIELDS:
            raise ValueError(
                f"review currency requires exactly {sorted(CURRENCY_FIELDS)}"
            )
        _code(currency["currency"])
        expenses = _number(currency["expenses"], "expenses")
        income = _number(currency["income"], "income")
        if _number(currency["net_flow"], "net_flow") != income - expenses:
            raise ValueError("net_flow must equal income - expenses")
        categories = currency["top_expense_categories"]
        if not isinstance(categories, list) or len(categories) > 5:
            raise ValueError("top_expense_categories: at most 5")
        for category in categories:
            if not isinstance(category, dict) or set(category) != {"name", "amount"}:
                raise ValueError("category requires exactly name, amount")
            name = category["name"]
            if not isinstance(name, str) or not name.strip() or len(name) > 100:
                raise ValueError("category names are 1-100 characters")
            _number(category["amount"], "category amount")

    budgets = value["budgets"]
    if not isinstance(budgets, list):
        raise ValueError("budgets must be a list")
    seen = set()
    for budget in budgets:
        if not isinstance(budget, dict):
            raise ValueError("budget must be an object")
        label = month_label(budget.get("year", 0), budget.get("month", 0))
        # MonthlyBudgetCalculator: remaining days exist only for the current local
        # month.
        expected = BUDGET_FIELDS | (
            {"remaining_days"} if label == context["current_month"] else set()
        )
        if set(budget) != expected or budget["budget_set"] is not True:
            raise ValueError(f"budget {label} requires exactly {sorted(expected)}")
        _month(label)
        key = (label, _code(budget["currency"]))
        if key in seen:
            raise ValueError("duplicate budget")
        seen.add(key)
        amount, spent = (
            _number(budget["amount"], "amount"),
            _number(budget["spent"], "spent"),
        )
        remaining = _number(budget["remaining"], "remaining")
        expected_expenses = _number(
            budget["expected_recurring_expenses"], "recurring"
        ) + _number(budget["expected_planned_expenses"], "planned")
        if amount <= 0 or remaining != amount - spent:
            raise ValueError("remaining must equal amount - spent, amount positive")
        if (
            _number(budget["free_to_spend"], "free_to_spend")
            != remaining - expected_expenses
        ):
            raise ValueError("free_to_spend must equal remaining - expected expenses")
        if "remaining_days" in budget and (
            type(budget["remaining_days"]) is not int
            or not 1 <= budget["remaining_days"] <= 31
        ):
            raise ValueError("remaining_days must be 1-31")
    return Scenario(dict(context), review, tuple(budgets), tuple(sorted(months)))


def parse_expected(value: Any) -> Expectations:
    if (
        not isinstance(value, dict)
        or not {"decision", "max_useful_read_calls"} <= set(value)
        or not set(value) <= {"decision", "max_useful_read_calls", "proposal_bounds"}
    ):
        raise ValueError(
            "expected requires decision, max_useful_read_calls[, proposal_bounds]"
        )
    if value["decision"] not in DECISIONS:
        raise ValueError(f"decision must be one of {DECISIONS}")
    useful = value["max_useful_read_calls"]
    if type(useful) is not int or not 0 <= useful <= 4:
        raise ValueError("max_useful_read_calls must be 0-4")
    bounds = value.get("proposal_bounds")
    if bounds is not None:
        if value["decision"] == "no_action":
            raise ValueError(
                "proposal_bounds require a decision that allows a proposal"
            )
        if not isinstance(bounds, dict) or set(bounds) != {
            "year",
            "month",
            "currency",
            "min",
            "max",
        }:
            raise ValueError("proposal_bounds requires year, month, currency, min, max")
        _code(bounds["currency"])
        if _number(bounds["min"], "min") > _number(bounds["max"], "max"):
            raise ValueError("proposal_bounds min must be <= max")
        bounds = Bounds(**bounds)
    return Expectations(value["decision"], useful, bounds)
