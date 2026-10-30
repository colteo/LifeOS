"""AI-009 I1: the Quick Capture specialist contracts (docs/contracts/quick-capture/{finance,
hydration,meal}/v1).

A specialist interprets ONE segment chosen by the router as one record of its module, to create a
draft or to correct one. Its output section is the v2 section of that module, unchanged (same
fields, states, formats and violation codes); what is new is the outcome, Finance's
`transaction_type` and the authorised text: spans are checked against the segment and each of its
context spans, never against the whole message, which a specialist never receives.

A specialist does nothing else: it does not resolve accounts or categories, compute dates or
quantities, estimate nutrients or invent food; .NET validates the output again and applies it.
"""

from typing import Annotated, Literal

from pydantic import AfterValidator, BaseModel, Field, model_validator

from lifeos_ai.assistant import schema_v2
from lifeos_ai.assistant.schema import (
    _AMOUNT,
    _CURRENCY,
    _STRICT,
    FINANCE_SCHEMA,
    MAX_NOTE_LENGTH,
    FinanceFields,
    Message,
    ProtocolViolation,
    TextField,
    _date,
    _mention,
    _nullable,
    _object,
    _text,
    _time,
    normalize_spacing,
)
from lifeos_ai.assistant.schema_v2 import MEAL_TYPES, UNITS, HydrationFields, MealFields
from lifeos_ai.quick_capture.common import (
    MAX_CONTEXT_SPANS,
    PROTOCOL_VERSION,
    AuthorisedText,
    ContextSpan,
    FinanceFieldName,
    HydrationFieldName,
    MealFieldName,
    unique,
)

Mode = Literal["create", "correct"]
BaseOutcome = Literal["interpreted", "unclear", "wrong_domain", "multiple_operations"]
FinanceOutcome = Literal[
    "interpreted", "unclear", "wrong_domain", "multiple_operations", "transfer"
]
TransactionType = Literal["expense", "income"]


# ---- Requests ----


def _names(name_type) -> type:
    return Annotated[
        list[name_type], Field(max_length=len(name_type.__args__)), AfterValidator(unique)
    ]


class FinanceDraft(BaseModel):
    """The target draft of a correction: its type and the NAMES of its filled and missing fields.
    Never a value, never an identifier."""

    model_config = _STRICT

    type: TransactionType | None
    filled_fields: _names(FinanceFieldName)
    missing_fields: _names(FinanceFieldName)


class HydrationDraft(BaseModel):
    model_config = _STRICT

    filled_fields: _names(HydrationFieldName)
    missing_fields: _names(HydrationFieldName)


class MealDraft(BaseModel):
    model_config = _STRICT

    filled_fields: _names(MealFieldName)
    missing_fields: _names(MealFieldName)


class FinanceSpecialistContext(BaseModel):
    model_config = _STRICT

    locale: Literal["it-IT"]
    draft: FinanceDraft | None
    finance: schema_v2.FinanceContext


class HydrationSpecialistContext(BaseModel):
    model_config = _STRICT

    locale: Literal["it-IT"]
    draft: HydrationDraft | None


class MealSpecialistContext(BaseModel):
    model_config = _STRICT

    locale: Literal["it-IT"]
    draft: MealDraft | None


class _SpecialistRequest(BaseModel):
    model_config = _STRICT

    protocol_version: Literal[1]
    mode: Mode
    segment: Message
    context_spans: list[ContextSpan] = Field(max_length=MAX_CONTEXT_SPANS)

    @model_validator(mode="after")
    def _mode_matches_draft(self):
        # The contract's if/then: no draft to create one, the target draft to correct it.
        draft = self.context.draft
        if self.mode == "create" and draft is not None:
            raise ValueError("draft_on_create")
        if self.mode == "correct" and draft is None:
            raise ValueError("draft_missing_on_correct")
        return self


class FinanceInterpretRequest(_SpecialistRequest):
    context: FinanceSpecialistContext


class HydrationInterpretRequest(_SpecialistRequest):
    context: HydrationSpecialistContext


