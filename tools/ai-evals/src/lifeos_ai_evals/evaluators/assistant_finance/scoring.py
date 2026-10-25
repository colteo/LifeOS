"""AI-007 F3-B1: scoring of one interpretation against its synthetic expectation.

Severity (what would happen in LifeOS if the output were applied):
- CRITICAL: a draft would silently hold wrong or invented data, or a draft would be
  created or changed when none was asked for: a false-positive draft; expense/income
  swapped; a wrong or invented amount, currency, account, date, time or user category;
  an ambiguity resolved by guessing; a new expense written over the open draft; a
  malformed output accepted.
- MINOR: LifeOS would ask again or the user sees it before confirming: a missed draft, a
  missing field (except a stated date or currency, whose absence silently changes the
  draft), a different proposed category or note, an extra proposal, a different
  non-draft intent. A refusal or rejected (invalid) output is never critical: nothing is
  applied. It counts against coverage, unless the case accepts a refusal.

Correct case: usable outcome (or an accepted refusal), acceptable intent, no error at
all.
"""

from decimal import Decimal, InvalidOperation

from lifeos_ai.assistant.schema import normalize_for_match, normalize_spacing

from lifeos_ai_evals.core.engine import Score
from lifeos_ai_evals.evaluators.assistant_finance.model import (
    DATE_PARTS,
    DRAFT_INTENTS,
    expects_draft,
)

# A wrong or invented value of these fields is applied to the draft as if the user said
# it.
CRITICAL_FIELDS = frozenset({"type", "amount", "currency", "account", "date", "time"})
# A stated value missing here silently changes the draft (today's date, the account's
# currency).
CRITICAL_WHEN_MISSING = frozenset({"date", "currency"})


def score_case(expected: dict, predicted: dict) -> Score:
    if "outcome" in expected:
        return _score_pipeline(expected, predicted)

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
    open_draft = predicted.get("open_draft", False)
    # "Confirm" read where the user did not say it. LifeOS never confirms from chat
    # (only the button does), so nothing is applied; gated at zero all the same
    # (assistant-finance-gates-v2).
    metrics["unrequested_confirm"] = int(
        intent == "finance_draft_confirm" and intent not in acceptable
    )

    if intent in acceptable:
        metrics["intent_correct"] = 1
    elif predicted_draft and not draft_expected:
        critical.append(f"false_draft:{intent}")
        metrics["false_draft"] = 1
    elif {intent, expected["intent"]} == {"finance_expense", "finance_income"}:
        critical.append("type_swapped")
    elif expected["intent"] in ("finance_expense", "finance_income") and (
        intent == "finance_draft_update" and open_draft
    ):
        critical.append("new_transaction_written_over_open_draft")
    else:
        minor.append(f"intent:{intent}")

    if (
        expected["intent"] == "unsupported"
        and intent == "unsupported"
        and interpretation.get("unsupported_reason")
        != expected.get("unsupported_reason")
    ):
        minor.append("unsupported_reason")

    if predicted_draft:
        _score_fields(expected, interpretation["finance"], critical, minor, metrics)
        _score_ambiguity(expected, interpretation, critical, minor)

    metrics["draft_covered"] = int(
        draft_expected
        and predicted_draft
        and metrics["intent_correct"] == 1
        and not critical
    )
    return _finish(metrics, critical, minor, correct=not critical and not minor)


def _score_fields(expected, finance, critical, minor, metrics) -> None:
    for name, spec in expected["fields"].items():
        metrics["slots_scored"] += 1
        problem = _field_problem(name, spec, finance[name])
        if problem is None:
            metrics["slots_correct"] += 1
            continue
        severity, label = problem
        (critical if severity == "critical" else minor).append(f"{name}:{label}")

    for name in expected["must_be_absent"]:
        metrics["slots_scored"] += 1
        state = finance[name]["state"]
        if state == "absent":
            metrics["slots_correct"] += 1
            continue
        metrics["invented_fields"] += 1
        stated_user_category = name == "category" and state == "stated"
        if name in CRITICAL_FIELDS - {"type"} or stated_user_category:
            critical.append(f"{name}:invented_{state}")
        else:
            minor.append(f"{name}:invented_{state}")


