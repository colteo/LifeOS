"""AI-007 F3: assistant interpretation protocol v1: request shape, output rules, strict schema."""

import copy

import pytest
from pydantic import ValidationError

from lifeos_ai.assistant.interpreter import InterpretationFailed, parse_provider_output
from lifeos_ai.assistant.schema import (
    INTERPRETATION_JSON_SCHEMA,
    AssistantInterpretation,
    InterpretRequest,
    ProtocolViolation,
    validate_interpretation,
)
from tests.assistant_fixtures import EXPENSE, MESSAGE, expense, request


def validate(data: dict, message: str = MESSAGE) -> None:
    validate_interpretation(AssistantInterpretation.model_validate(data), message)


def test_a_typical_request_and_interpretation_are_valid():
    assert InterpretRequest.model_validate(request()).message == MESSAGE
    validate(EXPENSE)


@pytest.mark.parametrize(
    "changes",
    [
        {"user_id": "0193a7c2-0000-7000-8000-000000000000"},
        {"history": [{"role": "user", "text": "ciao"}]},
        {"protocol_version": 2},
        {"message": ""},
        {"message": "x" * 1001},
        {"message": "nul\x00"},
    ],
)
def test_the_request_rejects_unknown_fields_ids_and_bad_messages(changes):
    with pytest.raises(ValidationError):
        InterpretRequest.model_validate(request(**changes))


def test_the_request_context_rejects_account_lists_and_values():
    data = request()
    data["context"]["finance"]["accounts"] = ["Revolut"]
    with pytest.raises(ValidationError):
        InterpretRequest.model_validate(data)

    data = request()
    data["context"]["finance"]["open_draft"] = {
        "kind": "finance.transaction.create",
        "type": "expense",
        "filled_fields": ["amount"],
        "missing_fields": ["account"],
        "awaiting_field": "account",
        "amount": "5",
    }
    with pytest.raises(ValidationError):
        InterpretRequest.model_validate(data)


def test_the_output_rejects_unknown_fields_and_confidence_scores():
    data = copy.deepcopy(EXPENSE)
    data["confidence"] = 0.97
    with pytest.raises(ValidationError):
        AssistantInterpretation.model_validate(data)


@pytest.mark.parametrize(
    ("data", "code"),
    [
        # A stated value must come from the message.
        (
            expense(amount={"state": "stated", "value": "7", "span": "7 euro"}),
            "amount_span_not_in_message",
        ),
        (expense(account={"state": "stated", "value": "Revolut", "span": None}), "account_span"),
        # The model may propose a category or a note, never an amount, an account, a date or a time.
        (expense(amount={"state": "inferred", "value": "5", "span": None}), "amount_inferred"),
        (
            expense(account={"state": "inferred", "value": "Revolut", "span": None}),
            "account_inferred",
        ),
        # Formats and removable fields.
        (expense(amount={"state": "stated", "value": "5,00", "span": "5"}), "amount_format"),
        (expense(currency={"state": "stated", "value": "euro", "span": "euro"}), "currency_format"),
        (expense(amount={"state": "clear", "value": None, "span": None}), "amount_clear"),
        (expense(note={"state": "absent", "value": "x", "span": None}), "note_unexpected_value"),
        (
            expense(type={"state": "stated", "value": "income", "span": "speso"}),
            "type_contradicts_intent",
        ),
        # Date and time parts.
        (
            expense(
                date={
                    "state": "stated",
                    "kind": "yesterday",
                    "days_ago": 1,
                    "weekday": None,
                    "day": None,
                    "month": None,
                    "year": None,
                    "span": "speso",
                }
            ),
            "date_parts",
        ),
        (
            expense(time={"state": "stated", "hour": 25, "minute": 0, "span": "speso"}),
            "time_parts",
        ),
        ({**EXPENSE, "intent": "unsupported"}, "unsupported_reason"),
    ],
)
def test_protocol_rules_are_enforced_never_repaired(data, code):
    with pytest.raises(ProtocolViolation) as failure:
        validate(data)
    assert failure.value.code == code


def test_spans_are_matched_without_case_and_spacing():
    validate(expense(amount={"state": "stated", "value": "5", "span": "5   EURO"}))


def test_parse_provider_output_turns_any_unusable_output_into_interpretation_failed():
    assert parse_provider_output(EXPENSE, MESSAGE).intent == "finance_expense"
    for raw in (
        "not json",
        {"intent": "finance_expense"},
        expense(account={"state": "inferred", "value": "X", "span": None}),
    ):
        with pytest.raises(InterpretationFailed):
            parse_provider_output(raw, MESSAGE)


def _objects(schema):
    if isinstance(schema, dict):
        if schema.get("type") == "object":
            yield schema
        for value in schema.values():
            yield from _objects(value)
    elif isinstance(schema, list):
        for value in schema:
            yield from _objects(value)


def test_the_json_schema_is_strict_every_object_requires_all_and_forbids_additional_fields():
    objects = list(_objects(INTERPRETATION_JSON_SCHEMA))
    assert len(objects) >= 5
    for schema in objects:
        assert schema["additionalProperties"] is False
        assert schema["required"] == list(schema["properties"])


def test_the_json_schema_matches_the_pydantic_models():
    def model_fields(model):
        return list(model.model_fields)

    finance = INTERPRETATION_JSON_SCHEMA["properties"]["finance"]["properties"]
    assert list(INTERPRETATION_JSON_SCHEMA["properties"]) == model_fields(AssistantInterpretation)
    assert list(finance) == list(EXPENSE["finance"])
    assert list(finance["date"]["properties"]) == list(EXPENSE["finance"]["date"])
    assert list(finance["time"]["properties"]) == list(EXPENSE["finance"]["time"])
    assert list(finance["amount"]["properties"]) == list(EXPENSE["finance"]["amount"])
