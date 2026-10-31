"""AI-008 G1/G2: dataset format of the `assistant_v2` evaluator (synthetic Italian
messages, protocol 2: `docs/contracts/assistant-interpretation/v2`).

Input: `{"request": <protocol v2 InterpretRequest>}`, written exactly in contract form.

Expected, for a message case:
- `intent`, optional `intent_alternatives`, `unsupported_reason`;
- `section`: the section the intent fills (`finance`, `hydration`, `meal`), null for an
  intent that changes no draft. A `draft_update` names the open draft's section;
- `fields`: the scored fields of that section. Text fields: `state`
  (`stated`/`inferred`/`clear`/`absent`), `value` and `span` (the span only builds the
  reference answer; the production validator already checks it), optional `values`
  (acceptable alternatives), `states` (acceptable states) and `optional` (an inferred
  proposal may be missing). Date: `kind` and its parts. Time: hour/minute;
- `must_be_absent`: fields of that section the model must not fill (invented values);
- optional `ambiguous_fields` and `accept_refusal`. Fields named nowhere are not scored.

The Finance cases of G1 are not written here: `adapt.py` converts the frozen protocol-1
datasets of `assistant_finance` to this format.
"""

from lifeos_ai.assistant.schema_v2 import INTENTS, InterpretRequest

STATES = ("absent", "stated", "inferred", "clear")
DATE_PARTS = ("kind", "days_ago", "weekday", "day", "month", "year")
SECTION_FIELDS = {
    "finance": (
        "type",
        "amount",
        "currency",
        "account",
        "category",
        "note",
        "date",
        "time",
    ),
    "hydration": ("amount", "unit", "date", "time"),
    "meal": ("description", "meal_type", "date", "time"),
}
# Intents that create or change a draft, and the section a new record fills.
RECORD_SECTIONS = {
    "finance_expense": "finance",
    "finance_income": "finance",
    "hydration_intake": "hydration",
    "nutrition_meal": "meal",
}
DRAFT_INTENTS = frozenset({*RECORD_SECTIONS, "draft_update"})
DRAFT_KIND_SECTIONS = {
    "finance.transaction.create": "finance",
    "hydration.intake.create": "hydration",
    "nutrition.meal.create": "meal",
}


def parse_input(raw) -> dict:
    if not isinstance(raw, dict) or set(raw) != {"request"}:
        raise ValueError("input must hold exactly 'request'")
    request = InterpretRequest.model_validate(raw["request"]).model_dump(mode="json")
    if request != raw["request"]:
        raise ValueError("request must be written exactly in protocol v2 form")
    return raw


def parse_expected(raw) -> dict:
    if not isinstance(raw, dict):
        raise ValueError("expected must be an object")
    allowed = {
        "intent",
        "intent_alternatives",
        "unsupported_reason",
        "section",
        "fields",
        "must_be_absent",
        "ambiguous_fields",
        "accept_refusal",
    }
    required = {"intent", "section", "fields", "must_be_absent"}
    if set(raw) - allowed or not required <= set(raw):
        raise ValueError("unexpected or missing expectation keys")
    for intent in [raw["intent"], *raw.get("intent_alternatives", [])]:
        if intent not in INTENTS:
            raise ValueError(f"unknown intent: {intent}")
    if (raw["intent"] == "unsupported") != (raw.get("unsupported_reason") is not None):
        raise ValueError("unsupported_reason is required exactly for 'unsupported'")
    section = raw["section"]
    intent = raw["intent"]
    if intent in RECORD_SECTIONS and section != RECORD_SECTIONS[intent]:
        raise ValueError(f"{intent} fills the {RECORD_SECTIONS[intent]} section")
    if intent == "draft_update" and section not in SECTION_FIELDS:
        raise ValueError("a draft_update names the open draft's section")
    if intent not in DRAFT_INTENTS and section is not None:
        raise ValueError("an intent that changes no draft has no section")
    fields, absent = raw["fields"], raw["must_be_absent"]
    if not isinstance(fields, dict) or not isinstance(absent, list):
        raise ValueError("fields must be an object and must_be_absent a list")
    names = SECTION_FIELDS.get(section, ())
    for name in [*fields, *absent, *raw.get("ambiguous_fields", [])]:
        if name not in names:
            raise ValueError(f"unknown field of section {section}: {name}")
    if set(fields) & set(absent):
        raise ValueError("a field cannot be both expected and forbidden")
    for name, spec in fields.items():
        if not isinstance(spec, dict) or spec.get("state") not in STATES:
            raise ValueError(f"{name}: a state is required")
        for state in spec.get("states", []):
            if state not in STATES:
                raise ValueError(f"{name}: unknown state {state}")
        if spec["state"] == "inferred" and not spec.get("values"):
            raise ValueError(
                f"{name}: an inferred expectation lists its acceptable values"
            )
    if intent in DRAFT_INTENTS and not fields and not absent:
        raise ValueError("a draft-changing expectation must score at least one field")
    return raw


def expects_draft(expected: dict) -> bool:
    return expected.get("intent") in DRAFT_INTENTS
