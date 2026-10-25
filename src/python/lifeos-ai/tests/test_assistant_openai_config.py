"""AI-007 F3-B1: fail-closed configuration of the assistant's OpenAI adapter (offline, no key).

The provider stays NOT selectable while ADR-014 is PROPOSED; the settings parser is exercised
directly, and the wiring only by patching the approved-provider list inside a test.
"""

from decimal import Decimal

import httpx
import pytest
from fastapi.testclient import TestClient

from lifeos_ai import config
from lifeos_ai.app import create_app
from lifeos_ai.assistant.interpreter import DisabledInterpreter
from lifeos_ai.assistant.openai_interpreter import OpenAIAssistantInterpreter
from lifeos_ai.config import (
    AssistantConfigurationError,
    ProviderSelectionError,
    assistant_interpreter_from_environment,
    openai_assistant_settings_from_environment,
)
from tests.assistant_fixtures import request
from tests.test_api import SERVICE_KEY, FakeEstimator

FAKE_KEY = "sk-test-assistant-not-a-real-key"

COMPLETE = {
    "LIFEOS_AI_ASSISTANT_OPENAI_API_KEY": FAKE_KEY,
    "LIFEOS_AI_ASSISTANT_OPENAI_MODEL": "gpt-test-model",
    "LIFEOS_AI_ASSISTANT_OPENAI_REGION": "eu",
    "LIFEOS_AI_ASSISTANT_OPENAI_DAILY_BUDGET_USD": "0.50",
    "LIFEOS_AI_ASSISTANT_OPENAI_MONTHLY_BUDGET_USD": "5",
    "LIFEOS_AI_ASSISTANT_OPENAI_INPUT_PRICE_PER_MTOK_USD": "0.125",
    "LIFEOS_AI_ASSISTANT_OPENAI_OUTPUT_PRICE_PER_MTOK_USD": "0.50",
}
REQUIRED = list(COMPLETE)


def without(*names, **changes) -> dict:
    environ = {k: v for k, v in COMPLETE.items() if k not in names}
    environ.update(changes)
    return environ


# ---- Selection: disabled by default, OpenAI prepared but not selectable ----


def test_the_default_is_disabled_and_needs_no_key():
    assert isinstance(assistant_interpreter_from_environment({}), DisabledInterpreter)
    assert config.ASSISTANT_PROVIDERS == ("disabled",)


@pytest.mark.parametrize("value", ["openai", "OpenAI", " openai "])
def test_openai_is_refused_even_when_completely_configured(value):
    environ = {**COMPLETE, "LIFEOS_AI_ASSISTANT_PROVIDER": value}

    with pytest.raises(ProviderSelectionError) as failure:
        assistant_interpreter_from_environment(environ)

    assert "not approved" in str(failure.value) and "ADR-014" in str(failure.value)
    assert FAKE_KEY not in str(failure.value)


def test_groq_is_never_a_fallback():
    with pytest.raises(ProviderSelectionError):
        assistant_interpreter_from_environment({"LIFEOS_AI_ASSISTANT_PROVIDER": "groq"})


def test_the_service_starts_offline_with_openai_variables_present_and_sends_nothing(monkeypatch):
    for name, value in COMPLETE.items():
        monkeypatch.setenv(name, value)
    monkeypatch.delenv("LIFEOS_AI_ASSISTANT_PROVIDER", raising=False)

    async def no_network(*args, **kwargs):
        raise AssertionError("no network call is allowed")

    monkeypatch.setattr(httpx.AsyncClient, "send", no_network)

    app = create_app(FakeEstimator(), service_key=SERVICE_KEY)
    with TestClient(app, headers={"Authorization": f"Bearer {SERVICE_KEY}"}) as http:
        response = http.post("/v1/assistant/interpret", json=request())
        health = http.get("/health").json()["assistant"]

    assert response.status_code == 503
    assert health["provider"] == "disabled" and health["configured"] is False


# ---- Settings: complete, typed, nothing guessed ----


def test_a_complete_configuration_is_parsed_with_safe_defaults():
    settings = openai_assistant_settings_from_environment(COMPLETE)

    assert settings.model == "gpt-test-model"
    assert settings.base_url == "https://eu.api.openai.com/v1"
    assert (settings.daily_budget_usd, settings.monthly_budget_usd) == (Decimal("0.50"), 5)
    assert settings.prices.input_per_mtok == Decimal("0.125")
    assert settings.reasoning_effort == "low"
    assert (settings.timeout_seconds, settings.deadline_seconds) == (20.0, 40.0)
    assert (settings.max_attempts, settings.max_output_tokens, settings.max_concurrency) == (
        2,
        2000,
        2,
    )
    assert FAKE_KEY not in repr(settings)


