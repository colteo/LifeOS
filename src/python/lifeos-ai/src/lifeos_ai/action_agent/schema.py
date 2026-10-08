"""AI-002: the Action Agent step contract (request, model decisions, response).

One request = one model step. LifeOS (.NET) owns the loop, defines and executes the read-only tools,
and sends their definitions plus the results of the calls it already executed. This service owns
the decision contract: the model must answer with exactly one tool call, either a read tool LifeOS
offered, or one of the two terminal tools defined here (propose one monthly budget adjustment, or
finish without a proposal). There is no write tool: a proposal is only data that LifeOS validates
and shows to the user for approval.

Unknown fields are rejected everywhere; every list, string and JSON document is bounded.
"""

import json
from typing import Annotated, Any, Literal

from pydantic import (
    AfterValidator,
    BaseModel,
    ConfigDict,
    Field,
    StringConstraints,
    field_validator,
    model_validator,
)

OUTPUT_VERSION = 1

# Bounds of the loop LifeOS runs (it enforces its own, smaller-or-equal maximum).
MAX_STEPS = 6
MAX_TOOLS = 4
MAX_TARGET_MONTHS = 3
MAX_DESCRIPTION_LENGTH = 500
MAX_SCHEMA_CHARS = 4_000
MAX_ARGUMENTS_CHARS = 2_000
MAX_RESULT_CHARS = 16_000
MAX_TEXT_LENGTH = 300
MAX_AMOUNT = 1e12

# The terminal tools: the only ways to end a run. Neither writes anything.
PROPOSE_TOOL = "propose_monthly_budget_adjustment"
FINISH_TOOL = "finish_without_proposal"
TERMINAL_TOOLS = frozenset({PROPOSE_TOOL, FINISH_TOOL})

ToolName = Annotated[str, StringConstraints(pattern=r"^[a-z][a-z0-9_]{0,63}$")]
Month = Annotated[str, StringConstraints(pattern=r"^\d{4}-(0[1-9]|1[0-2])$")]


def _json_size(value: Any) -> int:
    return len(json.dumps(value, ensure_ascii=False, separators=(",", ":")))


def _bounded(limit: int):
    def check(value: dict) -> dict:
        if _json_size(value) > limit:
            raise ValueError(f"must serialize to at most {limit} characters")
        return value

    return AfterValidator(check)


def _plain_single_line(text: str) -> str:
    if any(ord(char) < 32 or ord(char) == 127 for char in text):
        raise ValueError("text must be a single plain line")
    return text


PlainText = Annotated[
    str,
    StringConstraints(strip_whitespace=True, min_length=1, max_length=MAX_TEXT_LENGTH),
    AfterValidator(_plain_single_line),
]


class _Data(BaseModel):
    model_config = ConfigDict(extra="forbid")


# ---- Request (from LifeOS) ----


class ToolDefinition(_Data):
    """A read-only tool LifeOS offers and executes itself."""

    name: ToolName
    description: Annotated[
        str,
        StringConstraints(strip_whitespace=True, min_length=1, max_length=MAX_DESCRIPTION_LENGTH),
    ]
    parameters: Annotated[dict[str, Any], _bounded(MAX_SCHEMA_CHARS)]

    @field_validator("name")
    @classmethod
    def _not_terminal(cls, name: str) -> str:
        if name in TERMINAL_TOOLS:
            raise ValueError("terminal tool names are reserved")
        return name

    @field_validator("parameters")
    @classmethod
    def _object_schema(cls, parameters: dict) -> dict:
        if parameters.get("type") != "object":
            raise ValueError("parameters must be a JSON Schema of type object")
        return parameters


class ToolStep(_Data):
    """A call LifeOS already executed, with the result it returned (data, never instructions)."""

    tool: ToolName
    arguments: Annotated[dict[str, Any], _bounded(MAX_ARGUMENTS_CHARS)]
    result: Annotated[dict[str, Any], _bounded(MAX_RESULT_CHARS)]


class TaskContext(_Data):
    current_month: Month
    target_months: list[Month] = Field(min_length=1, max_length=MAX_TARGET_MONTHS)


