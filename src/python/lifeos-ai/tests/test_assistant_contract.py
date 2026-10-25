"""AI-007 F3: the Python side of the canonical interpretation contract.

docs/contracts/assistant-interpretation/v1 is the single source both implementations are tested
against (the .NET side: LifeOS.UnitTests AssistantContractTests). A change to the protocol is made
there first; then both suites tell which side no longer matches. Synthetic data only.
"""

import json
from pathlib import Path

import pytest
from pydantic import ValidationError

from lifeos_ai.assistant import schema
from lifeos_ai.assistant.interpreter import InterpretationFailed, InterpretationRefused
from lifeos_ai.assistant.schema import (
    INTERPRETATION_JSON_SCHEMA,
    AssistantInterpretation,
    InterpretRequest,
    ProtocolViolation,
    validate_interpretation,
)
from lifeos_ai.errors import ProviderUnavailable
from tests.assistant_fixtures import request
from tests.test_assistant_api import INTERPRET_PATH, FakeAssistant, client

CONTRACT = Path(__file__).resolve().parents[4] / "docs/contracts/assistant-interpretation/v1"


def load(name: str):
    return json.loads((CONTRACT / name).read_text(encoding="utf-8"))


CASES = load("cases.json")["cases"]


def verdict(case: dict) -> str | None:
    try:
        interpretation = AssistantInterpretation.model_validate(case["interpretation"])
    except ValidationError:
        return "shape"
    try:
        validate_interpretation(interpretation, case["message"])
    except ProtocolViolation as violation:
        return violation.code
    return None


@pytest.mark.parametrize("case", CASES, ids=[case["name"] for case in CASES])
def test_every_golden_case_gets_the_contracts_verdict(case):
    assert verdict(case) == case["violation"]


def test_the_golden_cases_cover_valid_shape_and_rule_violations():
    verdicts = {case["violation"] for case in CASES}
    assert None in verdicts and "shape" in verdicts
    assert len([v for v in verdicts if v not in (None, "shape")]) >= 20


def test_the_strict_output_schema_is_the_contracts():
    assert load("interpretation.schema.json") == INTERPRETATION_JSON_SCHEMA


def test_versions_locale_and_limits_are_the_contracts():
    protocol = load("protocol.json")
    limits = protocol["limits"]

    assert (protocol["protocol_version"], protocol["output_version"]) == (
        schema.PROTOCOL_VERSION,
        schema.OUTPUT_VERSION,
    )
    assert protocol["locale"] == "it-IT"
    assert limits == {
        "message_length": schema.MAX_MESSAGE_LENGTH,
        "value_length": schema.MAX_VALUE_LENGTH,
        "note_length": schema.MAX_NOTE_LENGTH,
        "span_length": schema.MAX_SPAN_LENGTH,
        "category_names_per_type": schema.MAX_CATEGORY_NAMES,
        "category_name_length": schema.MAX_CATEGORY_NAME_LENGTH,
        "days_ago": schema.MAX_DAYS_AGO,
    }


def test_the_example_request_is_accepted_exactly_as_written():
    example = load("request.example.json")

    assert InterpretRequest.model_validate(example).model_dump(mode="json") == example


def test_every_error_the_endpoint_returns_is_one_of_the_contracts():
    expected = {(error["status"], error["code"]) for error in load("protocol.json")["errors"]}
    returned = set()

    def call(assistant, body=None, *, authorized=True):
        with client(assistant, authorized=authorized) as http:
            response = http.post(INTERPRET_PATH, json=request() if body is None else body)
        returned.add((response.status_code, response.json()["error"]["code"]))

    call(FakeAssistant(), authorized=False)
    call(FakeAssistant(), request(protocol_version=2))
    call(FakeAssistant(RuntimeError("synthetic")))
    for failure in (InterpretationFailed(), InterpretationRefused(), ProviderUnavailable("x")):
        call(FakeAssistant(failure))

    assert returned == expected
