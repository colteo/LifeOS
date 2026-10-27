"""AI-008: protocol v2 (Finance, water, meals) — the Python side of the canonical contract, the
endpoint's dispatch between protocol versions, the OpenAI adapter and the frozen prompt v3.

docs/contracts/assistant-interpretation/v2 is the single source both implementations are tested
against (the .NET side: LifeOS.UnitTests AssistantContractTests). Offline, synthetic data only.
"""

import asyncio
import copy
import json
from pathlib import Path

import pytest
from pydantic import ValidationError

from lifeos_ai.assistant import schema_v2
from lifeos_ai.assistant.interpreter import (
    DisabledInterpreter,
    InterpretationFailed,
    parse_provider_output,
    prompt_version_for,
)
from lifeos_ai.assistant.openai_interpreter import (
    PROVIDER_OUTPUT_SCHEMA,
    PROVIDER_OUTPUT_SCHEMA_V2,
    OpenAIAssistantInterpreter,
    provider_output_schema,
)
from lifeos_ai.assistant.prompt import (
    PROMPT_PROTOCOLS,
    PROMPT_V2_VERSION,
    PROMPT_V3_SHA256,
    PROMPT_V3_VERSION,
    PROMPTS,
    SYSTEM_PROMPT_V3,
    build_input,
    prompt_sha256,
)
from lifeos_ai.assistant.schema import AssistantInterpretation, InterpretRequest
from tests.assistant_fixtures import EXPENSE, request
from tests.test_assistant_api import INTERPRET_PATH, FakeAssistant, client
from tests.test_assistant_openai import MODEL, adapter, no_network, ok, settings  # noqa: F401

CONTRACT = Path(__file__).resolve().parents[4] / "docs/contracts/assistant-interpretation/v2"


def load(name: str):
    return json.loads((CONTRACT / name).read_text(encoding="utf-8"))


CASES = load("cases.json")["cases"]
EXAMPLE = load("request.example.json")
WATER = next(case for case in CASES if case["name"] == "water_ml")


def verdict(case: dict) -> str | None:
    try:
        interpretation = schema_v2.AssistantInterpretation.model_validate(case["interpretation"])
    except ValidationError:
        return "shape"
    try:
        schema_v2.validate_interpretation(interpretation, case["message"])
    except schema_v2.ProtocolViolation as violation:
        return violation.code
    return None


@pytest.mark.parametrize("case", CASES, ids=[case["name"] for case in CASES])
def test_every_golden_case_gets_the_contracts_verdict(case):
    assert verdict(case) == case["violation"]


def test_the_golden_cases_cover_every_section_and_the_routing_rule():
    verdicts = {case["violation"] for case in CASES}
    assert {None, "shape", "section_mismatch"} <= verdicts
    assert any(str(code).startswith("hydration_") for code in verdicts)
    assert any(str(code).startswith("meal_") for code in verdicts)


def test_the_strict_output_schema_is_the_contracts():
    assert load("interpretation.schema.json") == schema_v2.INTERPRETATION_JSON_SCHEMA


def test_versions_locale_and_limits_are_the_contracts_and_v1s():
    protocol = load("protocol.json")
    v1 = json.loads((CONTRACT.parent / "v1" / "protocol.json").read_text(encoding="utf-8"))

    assert (protocol["protocol_version"], protocol["output_version"]) == (
        schema_v2.PROTOCOL_VERSION,
        schema_v2.OUTPUT_VERSION,
    )
    assert (protocol["limits"], protocol["errors"], protocol["locale"]) == (
        v1["limits"],
        v1["errors"],
        "it-IT",
    )


def test_the_example_request_is_accepted_exactly_as_written():
    assert schema_v2.InterpretRequest.model_validate(EXAMPLE).model_dump(mode="json") == EXAMPLE


def test_the_request_carries_no_module_data_beyond_the_open_drafts_field_names():
    # Hydration and meals add nothing to the context: a history, a target or a value is refused.
    for extra in ({"hydration": {"today_ml": 1200}}, {"meal": {"recent": ["pizza"]}}):
        body = copy.deepcopy(EXAMPLE)
        body["context"].update(extra)
        with pytest.raises(ValidationError):
            schema_v2.InterpretRequest.model_validate(body)
    body = copy.deepcopy(EXAMPLE)
    body["context"]["open_draft"]["amount_ml"] = 300
    with pytest.raises(ValidationError):
        schema_v2.InterpretRequest.model_validate(body)


# ---- The endpoint answers both versions ----


def test_a_v2_request_gets_a_v2_interpretation_with_the_v3_prompt():
    fake = V2Assistant(WATER["interpretation"])
    body = copy.deepcopy(EXAMPLE) | {"message": WATER["message"]}
    body["context"]["open_draft"] = None

    with client(fake) as http:
        response = http.post(INTERPRET_PATH, json=body)

    assert response.status_code == 200
    assert response.json() == {
        "output_version": 2,
        "provider": "fake",
        "model": "fake-model",
        "prompt_version": PROMPT_V3_VERSION,
        "interpretation": WATER["interpretation"],
    }
    assert isinstance(fake.requests[0], schema_v2.InterpretRequest)


def test_a_v1_request_is_still_answered_exactly_as_before():
    fake = FakeAssistant()

    with client(fake) as http:
        response = http.post(INTERPRET_PATH, json=request())

    assert (response.status_code, response.json()["output_version"]) == (200, 1)
    assert response.json()["interpretation"] == EXPENSE
    assert isinstance(fake.requests[0], InterpretRequest)


