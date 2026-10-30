"""AI-009 I1: the Quick Capture contracts (router, finance, hydration, meal) — the Python side.

docs/contracts/quick-capture/*/v1 is the single source both implementations are tested against;
nothing here changes it. Offline, synthetic data only.
"""

import json
from pathlib import Path

import pytest
from pydantic import ValidationError

from lifeos_ai.assistant import schema, schema_v2
from lifeos_ai.assistant.schema import ProtocolViolation
from lifeos_ai.quick_capture import common, router, specialists

CONTRACTS = Path(__file__).resolve().parents[4] / "docs/contracts/quick-capture"
MODULES = ("router", "finance", "hydration", "meal")
SPECIALISTS = ("finance", "hydration", "meal")


def load(module: str, name: str):
    return json.loads((CONTRACTS / module / "v1" / name).read_text(encoding="utf-8"))


OUTPUTS = {
    "router": router.RouterOutput,
    "finance": specialists.FinanceInterpretation,
    "hydration": specialists.HydrationInterpretation,
    "meal": specialists.MealInterpretation,
}
REQUESTS = {
    "router": router.RouteRequest,
    "finance": specialists.FinanceInterpretRequest,
    "hydration": specialists.HydrationInterpretRequest,
    "meal": specialists.MealInterpretRequest,
}
SCHEMAS = {
    "router": router.ROUTER_JSON_SCHEMA,
    "finance": specialists.FINANCE_JSON_SCHEMA,
    "hydration": specialists.HYDRATION_JSON_SCHEMA,
    "meal": specialists.MEAL_JSON_SCHEMA,
}
ROUTER_CASES = load("router", "cases.json")


def verdict(module: str, case: dict) -> str | None:
    try:
        output = OUTPUTS[module].model_validate(case["interpretation"])
    except ValidationError:
        return "shape"
    try:
        if module == "router":
            max_segments = case.get("max_segments", ROUTER_CASES["default_max_segments"])
            router.validate_route(output, case["message"], max_segments)
        else:
            specialists.validate_specialist(
                output, case["mode"], case["segment"], case["context_spans"]
            )
    except ProtocolViolation as violation:
        return violation.code
    return None


def cases(name: str, keep=lambda case: True) -> list:
    return [
        pytest.param(module, case, id=f"{module}-{case['name']}")
        for module in MODULES
        for case in load(module, name)["cases"]
        if keep(case)
    ]


# ---- Golden cases ----


@pytest.mark.parametrize(("module", "case"), cases("cases.json"))
def test_every_golden_case_gets_the_contracts_verdict(module, case):
    assert verdict(module, case) == case["violation"]


@pytest.mark.parametrize(
    ("module", "valid", "shape", "rule"),
    [
        ("router", 19, 10, 21),
        ("finance", 11, 5, 17),
        ("hydration", 6, 3, 6),
        ("meal", 5, 2, 7),
    ],
)
def test_every_case_of_the_contract_is_exercised(module, valid, shape, rule):
    violations = [case["violation"] for case in load(module, "cases.json")["cases"]]

    assert violations.count(None) == valid
    assert violations.count("shape") == shape
    assert len(violations) - valid - shape == rule


def test_the_router_cases_cover_every_rule_of_the_router_readme():
    codes = {case["violation"] for case in ROUTER_CASES["cases"]} - {None, "shape"}

    assert codes == {
        "protocol_version",
        "segments_count",
        "truncated_count",
        "module_required",
        "out_of_scope_reason",
        "segment_span",
        "segment_span_not_in_message",
        "segment_order",
        "reference_span_unexpected",
        "reference_span",
        "reference_span_not_in_segment",
        "context_spans_unexpected",
        "context_spans_count",
        "context_span",
        "context_span_not_in_message",
        "context_span_duplicate",
    }


# ---- Requests ----


@pytest.mark.parametrize(("module", "case"), cases("request.cases.json"))
def test_every_request_case_is_accepted_or_refused_as_the_contract_says(module, case):
    try:
        REQUESTS[module].model_validate(case["request"])
        outcome = None
    except ValidationError:
        outcome = "invalid_request"

    assert outcome == case["violation"]


RULES_BEYOND_THE_SCHEMA = {
    "open_drafts_kind_unique",
    "open_drafts_total",
    "awaiting_not_open",
    "awaiting_not_missing",
    "draft_on_create",
    "draft_missing_on_correct",
}


@pytest.mark.parametrize(
    ("module", "case"),
    cases("request.cases.json", lambda case: case["rule"] in RULES_BEYOND_THE_SCHEMA),
)
def test_a_request_rule_beyond_the_schema_is_refused_for_that_rule(module, case):
    with pytest.raises(ValidationError) as refused:
        REQUESTS[module].model_validate(case["request"])

    assert case["rule"] in str(refused.value)


@pytest.mark.parametrize("module", MODULES)
def test_the_example_request_is_accepted_exactly_as_written(module):
    example = load(module, "request.example.json")

    assert REQUESTS[module].model_validate(example).model_dump(mode="json") == example


