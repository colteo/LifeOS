"""AI-007 F3: the LifeOS Assistant interpretation protocol, version 1 (request, output, response).

The model has one job: read the user's message and return this structure. LifeOS (.NET) decides
everything else: it validates the output again, resolves mentions against the user's own data,
computes dates in the user's time zone and applies the result to a draft that only the user can
confirm.

Every field has a state: `absent` (not in the message), `stated` (said explicitly; `span` is the
verbatim text it comes from and must occur in the message), `inferred` (a proposal of the model,
allowed only for the category and the note) or `clear` (the user asked to remove it). There is no
confidence score; ambiguous fields are listed in `ambiguous_fields` and asked again.

The request carries no LifeOS identifier, no account list and no dates: the message, the locale, the
open draft's filled/missing field names and the user's category names. Unknown fields are rejected
both ways, every field must be present and types are strict (no "5" for 5). The same rules are
enforced by .NET (`AssistantInterpretationValidator`); both sides are tested against the canonical
contract in docs/contracts/assistant-interpretation/v1 (schema, limits, error codes, golden cases).
"""

import re
from typing import Annotated, Literal

from pydantic import AfterValidator, BaseModel, ConfigDict, Field, StringConstraints

PROTOCOL_VERSION = 1
OUTPUT_VERSION = 1

MAX_MESSAGE_LENGTH = 1000
MAX_VALUE_LENGTH = 200
MAX_NOTE_LENGTH = 1000
MAX_SPAN_LENGTH = 1000
MAX_CATEGORY_NAMES = 100
MAX_CATEGORY_NAME_LENGTH = 200
MAX_DAYS_AGO = 366

# Every model of the protocol: unknown fields refused, no type coercion.
_STRICT = ConfigDict(extra="forbid", strict=True)

FieldName = Literal["type", "amount", "currency", "account", "category", "note", "date", "time"]
FIELD_NAMES: tuple[str, ...] = FieldName.__args__

Intent = Literal[
    "finance_expense",
    "finance_income",
    "finance_draft_update",
    "finance_draft_confirm",
    "finance_draft_cancel",
    "general_question",
    "clarification",
    "unsupported",
]
INTENTS: tuple[str, ...] = Intent.__args__
DRAFT_CHANGING_INTENTS = frozenset({"finance_expense", "finance_income", "finance_draft_update"})

UnsupportedReason = Literal["transfer", "multiple_transactions", "other_capability", "other"]
FieldState = Literal["absent", "stated", "inferred", "clear"]
DateKind = Literal["today", "yesterday", "day_before_yesterday", "days_ago", "weekday", "calendar"]
Weekday = Literal["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"]


def _no_nul(text: str) -> str:
    if "\x00" in text:
        raise ValueError("text must not contain NUL")
    return text


Message = Annotated[
    str,
    StringConstraints(strip_whitespace=True, min_length=1, max_length=MAX_MESSAGE_LENGTH),
    AfterValidator(_no_nul),
]
CategoryName = Annotated[
    str,
    StringConstraints(strip_whitespace=True, min_length=1, max_length=MAX_CATEGORY_NAME_LENGTH),
    AfterValidator(_no_nul),
]


# ---- Request ----


class OpenDraftContext(BaseModel):
    """The open draft as the model needs it: which fields are filled or missing, never values."""

    model_config = _STRICT

    kind: Literal["finance.transaction.create"]
    type: Literal["expense", "income"] | None
    filled_fields: list[FieldName] = Field(max_length=len(FIELD_NAMES))
    missing_fields: list[FieldName] = Field(max_length=len(FIELD_NAMES))
    awaiting_field: FieldName | None


class FinanceContext(BaseModel):
    model_config = _STRICT

    open_draft: OpenDraftContext | None
    expense_categories: list[CategoryName] = Field(max_length=MAX_CATEGORY_NAMES)
    income_categories: list[CategoryName] = Field(max_length=MAX_CATEGORY_NAMES)


class InterpretationContext(BaseModel):
    model_config = _STRICT

    locale: Literal["it-IT"]
    finance: FinanceContext


class InterpretRequest(BaseModel):
    model_config = _STRICT

    protocol_version: Literal[1]
    message: Message
    context: InterpretationContext


# ---- Output (what the model must return) ----


class TextField(BaseModel):
    model_config = _STRICT

    state: FieldState
    value: str | None
    span: str | None


class DateField(BaseModel):
    model_config = _STRICT

    state: FieldState
    kind: DateKind | None
    days_ago: int | None
    weekday: Weekday | None
    day: int | None
    month: int | None
    year: int | None
    span: str | None


class TimeField(BaseModel):
    model_config = _STRICT

    state: FieldState
    hour: int | None
    minute: int | None
    span: str | None


