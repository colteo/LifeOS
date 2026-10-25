"""AI-007 F3-B1: dataset format of the `assistant_finance` evaluator (synthetic Italian
messages).

Input: `{"request": <protocol v1 InterpretRequest>, "scripted_response"?: <Responses
body>}`. `scripted_response` exists only in the offline pipeline dataset (malformed
provider outputs); a live run refuses any case that has one.

Expected, for a message case:
- `intent`, optional `intent_alternatives` (also acceptable), `unsupported_reason`;
- `fields`: the scored fields. Text fields: `state`
  (`stated`/`inferred`/`clear`/`absent`), `value` and `span` (the span is only used to
  build the reference answer; the production validator already checks it), optional
  `values` (acceptable alternatives), `states` (acceptable states) and `optional` (an
  inferred proposal may be missing). Date: `kind` and its parts. Time: hour/minute;
- `must_be_absent`: fields the model must not fill (invented values);
- optional `ambiguous_fields` (expected ambiguity) and `accept_refusal`. Fields named
  nowhere are not scored. For a pipeline case: `{"outcome": "<expected outcome>"}`.
"""

from lifeos_ai.assistant.schema import FIELD_NAMES, INTENTS, InterpretRequest

OUTCOMES = ("ok", "interpretation_failed", "interpretation_refused")
STATES = ("absent", "stated", "inferred", "clear")
DRAFT_INTENTS = frozenset({"finance_expense", "finance_income", "finance_draft_update"})
DATE_PARTS = ("kind", "days_ago", "weekday", "day", "month", "year")


def parse_input(raw) -> dict:
    if not isinstance(raw, dict) or set(raw) - {"request", "scripted_response"}:
        raise ValueError("input must hold 'request' and optionally 'scripted_response'")
    request = InterpretRequest.model_validate(raw["request"]).model_dump(mode="json")
    if request != raw["request"]:
        raise ValueError("request must be written exactly in protocol v1 form")
    scripted = raw.get("scripted_response")
    if scripted is not None and not isinstance(scripted, dict):
        raise ValueError("scripted_response must be an object")
    return raw


def parse_expected(raw) -> dict:
    if not isinstance(raw, dict):
        raise ValueError("expected must be an object")
    if "outcome" in raw:
        if set(raw) != {"outcome"} or raw["outcome"] not in OUTCOMES:
            raise ValueError("a pipeline expectation is exactly {'outcome': <outcome>}")
        return raw
    allowed = {
        "intent",
        "intent_alternatives",
        "unsupported_reason",
        "fields",
        "must_be_absent",
        "ambiguous_fields",
        "accept_refusal",
    }
    if set(raw) - allowed or not {"intent", "fields", "must_be_absent"} <= set(raw):
        raise ValueError("unexpected or missing expectation keys")
    for intent in [raw["intent"], *raw.get("intent_alternatives", [])]:
        if intent not in INTENTS:
            raise ValueError(f"unknown intent: {intent}")
    if (raw["intent"] == "unsupported") != (raw.get("unsupported_reason") is not None):
        raise ValueError("unsupported_reason is required exactly for 'unsupported'")
    fields = raw["fields"]
    absent = raw["must_be_absent"]
    if not isinstance(fields, dict) or not isinstance(absent, list):
        raise ValueError("fields must be an object and must_be_absent a list")
    for name in [*fields, *absent, *raw.get("ambiguous_fields", [])]:
        if name not in FIELD_NAMES:
            raise ValueError(f"unknown field: {name}")
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
    if raw["intent"] in DRAFT_INTENTS and not fields and not absent:
        raise ValueError("a draft-changing expectation must score at least one field")
    return raw


def expects_draft(expected: dict) -> bool:
    return expected.get("intent") in DRAFT_INTENTS