def _field_problem(name: str, spec: dict, actual: dict):
    """None when acceptable, else (severity, label)."""
    want = spec["state"]
    states = spec.get("states") or [want]
    got = actual["state"]
    critical_field = name in CRITICAL_FIELDS

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
        if want == "stated" and name in CRITICAL_WHEN_MISSING:
            return ("critical", "missing")
        return ("minor", "missing")

    if got == "clear":
        return ("critical" if critical_field else "minor", "cleared")

    matches = _matches(name, spec, actual)
    if got in states and matches:
        return None
    if name == "category":
        # A stated category becomes the user's choice: a wrong one is applied as theirs.
        if got == "stated" and not matches:
            return ("critical", "wrong_user_category")
        if want == "stated" and not matches:
            return ("critical", "user_category_replaced")
        return ("minor", f"{got}_instead_of_{want}")
    if name == "note":
        return ("minor", "different" if not matches else f"{got}_instead_of_{want}")
    if not matches:
        return ("critical", "wrong_value")
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
    if name in ("currency", "type"):
        return value in values
    if name == "note":
        return normalize_spacing(value) in {normalize_spacing(v) for v in values}
    return normalize_for_match(value) in {normalize_for_match(v) for v in values}


def _same_amount(left: str, right: str) -> bool:
    try:
        return Decimal(left) == Decimal(right)
    except (InvalidOperation, TypeError):
        return False


def _score_ambiguity(expected, interpretation, critical, minor) -> None:
    for name in expected.get("ambiguous_fields", []):
        field = interpretation["finance"][name]
        if field["state"] == "stated":
            # Guessing between two readings writes one of them as if the user chose it.
            target = critical if name in CRITICAL_FIELDS else minor
            target.append(f"{name}:ambiguity_guessed")
        elif name not in interpretation["ambiguous_fields"]:
            minor.append(f"{name}:ambiguity_not_reported")


def _score_pipeline(expected: dict, predicted: dict) -> Score:
    outcome = predicted["outcome"]
    critical = ["malformed_output_accepted"] if outcome == "ok" else []
    minor = [] if outcome == expected["outcome"] or critical else [f"outcome:{outcome}"]
    metrics = {"pipeline_rejected": int(outcome != "ok")}
    return _finish(metrics, critical, minor, correct=not critical and not minor)


def _finish(metrics: dict, critical: list, minor: list, *, correct: bool) -> Score:
    metrics = {**metrics, "critical_errors": len(critical), "minor_errors": len(minor)}
    return Score(correct, metrics, {"critical": critical, "minor": minor})


def _ratio(numerator: int, denominator: int) -> float | None:
    return None if denominator == 0 else numerator / denominator


class AssistantFinanceScorer:
    configuration = {
        "scorer": "assistant-finance-severity",
        "version": "1.1.0",
        "critical_fields": sorted(CRITICAL_FIELDS),
        "critical_when_missing": sorted(CRITICAL_WHEN_MISSING),
    }

    def score(self, expected: dict, predicted: dict) -> Score:
        return score_case(expected, predicted)

    def aggregate(self, scores: list[Score]) -> dict:
        def total(key: str) -> int:
            return sum(score.metrics.get(key, 0) for score in scores)

        message_scores = [
            score for score in scores if "pipeline_rejected" not in score.metrics
        ]
        pipeline_scores = [
            score for score in scores if "pipeline_rejected" in score.metrics
        ]
        cases = len(message_scores)
        drafts = sum(score.metrics["expects_draft"] for score in message_scores)
        critical_cases = sum(score.metrics["critical_errors"] > 0 for score in scores)
        return {
            "cases": len(scores),
            "message_cases": cases,
            "pipeline_cases": len(pipeline_scores),
            "pipeline_rejected": total("pipeline_rejected"),
            "usable_output_rate": _ratio(total("usable_output"), cases),
            "refusals": total("refused"),
            "intent_accuracy": _ratio(total("intent_correct"), cases),
            "slot_accuracy": _ratio(total("slots_correct"), total("slots_scored")),
            "invented_fields": total("invented_fields"),
            "critical_error_cases": critical_cases,
            "critical_error_rate": _ratio(critical_cases, len(scores)),
            "false_draft_rate": _ratio(total("false_draft"), cases - drafts),
            "unrequested_confirm_cases": total("unrequested_confirm"),
            "draft_coverage": _ratio(total("draft_covered"), drafts),
            "minor_error_cases": sum(
                score.metrics["minor_errors"] > 0 for score in scores
            ),
            "case_accuracy": _ratio(
                sum(score.correct for score in scores), len(scores)
            ),
        }
