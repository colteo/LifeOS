"""AI-009 I1: the Quick Capture router contract (docs/contracts/quick-capture/router/v1).

The router reads ONE message and lists its segments: per operation, the module, the action and
the verbatim words that belong to it. It extracts no field value (the specialists do), sees no
draft value or identifier and chooses no endpoint: .NET maps module + action through a fixed
table. An output that breaks a rule is refused whole, never repaired or partially used.
"""

from typing import Annotated, Literal

from pydantic import AfterValidator, BaseModel, Field, model_validator

from lifeos_ai.assistant.schema import (
    _STRICT,
    MAX_SPAN_LENGTH,
    Message,
    ProtocolViolation,
    _nullable,
    _object,
    normalize_for_match,
)
from lifeos_ai.quick_capture.common import (
    MAX_CONTEXT_SPAN_LENGTH,
    MAX_CONTEXT_SPANS,
    MAX_OPEN_DRAFT_KINDS,
    MAX_OPEN_DRAFTS,
    MAX_REFERENCE_SPAN_LENGTH,
    MAX_SEGMENTS,
    PROTOCOL_VERSION,
    FinanceFieldName,
    HydrationFieldName,
    MealFieldName,
    unique,
)

Module = Literal["finance", "hydration", "meal"]
Action = Literal["create", "correct", "cancel", "confirm_request", "out_of_scope", "unclear"]
OutOfScopeReason = Literal[
    "analytics", "general_question", "other_capability", "transfer", "relative_correction", "other"
]

# The actions that need a module, may carry context spans, may carry a reference span.
_MODULE_ACTIONS = frozenset({"create", "correct"})
_CONTEXT_ACTIONS = frozenset({"create", "correct"})
_REFERENCE_ACTIONS = frozenset({"correct", "cancel", "confirm_request"})


# ---- Request ----

_COUNT = Field(ge=1, le=MAX_OPEN_DRAFTS)


class FinanceOpenDrafts(BaseModel):
    """The open drafts of one kind: how many, and the union of their missing field names. No
    identifier, no value, no order."""

    model_config = _STRICT

    kind: Literal["finance.transaction.create"]
    count: int = _COUNT
    missing_fields: Annotated[
        list[FinanceFieldName],
        Field(max_length=len(FinanceFieldName.__args__)),
        AfterValidator(unique),
    ]


class HydrationOpenDrafts(BaseModel):
    model_config = _STRICT

    kind: Literal["hydration.intake.create"]
    count: int = _COUNT
    missing_fields: Annotated[
        list[HydrationFieldName],
        Field(max_length=len(HydrationFieldName.__args__)),
        AfterValidator(unique),
    ]


class MealOpenDrafts(BaseModel):
    model_config = _STRICT

    kind: Literal["nutrition.meal.create"]
    count: int = _COUNT
    missing_fields: Annotated[
        list[MealFieldName],
        Field(max_length=len(MealFieldName.__args__)),
        AfterValidator(unique),
    ]


OpenDrafts = Annotated[
    FinanceOpenDrafts | HydrationOpenDrafts | MealOpenDrafts, Field(discriminator="kind")
]


class FinanceAwaiting(BaseModel):
    """The one question the last reply asked about one draft."""

    model_config = _STRICT

    kind: Literal["finance.transaction.create"]
    field: FinanceFieldName


class HydrationAwaiting(BaseModel):
    model_config = _STRICT

    kind: Literal["hydration.intake.create"]
    field: HydrationFieldName


class MealAwaiting(BaseModel):
    model_config = _STRICT

    kind: Literal["nutrition.meal.create"]
    field: MealFieldName


Awaiting = Annotated[
    FinanceAwaiting | HydrationAwaiting | MealAwaiting, Field(discriminator="kind")
]


class RouteContext(BaseModel):
    model_config = _STRICT

    locale: Literal["it-IT"]
    max_segments: int = Field(ge=1, le=MAX_SEGMENTS)
    open_drafts: list[OpenDrafts] = Field(max_length=MAX_OPEN_DRAFT_KINDS)
    awaiting: Awaiting | None

    @model_validator(mode="after")
    def _request_rules(self) -> "RouteContext":
        # The contract's request rules beyond the schema, in its order (HTTP 422).
        kinds = [entry.kind for entry in self.open_drafts]
        if len(set(kinds)) != len(kinds):
            raise ValueError("open_drafts_kind_unique")
        if sum(entry.count for entry in self.open_drafts) > MAX_OPEN_DRAFTS:
            raise ValueError("open_drafts_total")
        if self.awaiting is not None:
            entry = next((e for e in self.open_drafts if e.kind == self.awaiting.kind), None)
            if entry is None:
                raise ValueError("awaiting_not_open")
            if self.awaiting.field not in entry.missing_fields:
                raise ValueError("awaiting_not_missing")
        return self