class FinanceFields(BaseModel):
    model_config = _STRICT

    type: TextField
    amount: TextField
    currency: TextField
    account: TextField
    category: TextField
    note: TextField
    date: DateField
    time: TimeField


class AssistantInterpretation(BaseModel):
    model_config = _STRICT

    # The version and the ambiguous field names are protocol rules (validate_interpretation), not
    # shape, so Python and .NET report the same violation code for them.
    protocol_version: int
    intent: Intent
    finance: FinanceFields
    ambiguous_fields: list[str]
    unsupported_reason: UnsupportedReason | None


class InterpretResponse(BaseModel):
    """The service's answer: the interpretation plus the identity needed to attribute it."""

    model_config = _STRICT

    output_version: Literal[1]
    provider: str
    model: str
    prompt_version: str
    interpretation: AssistantInterpretation


# ---- Protocol rules beyond the shape ----

# ASCII digits only (`\d` would also accept other scripts' digits, which .NET refuses).
_AMOUNT = re.compile(r"[0-9]{1,15}(\.[0-9]{1,4})?")
_CURRENCY = re.compile(r"[A-Z]{3}")
_SPACES = re.compile(r"\s+")


class ProtocolViolation(ValueError):
    """The interpretation is well-formed but breaks a protocol rule. `code` names the rule only."""

    def __init__(self, code: str):
        super().__init__(code)
        self.code = code


def normalize_spacing(text: str) -> str:
    """Typographic apostrophes as ', whitespace runs as one space, case kept."""
    text = text.strip().replace("’", "'").replace("‘", "'").replace("`", "'")
    return _SPACES.sub(" ", text)


def normalize_for_match(text: str) -> str:
    """Lowercase, typographic apostrophes as ', whitespace runs as one space."""
    return normalize_spacing(text).lower()


def validate_interpretation(interpretation: AssistantInterpretation, message: str) -> None:
    """Raises ProtocolViolation for anything the protocol forbids; never repairs.

    Rules are checked in the same order as .NET, so both report the same code.
    """
    if interpretation.protocol_version != PROTOCOL_VERSION:
        raise ProtocolViolation("protocol_version")
    ambiguous = interpretation.ambiguous_fields
    if any(name not in FIELD_NAMES for name in ambiguous) or len(set(ambiguous)) != len(ambiguous):
        raise ProtocolViolation("ambiguous_fields")
    if (interpretation.intent == "unsupported") != (interpretation.unsupported_reason is not None):
        raise ProtocolViolation("unsupported_reason")
    validate_finance(interpretation.finance, interpretation.intent, message)


def validate_finance(finance: FinanceFields, intent: str, message: str) -> None:
    """The Finance section's rules (protocol v1, unchanged in v2)."""
    normalized = normalize_for_match(message)

    _text(
        finance.type,
        "type",
        normalized,
        infer=False,
        clear=False,
        fmt=("expense", "income").__contains__,
    )
    if (intent, finance.type.value) in {
        ("finance_expense", "income"),
        ("finance_income", "expense"),
    }:
        raise ProtocolViolation("type_contradicts_intent")
    _text(finance.amount, "amount", normalized, infer=False, clear=False, fmt=_AMOUNT.fullmatch)
    _text(
        finance.currency, "currency", normalized, infer=False, clear=True, fmt=_CURRENCY.fullmatch
    )
    _mention(finance.account, "account", normalized, infer=False)
    _mention(finance.category, "category", normalized, infer=True)
    _text(finance.note, "note", normalized, infer=True, clear=True, max_length=MAX_NOTE_LENGTH)
    # A stated note is the user's own words: found in the message as written, case included.
    note = finance.note
    if note.state == "stated" and normalize_spacing(note.value) not in normalize_spacing(message):
        raise ProtocolViolation("note_not_verbatim")
    _date(finance.date, normalized)
    _time(finance.time, normalized)


def _text(field, name, message, *, infer, clear, fmt=None, max_length=MAX_VALUE_LENGTH):
    if field.state in ("absent", "clear"):
        if field.state == "clear" and not clear:
            raise ProtocolViolation(f"{name}_clear")
        if field.value is not None or field.span is not None:
            raise ProtocolViolation(f"{name}_unexpected_value")
        return
    if field.state == "inferred" and not infer:
        raise ProtocolViolation(f"{name}_inferred")
    value = field.value
    if value is None or not value.strip() or len(value) > max_length or "\x00" in value:
        raise ProtocolViolation(f"{name}_value")
    if fmt is not None and not fmt(value):
        raise ProtocolViolation(f"{name}_format")
    if field.state == "stated":
        _span(field.span, name, message)


def _mention(field, name, message, *, infer):
    """An account or a category: a stated mention is part of what its span quotes."""
    _text(field, name, message, infer=infer, clear=True)
    if field.state == "stated" and normalize_for_match(field.value) not in normalize_for_match(
        field.span
    ):
        raise ProtocolViolation(f"{name}_value_not_in_span")


