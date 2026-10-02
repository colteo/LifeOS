from dataclasses import dataclass
from datetime import date
from decimal import Decimal, InvalidOperation
from typing import Any

from lifeos_ai_evals.core.engine import nonempty_string


@dataclass(frozen=True, order=True)
class Trip:
    start_date: str
    end_date: str

    def __post_init__(self):
        start = calendar_date(self.start_date)
        end = calendar_date(self.end_date)
        if start > end:
            raise ValueError("trip start_date must be <= end_date")


def calendar_date(value: Any) -> date:
    if not isinstance(value, str):
        raise ValueError("date must be an ISO calendar date")
    parsed = date.fromisoformat(value)
    if parsed.isoformat() != value:
        raise ValueError("date must use YYYY-MM-DD")
    return parsed


@dataclass(frozen=True)
class Event:
    date: str
    transaction_type: str
    amount: str
    currency: str
    category: str
    description: str


def parse_input(value: Any) -> tuple[Event, ...]:
    if not isinstance(value, dict) or set(value) != {"events"}:
        raise ValueError("input must contain only events")
    if not isinstance(value["events"], list):
        raise ValueError("events must be a list")
    events = []
    for item in value["events"]:
        fields = {
            "date",
            "transaction_type",
            "amount",
            "currency",
            "category",
            "description",
        }
        if not isinstance(item, dict) or set(item) != fields:
            raise ValueError(f"event requires exactly {sorted(fields)}")
        calendar_date(item["date"])
        for key in fields:
            nonempty_string(item[key], key)
        if item["transaction_type"] not in {"expense", "income", "transfer"}:
            raise ValueError("invalid transaction_type")
        try:
            amount = Decimal(item["amount"])
        except InvalidOperation as exc:
            raise ValueError("amount must be a decimal string") from exc
        if not amount.is_finite() or amount < 0:
            raise ValueError("amount must be finite and nonnegative")
        currency = item["currency"]
        if (
            len(currency) != 3
            or not currency.isascii()
            or not currency.isupper()
            or not currency.isalpha()
        ):
            raise ValueError("currency must be three uppercase ASCII letters")
        events.append(Event(**item))
    return tuple(events)


def parse_expected(value: Any) -> tuple[Trip, ...]:
    if not isinstance(value, list):
        raise ValueError("expected must be a list of trip intervals")
    trips = []
    for item in value:
        if not isinstance(item, dict) or set(item) != {"start_date", "end_date"}:
            raise ValueError("trip requires start_date and end_date")
        trips.append(Trip(**item))
    trips.sort()
    for previous, current in zip(trips, trips[1:], strict=False):
        if previous.end_date >= current.start_date:
            raise ValueError("ground truth intervals must not overlap")
    return tuple(trips)