def test_the_global_region_uses_the_default_host():
    environ = {**COMPLETE, "LIFEOS_AI_ASSISTANT_OPENAI_REGION": "global"}

    assert openai_assistant_settings_from_environment(environ).base_url == (
        "https://api.openai.com/v1"
    )


@pytest.mark.parametrize("missing", REQUIRED)
def test_every_required_setting_missing_is_an_explicit_error(missing):
    with pytest.raises(AssistantConfigurationError) as failure:
        openai_assistant_settings_from_environment(without(missing))

    assert missing in str(failure.value)


def test_the_journal_key_is_never_reused():
    environ = without("LIFEOS_AI_ASSISTANT_OPENAI_API_KEY", OPENAI_API_KEY=FAKE_KEY)

    with pytest.raises(AssistantConfigurationError):
        openai_assistant_settings_from_environment(environ)


@pytest.mark.parametrize(
    ("name", "value"),
    [
        ("LIFEOS_AI_ASSISTANT_OPENAI_API_KEY", "sk with spaces"),
        ("LIFEOS_AI_ASSISTANT_OPENAI_MODEL", "gpt model/../x"),
        ("LIFEOS_AI_ASSISTANT_OPENAI_REGION", "us-west"),
        ("LIFEOS_AI_ASSISTANT_OPENAI_REASONING_EFFORT", "extreme"),
        ("LIFEOS_AI_ASSISTANT_OPENAI_TIMEOUT_SECONDS", "0"),
        ("LIFEOS_AI_ASSISTANT_OPENAI_TIMEOUT_SECONDS", "nan"),
        ("LIFEOS_AI_ASSISTANT_OPENAI_DEADLINE_SECONDS", "75"),
        ("LIFEOS_AI_ASSISTANT_OPENAI_MAX_ATTEMPTS", "4"),
        ("LIFEOS_AI_ASSISTANT_OPENAI_MAX_ATTEMPTS", "1.5"),
        ("LIFEOS_AI_ASSISTANT_OPENAI_MAX_OUTPUT_TOKENS", "100000"),
        ("LIFEOS_AI_ASSISTANT_OPENAI_MAX_CONCURRENCY", "0"),
        ("LIFEOS_AI_ASSISTANT_OPENAI_DAILY_BUDGET_USD", "-1"),
        ("LIFEOS_AI_ASSISTANT_OPENAI_DAILY_BUDGET_USD", "Infinity"),
        ("LIFEOS_AI_ASSISTANT_OPENAI_MONTHLY_BUDGET_USD", "0.10"),
        ("LIFEOS_AI_ASSISTANT_OPENAI_INPUT_PRICE_PER_MTOK_USD", "free"),
        ("LIFEOS_AI_ASSISTANT_OPENAI_OUTPUT_PRICE_PER_MTOK_USD", "0"),
    ],
)
def test_an_invalid_value_is_an_explicit_error_naming_the_setting_never_the_value(name, value):
    with pytest.raises(AssistantConfigurationError) as failure:
        openai_assistant_settings_from_environment({**COMPLETE, name: value})

    assert "LIFEOS_AI_ASSISTANT_OPENAI_" in str(failure.value)
    assert value not in str(failure.value) or value in ("0", "4")  # bounds may be printed
    assert FAKE_KEY not in str(failure.value)


# ---- Wiring once approved (simulated in-test only) ----


def test_once_approved_an_incomplete_configuration_stops_the_service(monkeypatch):
    monkeypatch.setattr(config, "ASSISTANT_PROVIDERS", ("disabled", "openai"))
    environ = without("LIFEOS_AI_ASSISTANT_OPENAI_MODEL", LIFEOS_AI_ASSISTANT_PROVIDER="openai")

    with pytest.raises(AssistantConfigurationError):
        assistant_interpreter_from_environment(environ)


def test_once_approved_a_complete_configuration_builds_the_adapter_without_any_traffic(
    monkeypatch,
):
    monkeypatch.setattr(config, "ASSISTANT_PROVIDERS", ("disabled", "openai"))

    async def no_network(*args, **kwargs):
        raise AssertionError("building the adapter must not send anything")

    monkeypatch.setattr(httpx.AsyncClient, "send", no_network)

    interpreter = assistant_interpreter_from_environment(
        {**COMPLETE, "LIFEOS_AI_ASSISTANT_PROVIDER": "openai"}
    )

    assert isinstance(interpreter, OpenAIAssistantInterpreter)
    assert (interpreter.provider, interpreter.model) == ("openai", "gpt-test-model")
    assert interpreter.prompt_version == "assistant-finance-interpret-v1"
