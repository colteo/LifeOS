"""AI-007 F3: POST /v1/assistant/interpret through the real app (fake interpreter, offline)."""

import logging

import httpx
import pytest
from fastapi.testclient import TestClient

from lifeos_ai.app import create_app
from lifeos_ai.assistant.interpreter import (
    DisabledInterpreter,
    InterpretationFailed,
    InterpretationRefused,
    ProviderUnavailable,
)
from lifeos_ai.assistant.schema import AssistantInterpretation, InterpretRequest
from lifeos_ai.config import ProviderSelectionError, assistant_interpreter_from_environment
from tests.assistant_fixtures import EXPENSE, MESSAGE, expense, request
from tests.test_api import SERVICE_KEY, FakeEstimator

INTERPRET_PATH = "/v1/assistant/interpret"


class FakeAssistant:
    """Scripted: returns the given interpretation or raises the given failure. Reads no text."""

    provider = "fake"
    model = "fake-model"
    prompt_version = "assistant-finance-interpret-v1"
    configured = True

    def __init__(self, outcome=None):
        self.outcome = EXPENSE if outcome is None else outcome
        self.requests: list[InterpretRequest] = []
        self.closed = False

    async def interpret(self, request):
        self.requests.append(request)
        if isinstance(self.outcome, Exception):
            raise self.outcome
        return AssistantInterpretation.model_validate(self.outcome)

    async def aclose(self):
        self.closed = True


def client(assistant, *, authorized=True):
    headers = {"Authorization": f"Bearer {SERVICE_KEY}"} if authorized else {}
    return TestClient(
        create_app(FakeEstimator(), assistant=assistant, service_key=SERVICE_KEY),
        headers=headers,
    )


def test_interpret_returns_the_versioned_attributed_interpretation():
    fake = FakeAssistant()

    with client(fake) as http:
        response = http.post(INTERPRET_PATH, json=request())

    assert response.status_code == 200
    assert response.json() == {
        "output_version": 1,
        "provider": "fake",
        "model": "fake-model",
        "prompt_version": "assistant-finance-interpret-v1",
        "interpretation": EXPENSE,
    }
    assert fake.requests == [InterpretRequest.model_validate(request())]
    assert fake.closed


def test_an_interpretation_breaking_the_protocol_never_leaves_the_service():
    fake = FakeAssistant(expense(amount={"state": "stated", "value": "7", "span": "7 euro"}))

    with client(fake) as http:
        response = http.post(INTERPRET_PATH, json=request())

    assert (response.status_code, response.json()) == (
        502,
        {"error": {"code": "interpretation_failed"}},
    )


@pytest.mark.parametrize(
    ("failure", "status", "code"),
    [
        (InterpretationFailed("malformed"), 502, "interpretation_failed"),
        (InterpretationRefused("refused"), 502, "interpretation_refused"),
        (ProviderUnavailable("timeout"), 503, "provider_unavailable"),
        (RuntimeError("boom"), 500, "internal_error"),
    ],
)
def test_failures_are_stable_codes_only(failure, status, code):
    with client(FakeAssistant(failure)) as http:
        response = http.post(INTERPRET_PATH, json=request())

    assert (response.status_code, response.json()) == (status, {"error": {"code": code}})


def test_an_invalid_request_names_fields_only_never_the_message():
    with client(FakeAssistant()) as http:
        response = http.post(INTERPRET_PATH, json=request(account_id="secret-id"))

    assert response.status_code == 422
    assert response.json()["error"]["code"] == "invalid_request"
    assert MESSAGE not in response.text and "secret-id" not in response.text


def test_the_route_requires_the_service_key():
    fake = FakeAssistant()

    with client(fake, authorized=False) as http:
        response = http.post(INTERPRET_PATH, json=request())

    assert response.status_code == 401
    assert fake.requests == []


def test_logs_carry_the_outcome_and_identity_but_never_the_message_or_output(caplog):
    with caplog.at_level(logging.INFO, logger="lifeos_ai"), client(FakeAssistant()) as http:
        http.post(INTERPRET_PATH, json=request())

    assert "outcome=ok" in caplog.text and "provider=fake" in caplog.text
    for secret in ("Pamela", "colazione", "Ristoranti", "5 euro"):
        assert secret not in caplog.text


def test_an_unexpected_failure_logs_its_type_only_never_its_text(caplog):
    leaky = RuntimeError(f"could not handle {MESSAGE!r}")

    with caplog.at_level(logging.INFO, logger="lifeos_ai"), client(FakeAssistant(leaky)) as http:
        response = http.post(INTERPRET_PATH, json=request())

    assert (response.status_code, response.json()) == (500, {"error": {"code": "internal_error"}})
    assert "RuntimeError" in caplog.text and "outcome=internal_error" in caplog.text
    assert "Pamela" not in caplog.text and "Traceback" not in caplog.text


# ---- Default configuration: no provider, no traffic ----


def test_the_default_configuration_selects_the_disabled_interpreter():
    interpreter = assistant_interpreter_from_environment({})

    assert isinstance(interpreter, DisabledInterpreter)
    assert interpreter.configured is False


@pytest.mark.parametrize("provider", ["openai", "groq", "fake"])
def test_no_provider_can_be_selected_before_adr_014_is_accepted(provider):
    with pytest.raises(ProviderSelectionError):
        assistant_interpreter_from_environment({"LIFEOS_AI_ASSISTANT_PROVIDER": provider})


def test_the_default_service_answers_unavailable_without_any_network_call(monkeypatch):
    # Even with provider keys in the environment, nothing may be sent for the assistant.
    monkeypatch.setenv("OPENAI_API_KEY", "sk-test-not-a-real-key")
    monkeypatch.delenv("LIFEOS_AI_ASSISTANT_PROVIDER", raising=False)

    async def no_network(*args, **kwargs):
        raise AssertionError("the assistant must not make network calls")

    monkeypatch.setattr(httpx.AsyncClient, "send", no_network)

    app = create_app(FakeEstimator(), service_key=SERVICE_KEY)
    with TestClient(app, headers={"Authorization": f"Bearer {SERVICE_KEY}"}) as http:
        response = http.post(INTERPRET_PATH, json=request())
        health = http.get("/health").json()["assistant"]

    assert (response.status_code, response.json()) == (
        503,
        {"error": {"code": "provider_unavailable"}},
    )
    assert health == {
        "provider": "disabled",
        "model": "-",
        "prompt_version": "assistant-finance-interpret-v1",
        "configured": False,
    }