class MealInterpretRequest(_SpecialistRequest):
    context: MealSpecialistContext


# ---- Outputs ----
#
# The version and the ambiguous field names are protocol rules, not shape, as in v1/v2.


class FinanceInterpretation(BaseModel):
    model_config = _STRICT

    protocol_version: int
    outcome: FinanceOutcome
    transaction_type: TransactionType | None
    finance: FinanceFields
    ambiguous_fields: list[str]


class HydrationInterpretation(BaseModel):
    model_config = _STRICT

    protocol_version: int
    outcome: BaseOutcome
    hydration: HydrationFields
    ambiguous_fields: list[str]


class MealInterpretation(BaseModel):
    model_config = _STRICT

    protocol_version: int
    outcome: BaseOutcome
    meal: MealFields
    ambiguous_fields: list[str]


class FinanceInterpretResponse(BaseModel):
    model_config = _STRICT

    output_version: Literal[1]
    provider: str
    model: str
    prompt_version: str
    interpretation: FinanceInterpretation


class HydrationInterpretResponse(BaseModel):
    model_config = _STRICT

    output_version: Literal[1]
    provider: str
    model: str
    prompt_version: str
    interpretation: HydrationInterpretation


class MealInterpretResponse(BaseModel):
    model_config = _STRICT

    output_version: Literal[1]
    provider: str
    model: str
    prompt_version: str
    interpretation: MealInterpretation


SpecialistRequest = FinanceInterpretRequest | HydrationInterpretRequest | MealInterpretRequest
SpecialistInterpretation = FinanceInterpretation | HydrationInterpretation | MealInterpretation


# ---- Protocol rules beyond the shape ----


def _section(interpretation: SpecialistInterpretation) -> tuple[BaseModel, tuple[str, ...]]:
    if isinstance(interpretation, FinanceInterpretation):
        return interpretation.finance, FinanceFieldName.__args__
    if isinstance(interpretation, HydrationInterpretation):
        return interpretation.hydration, HydrationFieldName.__args__
    return interpretation.meal, MealFieldName.__args__


def validate_specialist(
    interpretation: SpecialistInterpretation,
    mode: str,
    segment: str,
    context_spans: list[str],
) -> None:
    """Raises ProtocolViolation (the contract's code) for the first broken rule; never repairs.

    Rules in the contracts' order: version, ambiguous names, outcome, (Finance) transaction type,
    no clear on create, then the v2 field rules of the section with "message" read as the
    authorised text (the segment and each context span).
    """
    if interpretation.protocol_version != PROTOCOL_VERSION:
        raise ProtocolViolation("protocol_version")
    section, names = _section(interpretation)
    ambiguous = interpretation.ambiguous_fields
    if any(name not in names for name in ambiguous) or len(set(ambiguous)) != len(ambiguous):
        raise ProtocolViolation("ambiguous_fields")
    fields = [getattr(section, name) for name in names]
    finance = isinstance(interpretation, FinanceInterpretation)
    if interpretation.outcome != "interpreted":
        if (
            any(field.state != "absent" for field in fields)
            or ambiguous
            or (finance and interpretation.transaction_type is not None)
        ):
            raise ProtocolViolation("outcome_fields")
        return
    if finance:
        if (mode == "create") != (interpretation.transaction_type is not None):
            raise ProtocolViolation("transaction_type_mode")
        stated_type = section.type.value if section.type.state == "stated" else None
        if (interpretation.transaction_type, stated_type) in {
            ("expense", "income"),
            ("income", "expense"),
        }:
            raise ProtocolViolation("type_contradicts_intent")
    if mode == "create" and any(field.state == "clear" for field in fields):
        raise ProtocolViolation("clear_on_create")

    authorised = AuthorisedText(segment, context_spans)
    if finance:
        _finance(section, authorised, segment)
    elif isinstance(interpretation, HydrationInterpretation):
        _hydration(section, authorised)
    else:
        _meal(section, authorised, segment)


