"""AI-008 G1/G2: scoring of one protocol-2 interpretation against its expectation.

The Finance rules are those of `assistant_finance` (scorer 1.1.0), unchanged, so G1 is
scored as strictly as the v2 Finance prompt was. Protocol 2 adds two sections and these
rules for them:

- CRITICAL: a draft would silently hold wrong or invented data, or a draft would be
  created or changed when none was asked for. In addition to Finance: a record of the
  wrong module (water read as a meal, a meal as an expense...); a wrong or invented
  quantity, unit, date or time of water; an invented meal description (food the user
  did not say); a wrong or invented meal type, date or time; a stated date lost.
- MINOR: LifeOS asks again or the user sees it before confirming: a missed draft, a
  missing quantity, unit, description or meal type, a description worded differently, a
  different non-draft intent. A refusal or rejected (invalid) output is never critical
  (nothing is applied) but counts against coverage, unless the case accepts a refusal.

A section filled outside the intent cannot reach the scorer: the production validator
refuses it (`section_mismatch`).

Correct case: usable outcome (or an accepted refusal), acceptable intent, no error.
"""

from decimal import Decimal, InvalidOperation

from lifeos_ai.assistant.schema import normalize_for_match, normalize_spacing

from lifeos_ai_evals.core.engine import Score
from lifeos_ai_evals.evaluators.assistant_v2.model import (
    DATE_PARTS,
    DRAFT_INTENTS,
    RECORD_SECTIONS,
    expects_draft,
)

# A wrong or invented value of these fields is applied to the draft as if the user said
# it.
CRITICAL_FIELDS = {
    "finance": frozenset({"type", "amount", "currency", "account", "date", "time"}),
    "hydration": frozenset({"amount", "unit", "date", "time"}),
    "meal": frozenset({"description", "meal_type", "date", "time"}),
}
# A stated value missing here silently changes the draft (today's date, the account's
# currency).
CRITICAL_WHEN_MISSING = {
    "finance": frozenset({"date", "currency"}),
    "hydration": frozenset({"date"}),
    "meal": frozenset({"date"}),
}
# Fields whose value is the user's own wording: a different wording is minor.
WORDING_FIELDS = {"finance": frozenset({"note"}), "meal": frozenset({"description"})}


def score_case(expected: dict, predicted: dict) -> Score:
    critical: list[str] = []
    minor: list[str] = []
    outcome = predicted["outcome"]
    interpretation = predicted.get("interpretation")
    draft_expected = expects_draft(expected)
    metrics = {
        "usable_output": 0,
        "refused": int(outcome == "interpretation_refused"),
        "intent_correct": 0,
        "slots_scored": 0,
        "slots_correct": 0,
        "invented_fields": 0,
        "expects_draft": int(draft_expected),
        "predicted_draft": 0,
        "false_draft": 0,
        "draft_covered": 0,
        "unrequested_confirm": 0,
    }

    if outcome != "ok" or interpretation is None:
        accepted = outcome == "interpretation_refused" and expected.get(
            "accept_refusal", False
        )
        if not accepted:
            minor.append(f"no_interpretation:{outcome}")
        metrics["intent_correct"] = int(accepted and not draft_expected)
        return _finish(metrics, critical, minor, correct=accepted and not minor)

    metrics["usable_output"] = 1
    intent = interpretation["intent"]
    acceptable = [expected["intent"], *expected.get("intent_alternatives", [])]
    predicted_draft = intent in DRAFT_INTENTS
    metrics["predicted_draft"] = int(predicted_draft)
    open_draft = predicted.get("open_draft_section") is not None
    # LifeOS never confirms from chat (only the button does), so nothing is applied;
    # gated at zero all the same (as assistant-finance-gates-v2).
    metrics["unrequested_confirm"] = int(
        intent == "draft_confirm" and intent not in acceptable
    )

    if intent in acceptable:
        metrics["intent_correct"] = 1
    elif predicted_draft and not draft_expected:
        critical.append(f"false_draft:{intent}")
        metrics["false_draft"] = 1
    elif {intent, expected["intent"]} == {"finance_expense", "finance_income"}:
        critical.append("type_swapped")
    elif intent in RECORD_SECTIONS and expected["intent"] in RECORD_SECTIONS:
        critical.append(f"wrong_module:{intent}")
    elif (
        expected["intent"] in RECORD_SECTIONS
        and intent == "draft_update"
        and open_draft
    ):
        critical.append("new_record_written_over_open_draft")
    else:
        minor.append(f"intent:{intent}")

    if (
        expected["intent"] == "unsupported"
        and intent == "unsupported"
        and interpretation.get("unsupported_reason")
        != expected.get("unsupported_reason")
    ):
        minor.append("unsupported_reason")

    # Fields are scored whenever the output fills the expected section (as Finance did
    # for any draft intent, a swapped type included).
    section = expected["section"]
    predicted_section = RECORD_SECTIONS.get(intent) or (
        predicted.get("open_draft_section") if intent == "draft_update" else None
    )
    if section is not None and predicted_section == section:
        _score_fields(
            expected, section, interpretation[section], critical, minor, metrics
        )
        _score_ambiguity(expected, section, interpretation, critical, minor)

    metrics["draft_covered"] = int(
        draft_expected
        and predicted_draft
        and metrics["intent_correct"] == 1
        and not critical
    )
    return _finish(metrics, critical, minor, correct=not critical and not minor)


