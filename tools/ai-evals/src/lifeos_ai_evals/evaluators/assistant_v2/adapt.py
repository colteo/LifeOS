"""AI-008 G1: the frozen protocol-1 Finance datasets of `assistant_finance`, read in
protocol 2.

The conversion is mechanical and follows the v2 contract ("What changed from v1"): the
open draft moves to `context.open_draft` (same kind, fields and awaiting field),
`context.finance` keeps the category names, the draft intents lose their `finance_`
prefix, `multiple_transactions` becomes `multiple_operations`, and every draft-changing
expectation scores the `finance` section. Messages, fields, values, states, spans and
the rest of the expectations are unchanged, so the gates apply to the same frozen cases.

The source file is loaded (and validated) by `assistant_finance`; the converted dataset
keeps its name, version and SHA-256, so a G1 run is tied to the frozen file.

History:
- `assistant-finance-v1-to-v2-1`: the mechanical conversion only.
- `assistant-finance-v1-to-v2-2` (2026-10-31, after the first live run, owner approval):
  one expectation that protocol 2 makes obsolete is replaced (`PROTOCOL_2_OVERRIDES`).
  `h_other_capability_meal` logs a meal: in protocol 1 that was another capability
  (`unsupported`); in protocol 2 meals are a supported module, so the v2 contract's
  answer is a `nutrition_meal` draft (description verbatim, meal type named). Nothing
  else changed; no Finance expectation was touched.
"""

from dataclasses import replace

from lifeos_ai_evals.core.engine import Case, Dataset
from lifeos_ai_evals.evaluators.assistant_finance import plugin as finance_plugin
from lifeos_ai_evals.evaluators.assistant_v2.model import (
    DRAFT_INTENTS,
    parse_expected,
    parse_input,
)

ADAPTER_VERSION = "assistant-finance-v1-to-v2-2"
INTENTS = {
    "finance_draft_update": "draft_update",
    "finance_draft_confirm": "draft_confirm",
    "finance_draft_cancel": "draft_cancel",
}
REASONS = {"multiple_transactions": "multiple_operations"}
# (dataset name, case id) -> the protocol-2 expectation of a case whose protocol-1
# answer protocol 2 makes wrong (see History). Only modules protocol 2 added appear.
PROTOCOL_2_OVERRIDES = {
    ("assistant_finance_holdout", "h_other_capability_meal"): {
        "intent": "nutrition_meal",
        "unsupported_reason": None,
        "section": "meal",
        "fields": {
            "description": {
                "state": "stated",
                "value": "un'insalata",
                "span": "un'insalata",
                "values": ["un'insalata", "insalata"],
            },
            "meal_type": {"state": "stated", "value": "lunch", "span": "pranzo"},
        },
        "must_be_absent": ["date", "time"],
    },
}


def request(v1: dict) -> dict:
    finance = v1["context"]["finance"]
    return {
        "protocol_version": 2,
        "message": v1["message"],
        "context": {
            "locale": v1["context"]["locale"],
            "open_draft": finance["open_draft"],
            "finance": {
                "expense_categories": finance["expense_categories"],
                "income_categories": finance["income_categories"],
            },
        },
    }


def expected(v1: dict) -> dict:
    intent = INTENTS.get(v1["intent"], v1["intent"])
    converted = {
        **v1,
        "intent": intent,
        "section": "finance" if intent in DRAFT_INTENTS else None,
    }
    if "intent_alternatives" in v1:
        converted["intent_alternatives"] = [
            INTENTS.get(name, name) for name in v1["intent_alternatives"]
        ]
    if v1.get("unsupported_reason") is not None:
        converted["unsupported_reason"] = REASONS.get(
            v1["unsupported_reason"], v1["unsupported_reason"]
        )
    if converted["section"] is None:
        # A non-draft intent scores no field (as in v1, where none was listed).
        converted["fields"], converted["must_be_absent"] = {}, []
        converted.pop("ambiguous_fields", None)
    return converted


def convert(dataset: Dataset) -> Dataset:
    cases = []
    for case in dataset.cases:
        if "scripted_response" in case.input:
            raise ValueError(f"{case.id}: scripted pipeline cases are protocol 1 only")
        cases.append(
            Case(
                case.id,
                parse_input({"request": request(case.input["request"])}),
                parse_expected(
                    PROTOCOL_2_OVERRIDES.get((dataset.name, case.id))
                    or expected(case.expected)
                ),
                case.tags,
                case.description,
            )
        )
    return replace(dataset, cases=tuple(cases))


def finance_dataset(which: str) -> Dataset:
    """`v1` or `holdout-v1` of assistant_finance, converted."""
    paths = {
        "v1": finance_plugin.default_dataset,
        "holdout-v1": finance_plugin.holdout_dataset,
    }
    return convert(finance_plugin.load(paths[which]()))