def _verbatim(field: TextField, code: str, segment: str) -> None:
    # The user's own words: found in the segment as written, case included.
    if field.state == "stated" and normalize_spacing(field.value) not in normalize_spacing(segment):
        raise ProtocolViolation(code)


def _finance(finance: FinanceFields, authorised: AuthorisedText, segment: str) -> None:
    """The v2 Finance field rules, in v2 order (schema.validate_finance)."""
    _text(
        finance.type,
        "type",
        authorised,
        infer=False,
        clear=False,
        fmt=("expense", "income").__contains__,
    )
    _text(finance.amount, "amount", authorised, infer=False, clear=False, fmt=_AMOUNT.fullmatch)
    _text(
        finance.currency, "currency", authorised, infer=False, clear=True, fmt=_CURRENCY.fullmatch
    )
    _mention(finance.account, "account", authorised, infer=False)
    _mention(finance.category, "category", authorised, infer=True)
    _text(finance.note, "note", authorised, infer=True, clear=True, max_length=MAX_NOTE_LENGTH)
    _verbatim(finance.note, "note_not_verbatim", segment)
    _date(finance.date, authorised)
    _time(finance.time, authorised)


def _hydration(hydration: HydrationFields, authorised: AuthorisedText) -> None:
    """The v2 Hydration field rules, in v2 order (schema_v2.validate_interpretation)."""
    _text(
        hydration.amount,
        "hydration_amount",
        authorised,
        infer=False,
        clear=False,
        fmt=_AMOUNT.fullmatch,
    )
    _text(
        hydration.unit,
        "hydration_unit",
        authorised,
        infer=False,
        clear=False,
        fmt=UNITS.__contains__,
    )
    _date(hydration.date, authorised, "hydration_date")
    _time(hydration.time, authorised, "hydration_time")


def _meal(meal: MealFields, authorised: AuthorisedText, segment: str) -> None:
    """The v2 Meal field rules, in v2 order (schema_v2.validate_interpretation)."""
    _text(
        meal.description,
        "meal_description",
        authorised,
        infer=False,
        clear=False,
        max_length=MAX_NOTE_LENGTH,
    )
    _verbatim(meal.description, "meal_description_not_verbatim", segment)
    _text(
        meal.meal_type,
        "meal_meal_type",
        authorised,
        infer=False,
        clear=True,
        fmt=MEAL_TYPES.__contains__,
    )
    _date(meal.date, authorised, "meal_date")
    _time(meal.time, authorised, "meal_time")


# ---- JSON schemas for strict structured output ----
#
# The sections are v2's schemas (the same objects); each whole schema equals its contract's
# interpretation.schema.json (tests/test_quick_capture_contract.py).

_V2_SECTIONS = schema_v2.INTERPRETATION_JSON_SCHEMA["properties"]
_VERSION = {"type": "integer", "enum": [PROTOCOL_VERSION]}


def _ambiguous(names: tuple[str, ...]) -> dict:
    return {"type": "array", "items": {"type": "string", "enum": list(names)}}


FINANCE_JSON_SCHEMA: dict = _object(
    {
        "protocol_version": _VERSION,
        "outcome": {"type": "string", "enum": list(FinanceOutcome.__args__)},
        "transaction_type": _nullable("string", enum=[*TransactionType.__args__, None]),
        "finance": FINANCE_SCHEMA,
        "ambiguous_fields": _ambiguous(FinanceFieldName.__args__),
    }
)
HYDRATION_JSON_SCHEMA: dict = _object(
    {
        "protocol_version": _VERSION,
        "outcome": {"type": "string", "enum": list(BaseOutcome.__args__)},
        "hydration": _V2_SECTIONS["hydration"],
        "ambiguous_fields": _ambiguous(HydrationFieldName.__args__),
    }
)
MEAL_JSON_SCHEMA: dict = _object(
    {
        "protocol_version": _VERSION,
        "outcome": {"type": "string", "enum": list(BaseOutcome.__args__)},
        "meal": _V2_SECTIONS["meal"],
        "ambiguous_fields": _ambiguous(MealFieldName.__args__),
    }
)
