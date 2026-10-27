"""AI-008: the LifeOS Assistant interpretation protocol, version 2 (request, output, response).

One chat for several LifeOS modules. Version 2 keeps version 1's Finance section and rules exactly
(same fields, states and violation codes) and adds:

- two sections, `hydration` (one water intake: the quantity as stated, its unit ml/cl/l, date,
  time) and `meal` (one meal: the user's own description, an optional meal type, date, time);
- generic draft intents (`draft_update`, `draft_confirm`, `draft_cancel`) that act on whatever draft
  is open, plus `hydration_intake` and `nutrition_meal` to start one;
- the open draft at the top of the context (`context.open_draft`, with its `kind`), and the Finance
  category names in `context.finance`.

The request still carries no LifeOS identifier and no data of the new modules: no intake or meal
history, no target, no nutrition. Only the intent's section may be filled (`section_mismatch`);
Hydration and Meal violation codes carry their section's prefix. Canonical contract:
docs/contracts/assistant-interpretation/v2. Version 1 (schema.py) is still served unchanged for
callers that send it.
"""

from typing import Literal

from pydantic import BaseModel, Field

from lifeos_ai.assistant.schema import (
    _AMOUNT,
    _STRICT,
    DATE_FIELD_SCHEMA,
    FINANCE_SCHEMA,
    MAX_CATEGORY_NAMES,
    MAX_NOTE_LENGTH,
    TEXT_FIELD_SCHEMA,
    TIME_FIELD_SCHEMA,
    CategoryName,
    DateField,
    FinanceFields,
    Message,
    ProtocolViolation,
    TextField,
    TimeField,
    _date,
    _nullable,
    _object,
    _text,
    _time,
    normalize_for_match,
    normalize_spacing,
    validate_finance,
)

PROTOCOL_VERSION = 2
OUTPUT_VERSION = 2

FieldName = Literal[
    "type",
    "amount",
    "currency",
    "account",
    "category",
    "note",
    "date",
    "time",
    "unit",
    "description",
    "meal_type",
]
FIELD_NAMES: tuple[str, ...] = FieldName.__args__

Intent = Literal[
    "finance_expense",
    "finance_income",
    "hydration_intake",
    "nutrition_meal",
    "draft_update",
    "draft_confirm",
    "draft_cancel",
    "general_question",
    "clarification",
    "unsupported",
]
INTENTS: tuple[str, ...] = Intent.__args__

UnsupportedReason = Literal["transfer", "multiple_operations", "other_capability", "other"]
DraftKind = Literal[
    "finance.transaction.create", "hydration.intake.create", "nutrition.meal.create"
]

UNITS = ("ml", "cl", "l")
MEAL_TYPES = ("breakfast", "lunch", "dinner", "snack", "other")

# The section each intent that starts a record fills (the others stay absent).
_TARGET = {
    "finance_expense": "finance",
    "finance_income": "finance",
    "hydration_intake": "hydration",
    "nutrition_meal": "meal",
}


# ---- Request ----


class OpenDraftContext(BaseModel):
    """The open draft as the model needs it: its kind and which fields are filled or missing."""

    model_config = _STRICT

    kind: DraftKind
    type: Literal["expense", "income"] | None
    filled_fields: list[FieldName] = Field(max_length=len(FIELD_NAMES))
    missing_fields: list[FieldName] = Field(max_length=len(FIELD_NAMES))
    awaiting_field: FieldName | None


class FinanceContext(BaseModel):
    model_config = _STRICT

    expense_categories: list[CategoryName] = Field(max_length=MAX_CATEGORY_NAMES)
    income_categories: list[CategoryName] = Field(max_length=MAX_CATEGORY_NAMES)


class InterpretationContext(BaseModel):
    model_config = _STRICT

    locale: Literal["it-IT"]
    open_draft: OpenDraftContext | None
    finance: FinanceContext


class InterpretRequest(BaseModel):
    model_config = _STRICT

    protocol_version: Literal[2]
    message: Message
    context: InterpretationContext


# ---- Output ----


class HydrationFields(BaseModel):
    model_config = _STRICT

    amount: TextField
    unit: TextField
    date: DateField
    time: TimeField


class MealFields(BaseModel):
    model_config = _STRICT

    description: TextField
    meal_type: TextField
    date: DateField
    time: TimeField


