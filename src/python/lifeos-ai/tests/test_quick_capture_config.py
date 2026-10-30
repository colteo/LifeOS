"""AI-009 I1: Quick Capture is disabled by default and cannot be enabled by configuration in any
environment; its package interprets only. Offline."""

import ast
import asyncio
from pathlib import Path

import pytest

from lifeos_ai.app import create_app
from lifeos_ai.config import (
    ProviderSelectionError,
    QuickCaptureNotApprovedError,
    quick_capture_interpreter_from_environment,
)
from lifeos_ai.errors import ProviderUnavailable
from lifeos_ai.quick_capture import router, specialists
from lifeos_ai.quick_capture.interpreter import DisabledQuickCaptureInterpreter
from lifeos_ai.quick_capture.prompt import DEFAULT_PROMPTS
from tests.quick_capture_fixtures import request
from tests.test_api import SERVICE_KEY, FakeEstimator

VARIABLE = "LIFEOS_AI_QUICK_CAPTURE_PROVIDER"
# Every setting the Assistant's OpenAI adapter would need: still not enough to enable Quick Capture.
OPENAI_ENVIRONMENT = {
    "LIFEOS_AI_ASSISTANT_OPENAI_API_KEY": "test-assistant-key-not-real",
    "LIFEOS_AI_ASSISTANT_OPENAI_REGION": "eu",
    "LIFEOS_AI_ASSISTANT_OPENAI_DAILY_BUDGET_USD": "1",
    "LIFEOS_AI_ASSISTANT_OPENAI_MONTHLY_BUDGET_USD": "10",
    "LIFEOS_AI_ASSISTANT_OPENAI_INPUT_PRICE_PER_MTOK_USD": "1",
    "LIFEOS_AI_ASSISTANT_OPENAI_OUTPUT_PRICE_PER_MTOK_USD": "1",
}
PACKAGE = Path(__file__).resolve().parents[1] / "src" / "lifeos_ai" / "quick_capture"


@pytest.mark.parametrize("environ", [{}, {VARIABLE: ""}, {VARIABLE: " Disabled "}])
def test_the_default_and_the_only_accepted_value_is_disabled(environ):
    interpreter = quick_capture_interpreter_from_environment(environ)

    assert isinstance(interpreter, DisabledQuickCaptureInterpreter)
    assert (interpreter.provider, interpreter.model, interpreter.configured) == (
        "disabled",
        "-",
        False,
    )
    assert interpreter.prompt_versions == DEFAULT_PROMPTS


@pytest.mark.parametrize("value", ["openai", "OpenAI"])
def test_openai_is_refused_at_startup_even_when_fully_configured(value):
    with pytest.raises(QuickCaptureNotApprovedError) as refused:
        quick_capture_interpreter_from_environment({**OPENAI_ENVIRONMENT, VARIABLE: value})

    assert "not approved" in str(refused.value)
    assert "test-assistant-key-not-real" not in str(refused.value)


@pytest.mark.parametrize("value", ["groq", "gemini", "enabled", "true"])
def test_an_unknown_provider_stops_the_service(value):
    with pytest.raises(ProviderSelectionError):
        quick_capture_interpreter_from_environment({VARIABLE: value})


def test_the_service_does_not_start_with_quick_capture_selected(monkeypatch):
    monkeypatch.setenv(VARIABLE, "openai")

    with pytest.raises(QuickCaptureNotApprovedError):
        create_app(FakeEstimator(), service_key=SERVICE_KEY)


def test_the_disabled_interpreter_never_answers():
    interpreter = DisabledQuickCaptureInterpreter()
    route = router.RouteRequest.model_validate(request("router"))
    meal = specialists.MealInterpretRequest.model_validate(request("meal"))

    with pytest.raises(ProviderUnavailable):
        asyncio.run(interpreter.route(route))
    with pytest.raises(ProviderUnavailable):
        asyncio.run(interpreter.interpret(meal))


# ---- Boundaries ----


def imports(path: Path) -> set[str]:
    found = set()
    for node in ast.walk(ast.parse(path.read_text(encoding="utf-8"))):
        if isinstance(node, ast.Import):
            found.update(alias.name for alias in node.names)
        elif isinstance(node, ast.ImportFrom) and node.module:
            found.add(node.module)
    return found


def test_the_package_interprets_only_and_reuses_the_assistant_transport():
    sources = sorted(PACKAGE.glob("*.py"))
    assert {path.name for path in sources} == {
        "__init__.py",
        "common.py",
        "interpreter.py",
        "openai_interpreter.py",
        "prompt.py",
        "router.py",
        "specialists.py",
    }
    for path in sources:
        modules = imports(path)
        # No own HTTP client, no other capability (Nutrition stays untouched), no framework.
        assert "httpx" not in modules, path
        assert not any(m.startswith("lifeos_ai.nutrition") for m in modules), path
        assert not any(
            m.startswith(("lifeos_ai.action_agent", "lifeos_ai.weekly_review")) for m in modules
        ), path
        assert not any(m.split(".")[0] in {"langchain", "langgraph", "openai"} for m in modules)
        # Only the adapter knows the provider.
        if path.name != "openai_interpreter.py":
            assert not any("openai" in m for m in modules), path


@pytest.mark.parametrize(
    "model",
    [
        router.RouteRequest,
        router.RouteContext,
        router.RouterOutput,
        router.Segment,
        specialists.FinanceInterpretRequest,
        specialists.HydrationInterpretRequest,
        specialists.MealInterpretRequest,
        specialists.FinanceDraft,
        specialists.FinanceInterpretation,
        specialists.HydrationInterpretation,
        specialists.MealInterpretation,
    ],
)
def test_every_model_refuses_unknown_members_and_coercion(model):
    assert model.model_config.get("extra") == "forbid"
    assert model.model_config.get("strict") is True
