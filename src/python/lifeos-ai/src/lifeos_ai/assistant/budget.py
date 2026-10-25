"""AI-007 F3-B1: token accounting and a daily/monthly spend guard for the assistant's provider.

The guard is a LifeOS-side brake, not a guaranteed cap: it lives in the process memory, so every
restart (deploy, crash, idle spin-down) starts again from zero and each instance counts only its
own requests; N restarts in a day can spend up to N+1 daily budgets. The outer limit is the OpenAI
project's hard spend limit (an owner setting at OpenAI, ADR-014), which is not exact either: per
OpenAI's spend-limits guide enforcement "is not instantaneous" and recorded spend "can slightly
exceed" it; spend alerts only notify. Before a request the guard reserves a worst-case cost;
afterwards the reservation is replaced by the reported usage (plus the worst case of earlier
failed attempts, which may have been billed), or kept whole when the usage is unknown. Days and
months are UTC.

Costs are estimates from configured prices (USD per million tokens); they never decide anything
but whether to send the next request.
"""

import threading
from collections.abc import Callable
from dataclasses import dataclass
from datetime import UTC, datetime
from decimal import Decimal

MILLION = Decimal(1_000_000)


@dataclass(frozen=True)
class Prices:
    """USD per 1M tokens. Set `input_per_mtok` to the highest input rate that can apply (e.g. a
    cache-write rate above the plain input rate) so estimates stay conservative."""

    input_per_mtok: Decimal
    output_per_mtok: Decimal

    def cost(self, input_tokens: int, output_tokens: int) -> Decimal:
        return (
            Decimal(input_tokens) * self.input_per_mtok
            + Decimal(output_tokens) * self.output_per_mtok
        ) / MILLION


class BudgetExhausted(Exception):
    """The next request could exceed the daily or monthly budget: nothing is sent."""

    def __init__(self, period: str):
        super().__init__(period)
        self.period = period


@dataclass
class Reservation:
    amount: Decimal
    day: str
    month: str
    settled: bool = False


class SpendGuard:
    def __init__(
        self,
        *,
        daily_budget: Decimal,
        monthly_budget: Decimal,
        now: Callable[[], datetime] = lambda: datetime.now(UTC),
    ):
        if daily_budget <= 0 or monthly_budget <= 0:
            raise ValueError("budgets must be positive")
        self._daily = daily_budget
        self._monthly = monthly_budget
        self._now = now
        self._lock = threading.Lock()
        self._spent_day: dict[str, Decimal] = {}
        self._spent_month: dict[str, Decimal] = {}

    def reserve(self, amount: Decimal) -> Reservation:
        day, month = self._periods()
        with self._lock:
            spent_day = self._spent_day.get(day, Decimal(0))
            spent_month = self._spent_month.get(month, Decimal(0))
            if spent_day + amount > self._daily:
                raise BudgetExhausted("daily")
            if spent_month + amount > self._monthly:
                raise BudgetExhausted("monthly")
            self._spent_day = {day: spent_day + amount}
            self._spent_month = {month: spent_month + amount}
        return Reservation(amount, day, month)

    def settle(self, reservation: Reservation, actual: Decimal | None) -> None:
        """Replace the reservation with the actual cost; `None` keeps the whole reservation."""
        self._adjust(reservation, reservation.amount if actual is None else actual)

    def release(self, reservation: Reservation) -> None:
        """Nothing was sent: the reservation is returned in full."""
        self._adjust(reservation, Decimal(0))

    def spent(self) -> tuple[Decimal, Decimal]:
        day, month = self._periods()
        with self._lock:
            return self._spent_day.get(day, Decimal(0)), self._spent_month.get(month, Decimal(0))

    def _adjust(self, reservation: Reservation, final: Decimal) -> None:
        with self._lock:
            if reservation.settled:
                return
            reservation.settled = True
            delta = final - reservation.amount
            if reservation.day in self._spent_day:
                self._spent_day[reservation.day] += delta
            if reservation.month in self._spent_month:
                self._spent_month[reservation.month] += delta

    def _periods(self) -> tuple[str, str]:
        now = self._now().astimezone(UTC)
        return now.strftime("%Y-%m-%d"), now.strftime("%Y-%m")
