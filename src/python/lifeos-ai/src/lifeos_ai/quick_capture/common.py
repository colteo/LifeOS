"""What the Quick Capture contracts share: versions, kinds and their field names, limits and the
authorised text a specialist's spans are checked against."""

from collections.abc import Iterable
from typing import Annotated, Literal

from pydantic import AfterValidator, StringConstraints

from lifeos_ai.assistant.schema import MAX_VALUE_LENGTH, _no_nul, normalize_for_match

PROTOCOL_VERSION = 1
OUTPUT_VERSION = 1
LOCALE = "it-IT"

MAX_SEGMENTS = 5
MAX_OPEN_DRAFTS = 10
MAX_OPEN_DRAFT_KINDS = 3
MAX_CONTEXT_SPANS = 3
MAX_CONTEXT_SPAN_LENGTH = MAX_VALUE_LENGTH
MAX_REFERENCE_SPAN_LENGTH = MAX_VALUE_LENGTH

FinanceFieldName = Literal[
    "type", "amount", "currency", "account", "category", "note", "date", "time"
]
HydrationFieldName = Literal["amount", "unit", "date", "time"]
MealFieldName = Literal["description", "meal_type", "date", "time"]

FINANCE_KIND = "finance.transaction.create"
HYDRATION_KIND = "hydration.intake.create"
MEAL_KIND = "nutrition.meal.create"

# Each draft kind and the names of its fields (the order of the contracts).
KIND_FIELDS: dict[str, tuple[str, ...]] = {
    FINANCE_KIND: FinanceFieldName.__args__,
    HYDRATION_KIND: HydrationFieldName.__args__,
    MEAL_KIND: MealFieldName.__args__,
}


def unique(names: list) -> list:
    """For `uniqueItems` lists: a repeated name is refused, never collapsed."""
    if len(set(names)) != len(names):
        raise ValueError("items must be unique")
    return names


# A router context span as a specialist receives it (1–200 code points, not blank).
ContextSpan = Annotated[
    str,
    StringConstraints(strip_whitespace=True, min_length=1, max_length=MAX_CONTEXT_SPAN_LENGTH),
    AfterValidator(_no_nul),
]


class AuthorisedText:
    """The texts a specialist may quote: its segment and each context span, separately.

    Stands in for the v1/v2 "message" in the shared field rules (`normalized span in message`):
    a span is authorised when it is found, in match form, inside ONE of the texts; a span that
    straddles two of them is not.
    """

    def __init__(self, segment: str, context_spans: Iterable[str] = ()):
        self._texts = tuple(normalize_for_match(text) for text in (segment, *context_spans))

    def __contains__(self, normalized_span: object) -> bool:
        return isinstance(normalized_span, str) and any(
            normalized_span in text for text in self._texts
        )