@pytest.mark.parametrize("module", MODULES)
def test_the_requests_carry_no_identifier_value_or_history(module):
    def names(node) -> set[str]:
        found = set()
        if isinstance(node, dict):
            found |= set(node.get("properties", {}))
            for child in node.values():
                found |= names(child)
        elif isinstance(node, list):
            for child in node:
                found |= names(child)
        return found

    fields = names(REQUESTS[module].model_json_schema())
    assert {"protocol_version", "context", "locale"} <= fields
    for name in fields:
        assert "id" not in name.split("_"), name
        assert name not in {"user", "values", "history", "message_id", "draft_id"}, name
    if module != "router":
        # A specialist never receives the whole message, other segments or other drafts.
        assert "message" not in fields and "open_drafts" not in fields


# ---- Strict schemas, versions and limits ----


@pytest.mark.parametrize("module", MODULES)
def test_the_strict_output_schema_is_the_contracts(module):
    assert load(module, "interpretation.schema.json") == SCHEMAS[module]


def test_the_specialist_sections_are_the_v2_sections_unchanged():
    v2 = schema_v2.INTERPRETATION_JSON_SCHEMA["properties"]

    assert specialists.FINANCE_JSON_SCHEMA["properties"]["finance"] is schema.FINANCE_SCHEMA
    assert specialists.HYDRATION_JSON_SCHEMA["properties"]["hydration"] is v2["hydration"]
    assert specialists.MEAL_JSON_SCHEMA["properties"]["meal"] is v2["meal"]
    assert specialists.FinanceInterpretation.model_fields["finance"].annotation is (
        schema.FinanceFields
    )
    assert specialists.HydrationInterpretation.model_fields["hydration"].annotation is (
        schema_v2.HydrationFields
    )
    assert specialists.MealInterpretation.model_fields["meal"].annotation is schema_v2.MealFields


@pytest.mark.parametrize("module", MODULES)
def test_versions_locale_and_errors_are_the_contracts(module):
    protocol = load(module, "protocol.json")
    v2 = json.loads(
        (CONTRACTS.parent / "assistant-interpretation/v2/protocol.json").read_text(encoding="utf-8")
    )

    assert protocol["protocol_version"] == common.PROTOCOL_VERSION
    assert protocol["output_version"] == common.OUTPUT_VERSION
    assert protocol["locale"] == common.LOCALE
    assert protocol["errors"] == v2["errors"]


def test_the_router_limits_are_the_contracts():
    limits = load("router", "protocol.json")["limits"]

    assert limits == {
        "message_length": schema.MAX_MESSAGE_LENGTH,
        "max_segments": common.MAX_SEGMENTS,
        "span_length": schema.MAX_SPAN_LENGTH,
        "context_spans_per_segment": common.MAX_CONTEXT_SPANS,
        "context_span_length": common.MAX_CONTEXT_SPAN_LENGTH,
        "reference_span_length": common.MAX_REFERENCE_SPAN_LENGTH,
        "max_open_drafts": common.MAX_OPEN_DRAFTS,
    }


@pytest.mark.parametrize("module", SPECIALISTS)
def test_the_specialist_limits_and_kind_are_the_contracts(module):
    protocol = load(module, "protocol.json")
    limits = protocol["limits"]

    assert limits["segment_length"] == schema.MAX_MESSAGE_LENGTH
    assert limits["context_spans"] == common.MAX_CONTEXT_SPANS
    assert limits["context_span_length"] == common.MAX_CONTEXT_SPAN_LENGTH
    assert limits["value_length"] == schema.MAX_VALUE_LENGTH
    assert limits["note_length"] == schema.MAX_NOTE_LENGTH
    assert limits["span_length"] == schema.MAX_SPAN_LENGTH
    assert limits["days_ago"] == schema.MAX_DAYS_AGO
    if module == "finance":
        assert limits["category_names_per_type"] == schema.MAX_CATEGORY_NAMES
        assert limits["category_name_length"] == schema.MAX_CATEGORY_NAME_LENGTH
    assert protocol["kind"] in common.KIND_FIELDS


def test_the_kinds_and_their_field_names_are_the_contracts():
    request = load("router", "request.schema.json")
    entries = request["properties"]["context"]["properties"]["open_drafts"]["items"]["oneOf"]

    assert {
        entry["properties"]["kind"]["const"]: tuple(
            entry["properties"]["missing_fields"]["items"]["enum"]
        )
        for entry in entries
    } == common.KIND_FIELDS


@pytest.mark.parametrize("module", MODULES)
def test_the_endpoint_is_the_contracts(module):
    from lifeos_ai.app import create_app
    from tests.test_api import SERVICE_KEY, FakeEstimator

    method, path = load(module, "protocol.json")["endpoint"].split(" ")
    app = create_app(FakeEstimator(), service_key=SERVICE_KEY)

    assert any(
        getattr(route, "path", None) == path and method in getattr(route, "methods", ())
        for route in app.routes
    )