class AssistantInterpretation(BaseModel):
    model_config = _STRICT

    protocol_version: int
    intent: Intent
    finance: FinanceFields
    hydration: HydrationFields
    meal: MealFields
    ambiguous_fields: list[str]
    unsupported_reason: UnsupportedReason | None


class InterpretResponse(BaseModel):
    model_config = _STRICT

    output_version: Literal[2]
    provider: str
    model: str
    prompt_version: str
    interpretation: AssistantInterpretation


# ---- Protocol rules beyond the shape ----


def _absent(section: BaseModel) -> bool:
    return all(getattr(section, name).state == "absent" for name in type(section).model_fields)


def validate_interpretation(interpretation: AssistantInterpretation, message: str) -> None:
    """Raises ProtocolViolation for anything the protocol forbids; never repairs.

    Rules are checked in the same order as .NET (AssistantInterpretationValidator), so both report
    the same code.
    """
    if interpretation.protocol_version != PROTOCOL_VERSION:
        raise ProtocolViolation("protocol_version")
    ambiguous = interpretation.ambiguous_fields
    if any(name not in FIELD_NAMES for name in ambiguous) or len(set(ambiguous)) != len(ambiguous):
        raise ProtocolViolation("ambiguous_fields")
    if (interpretation.intent == "unsupported") != (interpretation.unsupported_reason is not None):
        raise ProtocolViolation("unsupported_reason")

    used = {
        name
        for name, section in (
            ("finance", interpretation.finance),
            ("hydration", interpretation.hydration),
            ("meal", interpretation.meal),
        )
        if not _absent(section)
    }
    target = _TARGET.get(interpretation.intent)
    if (target is not None and not used <= {target}) or (
        interpretation.intent == "draft_update" and len(used) > 1
    ):
        raise ProtocolViolation("section_mismatch")

    validate_finance(interpretation.finance, interpretation.intent, message)

    normalized = normalize_for_match(message)
    hydration = interpretation.hydration
    _text(
        hydration.amount,
        "hydration_amount",
        normalized,
        infer=False,
        clear=False,
        fmt=_AMOUNT.fullmatch,
    )
    _text(
        hydration.unit,
        "hydration_unit",
        normalized,
        infer=False,
        clear=False,
        fmt=UNITS.__contains__,
    )
    _date(hydration.date, normalized, "hydration_date")
    _time(hydration.time, normalized, "hydration_time")

    meal = interpretation.meal
    _text(
        meal.description,
        "meal_description",
        normalized,
        infer=False,
        clear=False,
        max_length=MAX_NOTE_LENGTH,
    )
    # A stated description is the user's own words: found in the message as written, case included.
    description = meal.description
    if description.state == "stated" and normalize_spacing(
        description.value
    ) not in normalize_spacing(message):
        raise ProtocolViolation("meal_description_not_verbatim")
    _text(
        meal.meal_type,
        "meal_meal_type",
        normalized,
        infer=False,
        clear=True,
        fmt=MEAL_TYPES.__contains__,
    )
    _date(meal.date, normalized, "meal_date")
    _time(meal.time, normalized, "meal_time")


# ---- JSON schema for strict structured output ----

INTERPRETATION_JSON_SCHEMA: dict = _object(
    {
        "protocol_version": {"type": "integer", "enum": [PROTOCOL_VERSION]},
        "intent": {"type": "string", "enum": list(INTENTS)},
        "finance": FINANCE_SCHEMA,
        "hydration": _object(
            {
                "amount": TEXT_FIELD_SCHEMA,
                "unit": TEXT_FIELD_SCHEMA,
                "date": DATE_FIELD_SCHEMA,
                "time": TIME_FIELD_SCHEMA,
            }
        ),
        "meal": _object(
            {
                "description": TEXT_FIELD_SCHEMA,
                "meal_type": TEXT_FIELD_SCHEMA,
                "date": DATE_FIELD_SCHEMA,
                "time": TIME_FIELD_SCHEMA,
            }
        ),
        "ambiguous_fields": {
            "type": "array",
            "items": {"type": "string", "enum": list(FIELD_NAMES)},
        },
        "unsupported_reason": _nullable("string", enum=[*UnsupportedReason.__args__, None]),
    }
)
