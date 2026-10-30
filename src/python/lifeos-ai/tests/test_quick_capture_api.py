"""AI-009 I1: the four Quick Capture endpoints — authentication, validation, re-validation of the
interpreter's output, error mapping, logs. Offline: a scripted interpreter, synthetic data only."""

import logging

import pytest
from fastapi.testclient import TestClient

from lifeos_ai.app import create_app
from lifeos_ai.assistant.interpreter import InterpretationFailed, InterpretationRefused
from lifeos_ai.errors import ProviderUnavailable
from lifeos_ai.quick_capture.prompt import DEFAULT_PROMPTS
from lifeos_ai.quick_capture.router import RouterOutput
from lifeos_ai.quick_capture.specialists import (
    FinanceInterpretation,
    FinanceInterpretRequest,
    HydrationInterpretation,
    HydrationInterpretRequest,
    MealInterpretation,
)
from tests.assistant_fixtures import request as assistant_request
from tests.quick_capture_fixtures import PATHS, ROLES, answer, request
from tests.test_api import SERVICE_KEY, FakeEstimator
from tests.test_assistant_api import INTERPRET_PATH, FakeAssistant

_OUTPUTS = {
    FinanceInterpretRequest: FinanceInterpretation,
    HydrationInterpretRequest: HydrationInterpretation,
}


class FakeQuickCapture:
    """Scripted: answers with the given output (validated as the role's model) or raises."""

    provider = "fake"
    model = "fake-model"
    configured = True

    def __init__(self, outcome=None):
        self.outcome = outcome
        self.prompt_versions = dict(DEFAULT_PROMPTS)
        self.requests: list = []
        self.closed = False

    def _answer(self, request, output_type):
        self.requests.append(request)
        if isinstance(self.outcome, Exception):
            raise self.outcome
        if self.outcome is None or isinstance(self.outcome, dict):
            return output_type.model_validate(self.outcome)
        return self.outcome

    async def route(self, request):
        return self._answer(request, RouterOutput)

    async def interpret(self, request):
        return self._answer(request, _OUTPUTS.get(type(request), MealInterpretation))

    async def aclose(self):
        self.closed = True


def client(quick_capture=None, *, authorized=True, assistant=None):
    headers = {"Authorization": f"Bearer {SERVICE_KEY}"} if authorized else {}
    return TestClient(
        create_app(
            FakeEstimator(),
            assistant=assistant,
            quick_capture=quick_capture,
            service_key=SERVICE_KEY,
        ),
        headers=headers,
    )


@pytest.mark.parametrize("role", ROLES)
def test_a_valid_output_is_returned_versioned_and_attributed(role):
    fake = FakeQuickCapture(answer(role))

    with client(fake) as http:
        response = http.post(PATHS[role], json=request(role))

    assert response.status_code == 200
    assert response.json() == {
        "output_version": 1,
        "provider": "fake",
        "model": "fake-model",
        "prompt_version": DEFAULT_PROMPTS[role],
        "interpretation": answer(role),
    }
    assert [r.model_dump(mode="json") for r in fake.requests] == [request(role)]


@pytest.mark.parametrize("role", ROLES)
def test_by_default_the_pipeline_is_disabled_and_unavailable(role, monkeypatch):
    monkeypatch.delenv("LIFEOS_AI_QUICK_CAPTURE_PROVIDER", raising=False)

    with client() as http:
        response = http.post(PATHS[role], json=request(role))

    assert (response.status_code, response.json()) == (
        503,
        {"error": {"code": "provider_unavailable"}},
    )


@pytest.mark.parametrize("role", ROLES)
def test_without_the_service_key_nothing_is_read_or_interpreted(role):
    fake = FakeQuickCapture(answer(role))

    with client(fake, authorized=False) as http:
        response = http.post(PATHS[role], json=request(role))

    assert (response.status_code, response.json()) == (401, {"error": {"code": "unauthorized"}})
    assert fake.requests == []


@pytest.mark.parametrize("role", ROLES)
def test_an_invalid_request_is_refused_by_field_name_without_echoing_the_text(role):
    fake = FakeQuickCapture(answer(role))
    body = request(role)
    body["context"]["draft_id"] = "draft-secret-text"

    with client(fake) as http:
        response = http.post(PATHS[role], json=body)

    assert response.status_code == 422
    assert response.json()["error"]["code"] == "invalid_request"
    assert "draft-secret-text" not in response.text
    assert fake.requests == []