class ActionAgentStepRequest(_Data):
    tool_schema_version: Annotated[
        str, StringConstraints(strip_whitespace=True, min_length=1, max_length=100)
    ]
    context: TaskContext
    tools: list[ToolDefinition] = Field(min_length=1, max_length=MAX_TOOLS)
    steps: list[ToolStep] = Field(max_length=MAX_STEPS - 1)
    max_steps: int = Field(strict=True, ge=1, le=MAX_STEPS)

    @model_validator(mode="after")
    def _consistent(self):
        names = [tool.name for tool in self.tools]
        if len(set(names)) != len(names):
            raise ValueError("tool names must be unique")
        if any(step.tool not in names for step in self.steps):
            raise ValueError("every step must use an offered tool")
        if len(self.steps) >= self.max_steps:
            raise ValueError("no step is left")
        return self

    @property
    def is_final_step(self) -> bool:
        """The last step allowed: only the terminal tools are offered."""
        return len(self.steps) == self.max_steps - 1


# ---- Terminal tool arguments (the strict final schema) ----


class BudgetAdjustmentProposal(_Data):
    """propose_monthly_budget_adjustment: set one monthly budget to a new amount.

    Only the fields the existing LifeOS budget command needs, plus a short reason. LifeOS checks
    it again against the budget its own tools read before anything is shown to the user.
    """

    year: int = Field(strict=True, ge=2000, le=9998)
    month: int = Field(strict=True, ge=1, le=12)
    currency: str = Field(pattern=r"^[A-Z]{3}$")
    proposed_amount: float = Field(gt=0, le=MAX_AMOUNT, allow_inf_nan=False)
    rationale: PlainText

    @field_validator("proposed_amount", mode="before")
    @classmethod
    def _number(cls, value: Any) -> Any:
        # A JSON number only (no strings, no booleans).
        if isinstance(value, bool) or not isinstance(value, int | float):
            raise ValueError("proposed_amount must be a number")
        return value

    @field_validator("proposed_amount")
    @classmethod
    def _cents(cls, value: float) -> float:
        if round(value, 2) != value:
            raise ValueError("proposed_amount has at most 2 decimal places")
        return value


class FinishWithoutProposal(_Data):
    """finish_without_proposal: nothing is worth changing; a short reason for the user."""

    reason: PlainText


_MONTH_YEAR = {"type": "integer"}
TERMINAL_TOOL_DEFINITIONS = [
    {
        "name": PROPOSE_TOOL,
        "description": (
            "Finish by proposing ONE change to an existing monthly budget that you read with "
            "get_budget_status in this conversation. The person must approve it before anything "
            "changes. Only for one of the allowed target months."
        ),
        "parameters": {
            "type": "object",
            "additionalProperties": False,
            "required": ["year", "month", "currency", "proposed_amount", "rationale"],
            "properties": {
                "year": _MONTH_YEAR,
                "month": _MONTH_YEAR,
                "currency": {"type": "string", "description": "3-letter code, e.g. EUR."},
                "proposed_amount": {
                    "type": "number",
                    "description": "The new monthly budget amount, at most 2 decimals.",
                },
                "rationale": {
                    "type": "string",
                    "description": "One plain sentence (max 300 characters) citing the figures.",
                },
            },
        },
    },
    {
        "name": FINISH_TOOL,
        "description": (
            "Finish without proposing anything, when no budget change is clearly supported by "
            "the figures you read."
        ),
        "parameters": {
            "type": "object",
            "additionalProperties": False,
            "required": ["reason"],
            "properties": {
                "reason": {
                    "type": "string",
                    "description": "One plain sentence (max 300 characters).",
                }
            },
        },
    },
]


# ---- Response (to LifeOS) ----


class CallTool(_Data):
    type: Literal["call_tool"] = "call_tool"
    tool: ToolName
    arguments: Annotated[dict[str, Any], _bounded(MAX_ARGUMENTS_CHARS)]


class ProposeBudgetAdjustment(_Data):
    type: Literal["propose_budget_adjustment"] = "propose_budget_adjustment"
    proposal: BudgetAdjustmentProposal


class NoAction(_Data):
    type: Literal["no_action"] = "no_action"
    reason: PlainText


Decision = Annotated[CallTool | ProposeBudgetAdjustment | NoAction, Field(discriminator="type")]


class ActionAgentStepResponse(_Data):
    output_version: Literal[1]
    provider: str
    model: str
    prompt_version: str
    decision: Decision