def _date(field: DateField, message: str, name: str = "date") -> None:
    parts = (field.kind, field.days_ago, field.weekday, field.day, field.month, field.year)
    if field.state in ("absent", "clear"):
        if any(part is not None for part in parts) or field.span is not None:
            raise ProtocolViolation(f"{name}_unexpected_value")
        return
    if field.state == "inferred":
        raise ProtocolViolation(f"{name}_inferred")
    if field.kind is None:
        raise ProtocolViolation(f"{name}_kind")
    others_empty = {
        "days_ago": field.days_ago is None,
        "weekday": field.weekday is None,
        "calendar": field.day is None and field.month is None and field.year is None,
    }
    if field.kind == "days_ago":
        consistent = (
            field.days_ago is not None
            and 1 <= field.days_ago <= MAX_DAYS_AGO
            and others_empty["weekday"]
            and others_empty["calendar"]
        )
    elif field.kind == "weekday":
        consistent = (
            field.weekday is not None and others_empty["days_ago"] and others_empty["calendar"]
        )
    elif field.kind == "calendar":
        consistent = (
            field.day is not None
            and 1 <= field.day <= 31
            and field.month is not None
            and 1 <= field.month <= 12
            and (field.year is None or 1900 <= field.year <= 2999)
            and others_empty["days_ago"]
            and others_empty["weekday"]
        )
    else:
        consistent = all(others_empty.values())
    if not consistent:
        raise ProtocolViolation(f"{name}_parts")
    _span(field.span, name, message)


def _time(field: TimeField, message: str, name: str = "time") -> None:
    if field.state in ("absent", "clear"):
        if field.hour is not None or field.minute is not None or field.span is not None:
            raise ProtocolViolation(f"{name}_unexpected_value")
        return
    if field.state == "inferred":
        raise ProtocolViolation(f"{name}_inferred")
    if field.hour is None or not 0 <= field.hour <= 23 or field.minute is None:
        raise ProtocolViolation(f"{name}_parts")
    if not 0 <= field.minute <= 59:
        raise ProtocolViolation(f"{name}_parts")
    _span(field.span, name, message)


def _span(span: str | None, name: str, message: str) -> None:
    if span is None or not span.strip() or len(span) > MAX_SPAN_LENGTH:
        raise ProtocolViolation(f"{name}_span")
    if normalize_for_match(span) not in message:
        raise ProtocolViolation(f"{name}_span_not_in_message")


# ---- JSON schema for strict structured output (provider adapters, F3-B) ----
#
# Written out explicitly (not generated) so it satisfies strict structured-output rules: every
# object lists all its properties as required and forbids additional ones; optional values are
# nullable instead of omitted. tests/test_assistant_schema.py keeps it in step with the models.


def _nullable(json_type: str, **extra) -> dict:
    return {"type": [json_type, "null"], **extra}


def _object(properties: dict) -> dict:
    return {
        "type": "object",
        "properties": properties,
        "required": list(properties),
        "additionalProperties": False,
    }


_STATE = {"type": "string", "enum": list(FieldState.__args__)}
_TEXT_FIELD = _object({"state": _STATE, "value": _nullable("string"), "span": _nullable("string")})
DATE_FIELD_SCHEMA = _object(
    {
        "state": _STATE,
        "kind": _nullable("string", enum=[*DateKind.__args__, None]),
        "days_ago": _nullable("integer"),
        "weekday": _nullable("string", enum=[*Weekday.__args__, None]),
        "day": _nullable("integer"),
        "month": _nullable("integer"),
        "year": _nullable("integer"),
        "span": _nullable("string"),
    }
)
TIME_FIELD_SCHEMA = _object(
    {
        "state": _STATE,
        "hour": _nullable("integer"),
        "minute": _nullable("integer"),
        "span": _nullable("string"),
    }
)
TEXT_FIELD_SCHEMA = _TEXT_FIELD
FINANCE_SCHEMA = _object(
    {
        "type": _TEXT_FIELD,
        "amount": _TEXT_FIELD,
        "currency": _TEXT_FIELD,
        "account": _TEXT_FIELD,
        "category": _TEXT_FIELD,
        "note": _TEXT_FIELD,
        "date": DATE_FIELD_SCHEMA,
        "time": TIME_FIELD_SCHEMA,
    }
)

INTERPRETATION_JSON_SCHEMA: dict = _object(
    {
        "protocol_version": {"type": "integer", "enum": [PROTOCOL_VERSION]},
        "intent": {"type": "string", "enum": list(INTENTS)},
        "finance": FINANCE_SCHEMA,
        "ambiguous_fields": {
            "type": "array",
            "items": {"type": "string", "enum": list(FIELD_NAMES)},
        },
        "unsupported_reason": _nullable("string", enum=[*UnsupportedReason.__args__, None]),
    }
)
