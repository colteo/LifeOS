"""Versioned mirror of the .NET side of the AI-002 loop (lifeos-run-loop-v1).

The production loop, tools and proposal validation are .NET (RunActionAgentHandler,
ActionAgentTools, ProposedAction). The Python service is called once per step. To replay
scenarios without a database, this module reproduces exactly what .NET sends and checks:

- the read-tool definitions (tool schema action-agent-tools-v1), byte-for-byte the .NET
  text;
- MaxSteps = 5, the repeated-call key, strict read-tool argument validation and readable
  months;
- proposal validation order: target month, grounded (read in this run), budget set, then
  the Domain rules (different from current, within MaxChangeFactor 2) -> stable error
  codes.

tests/test_action_agent_drift.py parses the .NET sources and fails when any of these
drift. It is a mirror, not the .NET code: behaviour outside these rules (persistence,
concurrency, approval) is not evaluated here.
"""

import json
from decimal import Decimal

LOOP_VERSION = "lifeos-run-loop-v1"
TOOL_SCHEMA_VERSION = "action-agent-tools-v1"
MAX_STEPS = 5
MAX_CHANGE_FACTOR = Decimal(2)

GET_WEEKLY_REVIEW = "get_weekly_review"
GET_BUDGET_STATUS = "get_budget_status"

# .NET RunActionAgentHandler error codes.
INVALID_OUTPUT = "invalid_output"
UNKNOWN_TOOL = "unknown_tool"
REPEATED_TOOL_CALL = "repeated_tool_call"
MAX_STEPS_REACHED = "max_steps"
PROPOSAL_MONTH_NOT_ALLOWED = "proposal_month_not_allowed"
PROPOSAL_NOT_GROUNDED = "proposal_not_grounded"
PROPOSAL_WITHOUT_BUDGET = "proposal_without_budget"
INVALID_PROPOSAL = "invalid_proposal"

READ_TOOLS = [
    {
        "name": GET_WEEKLY_REVIEW,
        "description": (
            "Returns the finance section of the saved weekly review being analysed: "
            "the week's "
            "dates and, per currency, expenses, income, net flow and the largest "
            "expense "
            "categories. Takes no arguments."
        ),
        "parameters": {
            "type": "object",
            "properties": {},
            "required": [],
            "additionalProperties": False,
        },
    },
    {
        "name": GET_BUDGET_STATUS,
        "description": (
            "Returns the monthly budget for one month and currency: whether one is "
            "set, its "
            "amount, spent so far, remaining, expected recurring and planned "
            "expenses, free to "
            "spend and remaining days. Read-only."
        ),
        "parameters": {
            "type": "object",
            "properties": {
                "year": {"type": "integer", "description": "Calendar year, e.g. 2026."},
                "month": {"type": "integer", "description": "Calendar month 1-12."},
                "currency": {
                    "type": "string",
                    "description": "3-letter currency code, e.g. EUR.",
                },
            },
            "required": ["year", "month", "currency"],
            "additionalProperties": False,
        },
    },
]
READ_TOOL_NAMES = frozenset(tool["name"] for tool in READ_TOOLS)

INT32 = (-(2**31), 2**31 - 1)


def month_label(year: int, month: int) -> str:
    return f"{year:04d}-{month:02d}"


def budget_arguments(arguments) -> tuple[str, str] | None:
    """ActionAgentTools.TryReadBudgetArguments: exactly {year, month, currency}, JSON
    integers, a valid month, a currency that normalizes (trim, upper) to 3 letters.
    (month, currency)."""
    if not isinstance(arguments, dict) or sorted(arguments) != [
        "currency",
        "month",
        "year",
    ]:
        return None
    year, month, currency = arguments["year"], arguments["month"], arguments["currency"]
    if any(
        type(value) is not int or not INT32[0] <= value <= INT32[1]
        for value in (year, month)
    ):
        return None
    if not isinstance(currency, str) or not 1 <= year <= 9998 or not 1 <= month <= 12:
        return None
    normalized = currency.strip().upper()
    if len(normalized) != 3 or not normalized.isascii() or not normalized.isalpha():
        return None
    return month_label(year, month), normalized


def call_key(tool: str, arguments) -> str:
    """ActionAgentTools.CallKey: a call is identified by meaning, not spelling."""
    if tool == GET_WEEKLY_REVIEW and arguments == {}:
        return GET_WEEKLY_REVIEW
    if (
        tool == GET_BUDGET_STATUS
        and (parsed := budget_arguments(arguments)) is not None
    ):
        return f"{GET_BUDGET_STATUS}:{parsed[0]}:{parsed[1]}"
    return f"{tool}:invalid:{json.dumps(arguments, separators=(',', ':'))}"


def invalid(message: str) -> dict:
    return {"error": "invalid_arguments", "message": message}


def execute(
    scenario, tool: str, arguments
) -> tuple[dict, tuple[str, str, Decimal | None] | None]:
    """ActionAgentTools.ExecuteAsync over a scenario: (result, observation or None).

    The observation is LifeOS's own record of a budget read: (month, currency,
    amount|None)."""
    if tool == GET_WEEKLY_REVIEW:
        if arguments != {}:
            return invalid("get_weekly_review takes no arguments."), None
        return scenario.weekly_review, None
    parsed = budget_arguments(arguments)
    if parsed is None:
        return (
            invalid(
                "Arguments must be an object with integer year and month and a "
                "3-letter "
                "currency code."
            ),
            None,
        )
    month, currency = parsed
    if month not in scenario.readable_months:
        return invalid(
            f"The month must be one of: {', '.join(scenario.readable_months)}."
        ), None
    budget = scenario.budget(month, currency)
    if budget is None:
        year, number = (int(part) for part in month.split("-"))
        return (
            {"year": year, "month": number, "currency": currency, "budget_set": False},
            (month, currency, None),
        )
    return budget, (month, currency, Decimal(str(budget["amount"])))


def validate_proposal(scenario, proposal, observations: dict) -> str | None:
    """RunActionAgentHandler.ProposeAsync + ProposedAction.ProposeBudgetAdjustment: the
    first failing check's error code, or None when .NET would store the Pending
    proposal."""
    month = month_label(proposal.year, proposal.month)
    if month not in scenario.context["target_months"]:
        return PROPOSAL_MONTH_NOT_ALLOWED
    currency = proposal.currency.strip().upper()
    if (month, currency) not in observations:
        return PROPOSAL_NOT_GROUNDED
    current = observations[(month, currency)]
    if current is None:
        return PROPOSAL_WITHOUT_BUDGET
    amount = Decimal(str(proposal.proposed_amount))
    if (
        amount == current
        or amount > current * MAX_CHANGE_FACTOR
        or amount * MAX_CHANGE_FACTOR < current
    ):
        return INVALID_PROPOSAL
    return None