def _score_fields(expected, section, fields, critical, minor, metrics) -> None:
    for name, spec in expected["fields"].items():
        metrics["slots_scored"] += 1
        problem = _field_problem(section, name, spec, fields[name])
        if problem is None:
            metrics["slots_correct"] += 1
            continue
        severity, label = problem
        (critical if severity == "critical" else minor).append(f"{name}:{label}")

    for name in expected["must_be_absent"]:
        metrics["slots_scored"] += 1
        state = fields[name]["state"]
        if state == "absent":
            metrics["slots_correct"] += 1
            continue
        metrics["invented_fields"] += 1
        stated_user_category = (
            section == "finance" and name == "category" and state == "stated"
        )
        invented_critical = CRITICAL_FIELDS[section] - {"type"}
        if name in invented_critical or stated_user_category:
            critical.append(f"{name}:invented_{state}")
        else:
            minor.append(f"{name}:invented_{state}")


def _field_problem(section: str, name: str, spec: dict, actual: dict):
    """None when acceptable, else (severity, label)."""
    want = spec["state"]
    states = spec.get("states") or [want]
    got = actual["state"]
    critical_field = name in CRITICAL_FIELDS[section]

    if want == "absent":
        if got == "absent":
            return None
        return ("critical" if critical_field else "minor", f"invented_{got}")

    if want == "clear":
        if got == "clear":
            return None
        if got == "absent":
            return ("minor", "not_cleared")
        return ("critical" if critical_field else "minor", f"{got}_instead_of_clear")

    if got == "absent":
        if want == "inferred" and spec.get("optional"):
            return None
        if want == "stated" and name in CRITICAL_WHEN_MISSING[section]:
            return ("critical", "missing")
        return ("minor", "missing")

    if got == "clear":
        return ("critical" if critical_field else "minor", "cleared")

    matches = _matches(name, spec, actual)
    if got in states and matches:
        return None
    if section == "finance" and name == "category":
        # A stated category becomes the user's choice: a wrong one is applied as theirs.
        if got == "stated" and not matches:
            return ("critical", "wrong_user_category")
        if want == "stated" and not matches:
            return ("critical", "user_category_replaced")
        return ("minor", f"{got}_instead_of_{want}")
    if name in WORDING_FIELDS.get(section, ()):
        return ("minor", "different" if not matches else f"{got}_instead_of_{want}")
    if not matches:
        return ("critical" if critical_field else "minor", "wrong_value")
    return ("minor", f"{got}_instead_of_{want}")


def _matches(name: str, spec: dict, actual: dict) -> bool:
    if name == "date":
        return all(actual.get(part) == spec.get(part) for part in DATE_PARTS)
    if name == "time":
        return actual.get("hour") == spec.get("hour") and actual.get(
            "minute"
        ) == spec.get("minute")
    values = spec.get("values") or [spec.get("value")]
    value = actual.get("value")
    if value is None:
        return False
    if name == "amount":
        return any(_same_amount(value, candidate) for candidate in values)
    if name in ("currency", "type", "unit", "meal_type"):
        return value in values
    if name in ("note", "description"):
        return normalize_spacing(value) in {normalize_spacing(v) for v in values}
    return normalize_for_match(value) in {normalize_for_match(v) for v in values}


def _same_amount(left: str, right: str) -> bool:
    try:
        return Decimal(left) == Decimal(right)
    except (InvalidOperation, TypeError):
        return False


def _score_ambiguity(expected, section, interpretation, critical, minor) -> None:
    for name in expected.get("ambiguous_fields", []):
        field = interpretation[section][name]
        if field["state"] == "stated":
            # Guessing between two readings writes one of them as if the user chose it.
            target = critical if name in CRITICAL_FIELDS[section] else minor
            target.append(f"{name}:ambiguity_guessed")
        elif name not in interpretation["ambiguous_fields"]:
            minor.append(f"{name}:ambiguity_not_reported")


def _finish(metrics: dict, critical: list, minor: list, *, correct: bool) -> Score:
    metrics = {**metrics, "critical_errors": len(critical), "minor_errors": len(minor)}
    return Score(correct, metrics, {"critical": critical, "minor": minor})


def _ratio(numerator: int, denominator: int) -> float | None:
    return None if denominator == 0 else numerator / denominator


class AssistantV2Scorer:
    configuration = {
        "scorer": "assistant-v2-severity",
        "version": "1.0.0",
        "critical_fields": {k: sorted(v) for k, v in CRITICAL_FIELDS.items()},
        "critical_when_missing": {
            k: sorted(v) for k, v in CRITICAL_WHEN_MISSING.items()
        },
    }

    def score(self, expected: dict, predicted: dict) -> Score:
        return score_case(expected, predicted)

    def aggregate(self, scores: list[Score]) -> dict:
        def total(key: str) -> int:
            return sum(score.metrics.get(key, 0) for score in scores)

        cases = len(scores)
        drafts = total("expects_draft")
        critical_cases = sum(score.metrics["critical_errors"] > 0 for score in scores)
        return {
            "cases": cases,
            "usable_output_rate": _ratio(total("usable_output"), cases),
            "refusals": total("refused"),
            "intent_accuracy": _ratio(total("intent_correct"), cases),
            "slot_accuracy": _ratio(total("slots_correct"), total("slots_scored")),
            "invented_fields": total("invented_fields"),
            "critical_error_cases": critical_cases,
            "critical_error_rate": _ratio(critical_cases, cases),
            "false_draft_rate": _ratio(total("false_draft"), cases - drafts),
            "unrequested_confirm_cases": total("unrequested_confirm"),
            "draft_coverage": _ratio(total("draft_covered"), drafts),
            "minor_error_cases": sum(
                score.metrics["minor_errors"] > 0 for score in scores
            ),
            "case_accuracy": _ratio(sum(score.correct for score in scores), cases),
        }