def test_a_request_rule_beyond_the_schema_is_an_invalid_request():
    body = request("router")
    body["context"]["awaiting"] = {"kind": "hydration.intake.create", "field": "amount"}

    with client(FakeQuickCapture(answer("router"))) as http:
        response = http.post(PATHS["router"], json=body)

    assert response.status_code == 422
    assert response.json()["error"]["code"] == "invalid_request"


def broken(role: str) -> dict:
    output = answer(role)
    if role == "router":
        output["segments"][1]["span"] = "ho bevuto un litro di vino"
    else:
        section = output[role]
        first = next(name for name, field in section.items() if field["state"] == "stated")
        section[first]["span"] = "parole che non sono nel segmento"
    return output


@pytest.mark.parametrize("role", ROLES)
def test_an_output_breaking_a_rule_is_interpretation_failed_and_never_partially_returned(role):
    with client(FakeQuickCapture(broken(role))) as http:
        response = http.post(PATHS[role], json=request(role))

    assert (response.status_code, response.json()) == (
        502,
        {"error": {"code": "interpretation_failed"}},
    )


@pytest.mark.parametrize("role", ["finance", "hydration", "meal"])
def test_an_output_of_another_role_is_interpretation_failed(role):
    other = RouterOutput.model_validate(answer("router"))

    with client(FakeQuickCapture(other)) as http:
        response = http.post(PATHS[role], json=request(role))

    assert response.status_code == 502
    assert response.json() == {"error": {"code": "interpretation_failed"}}


def test_a_specialist_output_on_the_router_endpoint_is_interpretation_failed():
    other = HydrationInterpretation.model_validate(answer("hydration"))

    with client(FakeQuickCapture(other)) as http:
        response = http.post(PATHS["router"], json=request("router"))

    assert response.json() == {"error": {"code": "interpretation_failed"}}


@pytest.mark.parametrize(
    ("failure", "status", "code"),
    [
        (InterpretationFailed(), 502, "interpretation_failed"),
        (InterpretationRefused(), 502, "interpretation_refused"),
        (ProviderUnavailable(), 503, "provider_unavailable"),
    ],
)
@pytest.mark.parametrize("role", ROLES)
def test_interpreter_failures_are_the_contracts_errors(role, failure, status, code, caplog):
    with (
        caplog.at_level(logging.INFO, logger="lifeos_ai"),
        client(FakeQuickCapture(failure)) as http,
    ):
        response = http.post(PATHS[role], json=request(role))

    assert (response.status_code, response.json()) == (status, {"error": {"code": code}})
    assert f"quick capture outcome={code} role={role} " in caplog.text


@pytest.mark.parametrize("role", ROLES)
def test_an_unexpected_failure_is_internal_error_and_its_text_is_never_logged(role, caplog):
    leak = RuntimeError("Ieri ho speso 12 euro per la pizza")

    with caplog.at_level(logging.INFO, logger="lifeos_ai"), client(FakeQuickCapture(leak)) as http:
        response = http.post(PATHS[role], json=request(role))

    assert (response.status_code, response.json()) == (500, {"error": {"code": "internal_error"}})
    assert "pizza" not in caplog.text
    assert "RuntimeError" in caplog.text


@pytest.mark.parametrize("role", ROLES)
def test_the_log_names_outcome_role_and_identity_but_no_user_text(role, caplog):
    with (
        caplog.at_level(logging.INFO, logger="lifeos_ai"),
        client(FakeQuickCapture(answer(role))) as http,
    ):
        http.post(PATHS[role], json=request(role))

    assert (
        f"quick capture outcome=ok role={role} provider=fake model=fake-model "
        f"prompt_version={DEFAULT_PROMPTS[role]}" in caplog.text
    )
    for text in ("pizza", "pomodoro", "Ristoranti", "300 ml", "Ieri"):
        assert text not in caplog.text


def test_the_interpreter_is_closed_on_shutdown():
    fake = FakeQuickCapture()

    with client(fake):
        pass

    assert fake.closed


def test_the_assistant_protocols_are_served_unchanged_next_to_quick_capture():
    with client(FakeQuickCapture(answer("router")), assistant=FakeAssistant()) as http:
        assistant = http.post(INTERPRET_PATH, json=assistant_request())
        routed = http.post(PATHS["router"], json=request("router"))

    assert assistant.status_code == 200 and assistant.json()["output_version"] == 1
    assert routed.status_code == 200