@pytest.mark.parametrize(
    "answer",
    [
        # A v1 answer to a v2 request, and a v2 answer that breaks a v2 rule.
        EXPENSE,
        next(case for case in CASES if case["name"] == "expense_with_water_fields")[
            "interpretation"
        ],
    ],
)
def test_a_v2_answer_that_is_not_a_valid_v2_interpretation_never_leaves_the_service(answer):
    message = next(case for case in CASES if case["name"] == "expense_with_water_fields")["message"]
    body = copy.deepcopy(EXAMPLE) | {"message": message}

    with client(V2Assistant(answer)) as http:
        response = http.post(INTERPRET_PATH, json=body)

    assert (response.status_code, response.json()) == (
        502,
        {"error": {"code": "interpretation_failed"}},
    )


@pytest.mark.parametrize("version", [0, 3, "2"])
def test_an_unknown_protocol_version_is_an_invalid_request(version):
    with client(FakeAssistant()) as http:
        response = http.post(
            INTERPRET_PATH, json=copy.deepcopy(EXAMPLE) | {"protocol_version": version}
        )

    assert (response.status_code, response.json()["error"]["code"]) == (422, "invalid_request")


def test_the_disabled_interpreter_names_both_prompts():
    disabled = DisabledInterpreter()
    assert (prompt_version_for(disabled, 1), prompt_version_for(disabled, 2)) == (
        disabled.prompt_version,
        PROMPT_V3_VERSION,
    )


# ---- OpenAI adapter ----


def test_a_v2_request_sends_the_v3_prompt_and_the_v2_schema_and_v1_is_unchanged():
    interpreter, _, _ = adapter()
    v2 = interpreter.request_body(schema_v2.InterpretRequest.model_validate(EXAMPLE))
    v1 = interpreter.request_body(InterpretRequest.model_validate(request()))

    assert v2["input"][0]["content"] == SYSTEM_PROMPT_V3
    assert v2["text"]["format"]["schema"] == PROVIDER_OUTPUT_SCHEMA_V2
    assert PROVIDER_OUTPUT_SCHEMA_V2 == provider_output_schema(schema_v2.INTERPRETATION_JSON_SCHEMA)
    assert v1["text"]["format"]["schema"] == PROVIDER_OUTPUT_SCHEMA
    assert (v2["store"], v1["store"]) == (False, False)
    assert "tools" not in v2 and "previous_response_id" not in v2


def test_the_adapter_holds_a_v2_answer_to_protocol_v2():
    interpreter, transport, _ = adapter(ok(WATER["interpretation"]))
    body = copy.deepcopy(EXAMPLE) | {"message": WATER["message"]}

    result = asyncio.run(interpreter.interpret(schema_v2.InterpretRequest.model_validate(body)))

    assert result == schema_v2.AssistantInterpretation.model_validate(WATER["interpretation"])
    assert transport.bodies()[0]["input"][0]["content"] == SYSTEM_PROMPT_V3


def test_a_v1_answer_to_a_v2_request_is_refused_by_the_adapter():
    interpreter, _, _ = adapter(ok(EXPENSE))

    with pytest.raises(InterpretationFailed):
        asyncio.run(interpreter.interpret(schema_v2.InterpretRequest.model_validate(EXAMPLE)))


@pytest.mark.parametrize(
    "changes",
    [
        {"prompt_version_v2": PROMPT_V2_VERSION},
        {"prompt_version": PROMPT_V3_VERSION},
        {"prompt_version_v2": "assistant-unknown"},
    ],
)
def test_a_prompt_of_the_wrong_protocol_is_refused_at_start(changes):
    with pytest.raises(ValueError):
        OpenAIAssistantInterpreter(settings(**changes))


def test_parse_provider_output_follows_the_protocol_version():
    assert (
        parse_provider_output(WATER["interpretation"], WATER["message"], 2).intent
        == "hydration_intake"
    )
    with pytest.raises(InterpretationFailed):
        parse_provider_output(WATER["interpretation"], WATER["message"], 1)


# ---- Prompt v3 ----


def test_v3_is_frozen_by_its_hash_and_written_for_protocol_2():
    assert PROMPT_V3_VERSION == "assistant-interpret-v3"
    assert prompt_sha256(SYSTEM_PROMPT_V3) == PROMPT_V3_SHA256 == PROMPTS[PROMPT_V3_VERSION]
    assert PROMPT_V3_SHA256 == "21d3170dad4b6dff5454650610ae0a0006cec0a410061c452ebe5df9618d1b37"
    assert PROMPT_PROTOCOLS == {
        "assistant-finance-interpret-v1": 1,
        PROMPT_V2_VERSION: 1,
        PROMPT_V3_VERSION: 2,
    }


def test_v3_names_every_v2_intent_and_never_asks_to_convert_a_container():
    for intent in schema_v2.INTENTS:
        assert intent in SYSTEM_PROMPT_V3
    assert "bicchiere" in SYSTEM_PROMPT_V3 and "LifeOS chiede la quantità" in SYSTEM_PROMPT_V3
    assert '"protocol_version" è sempre 2' in SYSTEM_PROMPT_V3


def test_a_prompt_is_never_used_with_a_request_of_another_protocol():
    with pytest.raises(ValueError):
        build_input(InterpretRequest.model_validate(request()), PROMPT_V3_VERSION)
    with pytest.raises(ValueError):
        build_input(schema_v2.InterpretRequest.model_validate(EXAMPLE), PROMPT_V2_VERSION)


class V2Assistant(FakeAssistant):
    """Answers with the scripted interpretation: a v2 one, or a v1 one when it is not v2 (a
    provider answering the wrong protocol)."""

    prompt_version_v2 = PROMPT_V3_VERSION

    async def interpret(self, request):
        self.requests.append(request)
        try:
            return schema_v2.AssistantInterpretation.model_validate(self.outcome)
        except ValidationError:
            return AssistantInterpretation.model_validate(self.outcome)