class RouteRequest(BaseModel):
    model_config = _STRICT

    protocol_version: Literal[1]
    message: Message
    context: RouteContext


# ---- Output ----


class Segment(BaseModel):
    model_config = _STRICT

    module: Module | None
    action: Action
    span: str
    context_spans: list[str]
    reference_span: str | None
    out_of_scope_reason: OutOfScopeReason | None


class RouterOutput(BaseModel):
    model_config = _STRICT

    # The version is a protocol rule (validate_route), not shape, as in the Assistant protocols.
    protocol_version: int
    segments: list[Segment]
    truncated: bool


class RouteResponse(BaseModel):
    """The service's answer: the router output plus the identity needed to attribute it."""

    model_config = _STRICT

    output_version: Literal[1]
    provider: str
    model: str
    prompt_version: str
    interpretation: RouterOutput


# ---- Protocol rules beyond the shape ----


def validate_route(output: RouterOutput, message: str, max_segments: int) -> None:
    """Raises ProtocolViolation (the contract's code) for the first broken rule; never repairs."""
    if output.protocol_version != PROTOCOL_VERSION:
        raise ProtocolViolation("protocol_version")
    if len(output.segments) > max_segments:
        raise ProtocolViolation("segments_count")
    if output.truncated and len(output.segments) != max_segments:
        raise ProtocolViolation("truncated_count")
    normalized = normalize_for_match(message)
    cursor = 0
    for segment in output.segments:
        cursor = _segment(segment, normalized, cursor)


def _segment(segment: Segment, message: str, cursor: int) -> int:
    """The rules of one segment; returns where the next segment's span may start."""
    if segment.action in _MODULE_ACTIONS and segment.module is None:
        raise ProtocolViolation("module_required")
    if (segment.action == "out_of_scope") != (segment.out_of_scope_reason is not None):
        raise ProtocolViolation("out_of_scope_reason")

    span = segment.span
    if not span.strip() or len(span) > MAX_SPAN_LENGTH:
        raise ProtocolViolation("segment_span")
    matched = normalize_for_match(span)
    if matched not in message:
        raise ProtocolViolation("segment_span_not_in_message")
    # In message order and without overlap: a repeated phrase must appear twice.
    at = message.find(matched, cursor)
    if at < 0:
        raise ProtocolViolation("segment_order")

    reference = segment.reference_span
    if reference is not None:
        if segment.action not in _REFERENCE_ACTIONS:
            raise ProtocolViolation("reference_span_unexpected")
        if not reference.strip() or len(reference) > MAX_REFERENCE_SPAN_LENGTH:
            raise ProtocolViolation("reference_span")
        if normalize_for_match(reference) not in matched:
            raise ProtocolViolation("reference_span_not_in_segment")

    spans = segment.context_spans
    if spans and segment.action not in _CONTEXT_ACTIONS:
        raise ProtocolViolation("context_spans_unexpected")
    if len(spans) > MAX_CONTEXT_SPANS:
        raise ProtocolViolation("context_spans_count")
    seen: set[str] = set()
    for context_span in spans:
        if not context_span.strip() or len(context_span) > MAX_CONTEXT_SPAN_LENGTH:
            raise ProtocolViolation("context_span")
        form = normalize_for_match(context_span)
        if form not in message:
            raise ProtocolViolation("context_span_not_in_message")
        if form in seen:
            raise ProtocolViolation("context_span_duplicate")
        seen.add(form)
    return at + len(matched)


# ---- JSON schema for strict structured output ----
#
# Written out explicitly in the documented Structured Outputs subset; equal to the contract's
# interpretation.schema.json (tests/test_quick_capture_contract.py).

ROUTER_JSON_SCHEMA: dict = _object(
    {
        "protocol_version": {"type": "integer", "enum": [PROTOCOL_VERSION]},
        "segments": {
            "type": "array",
            "items": _object(
                {
                    "module": _nullable("string", enum=[*Module.__args__, None]),
                    "action": {"type": "string", "enum": list(Action.__args__)},
                    "span": {"type": "string"},
                    "context_spans": {"type": "array", "items": {"type": "string"}},
                    "reference_span": _nullable("string"),
                    "out_of_scope_reason": _nullable(
                        "string", enum=[*OutOfScopeReason.__args__, None]
                    ),
                }
            ),
        },
        "truncated": {"type": "boolean"},
    }
)
