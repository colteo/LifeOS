"""Synthetic Quick Capture requests and answers, taken from the contracts (no invented data): each
role's example request and the valid golden case written for exactly that request."""

import copy
import json
from pathlib import Path

CONTRACTS = Path(__file__).resolve().parents[4] / "docs/contracts/quick-capture"
ROLES = ("router", "finance", "hydration", "meal")
PATHS = {
    "router": "/v1/quick-capture/route",
    "finance": "/v1/quick-capture/finance/interpret",
    "hydration": "/v1/quick-capture/hydration/interpret",
    "meal": "/v1/quick-capture/meal/interpret",
}
_ANSWER_CASES = {
    "router": "three_modules_shared_date",
    "finance": "create_expense_with_context_date",
    "hydration": "correct_awaited_amount",
    "meal": "create_with_type_and_context_date",
}


def _load(role: str, name: str):
    return json.loads((CONTRACTS / role / "v1" / name).read_text(encoding="utf-8"))


def request(role: str) -> dict:
    """The contract's example request of the role."""
    return copy.deepcopy(_load(role, "request.example.json"))


def answer(role: str) -> dict:
    """A valid output for `request(role)`."""
    case = next(c for c in _load(role, "cases.json")["cases"] if c["name"] == _ANSWER_CASES[role])
    return copy.deepcopy(case["interpretation"])


def case(role: str, name: str) -> dict:
    return copy.deepcopy(next(c for c in _load(role, "cases.json")["cases"] if c["name"] == name))
