"""AI-003 live Weekly Review adapter, offline: production reuse, drift, outcomes,
telemetry."""

import json
from pathlib import Path

import httpx
import pytest
from lifeos_ai.groq_chat import GroqSettings
from lifeos_ai.weekly_review import prompt as production_prompt
from lifeos_ai.weekly_review.groq import GroqWeeklyReviewInterpreter
from live_fakes import ScriptedTransport, content, no_sleep, usage

from lifeos_ai_evals.core.engine import evaluate
from lifeos_ai_evals.core.experiments import ExperimentVariant
from lifeos_ai_evals.evaluators.weekly_review import live, plugin

ROOT = Path(__file__).resolve().parents[1]
CONTROL = ROOT / "experiments/weekly_review/control-v1.json"
CANDIDATE = ROOT / "experiments/weekly_review/candidate-model-gpt-oss-120b.json"
VALID = {
    "summary": "You logged 11 meals and 3 workouts.",
    "wins": ["You completed 55 of 56 prescribed sets."],
    "attention": [],
    "patterns": [],
    "next_week_focus": [],
}


def predictor(*replies, path=CONTROL):
    transport = ScriptedTransport(*replies)
    system = live.LiveWeeklyReviewPredictor(
        ExperimentVariant.load(path),
        transport=transport,
        sleep=no_sleep,
        case_sleep=lambda _: None,
    )
    return system, transport


def first_case():
    return plugin.load(plugin.default_dataset()).cases[0]


def test_control_config_is_the_production_identity():
    variant = ExperimentVariant.load(CONTROL)
    assert variant.model == "openai/gpt-oss-20b"
    assert variant.prompt_version == production_prompt.PROMPT_VERSION
    # The registry references the production prompt object; nothing is copied.
    assert (
        live.PROMPTS[production_prompt.PROMPT_VERSION]
        is production_prompt.SYSTEM_PROMPT
    )


def test_control_request_body_equals_the_production_body_for_every_case():
    system, _ = predictor()
    production = GroqWeeklyReviewInterpreter(GroqSettings(api_key="x"))
    variant = system.interpreter()
    for case in plugin.load(plugin.default_dataset()).cases:
        assert variant.request_body(case.input) == production.request_body(
            case.input
        ), case.id


def test_candidate_model_changes_only_the_model():
    control, _ = predictor()
    candidate, _ = predictor(path=CANDIDATE)
    request = first_case().input
    left = control.interpreter().request_body(request)
    right = candidate.interpreter().request_body(request)
    assert right.pop("model") == "openai/gpt-oss-120b"
    left.pop("model")
    assert left == right


@pytest.mark.parametrize(
    ("field", "value", "message"),
    [
        ("provider", "openai", "provider adapter unavailable"),
        ("prompt_version", "weekly-review-insights-v9", "unregistered prompt"),
        ("context_version", "x", "unsupported context"),
        ("structured_output", "json_schema", "unsupported output contract"),
        ("generation_settings", {"temperature": 0}, "generation_settings requires"),
        (
            "generation_settings",
            {
                "temperature": 0,
                "max_completion_tokens": 10,
                "reasoning_effort": "low",
                "include_reasoning": True,
            },
            "include_reasoning",
        ),
    ],
)
def test_unsupported_variants_fail_without_fallback(tmp_path, field, value, message):
    data = json.loads(CONTROL.read_text(encoding="utf-8"))
    data[field] = value
    path = tmp_path / "variant.json"
    path.write_text(json.dumps(data), encoding="utf-8")
    with pytest.raises(ValueError, match=message):
        plugin.experiment(path)


def test_live_runs_require_the_environment_key(monkeypatch):
    monkeypatch.delenv("GROQ_API_KEY", raising=False)
    with pytest.raises(ValueError, match="GROQ_API_KEY"):
        plugin.experiment(CONTROL)


def test_outcomes_valid_invalid_and_unavailable_with_telemetry():
    replies = [
        content(VALID, tokens=usage(800, 100)),
        content(
            {
                "summary": "**markdown**",
                "wins": [],
                "attention": [],
                "patterns": [],
                "next_week_focus": [],
            }
        ),
        httpx.Response(429, headers={"retry-after": "0"}),
        content(VALID, tokens=usage(810, 90)),
        httpx.Response(503),
        httpx.Response(503),
        httpx.Response(503),
    ]
    system, transport = predictor(*replies)
    cases = plugin.load(plugin.default_dataset())
    subset = type(cases)(cases.name, cases.version, cases.cases[:4], cases.sha256)
    result = evaluate("weekly_review", subset, system, plugin.scorer())
    statuses = [case.status for case in result.cases]
    assert (
        statuses[1] == "incorrect" and result.cases[1].prediction.value.valid is False
    )
    assert statuses[3] == "error"
    assert result.cases[3].error == {
        "stage": "prediction",
        "type": "ProviderUnavailable",
    }
    assert result.aggregate_metrics["valid_output_rate"] == pytest.approx(2 / 3)
    # Every request carried the production structured-output contract.
    assert all(
        body["response_format"]["json_schema"]["strict"] for body in transport.bodies
    )

    telemetry = system.experiment_metadata()["telemetry"]
    assert telemetry["logical_requests"] == 4
    assert telemetry["request_attempts"] == 7
    assert telemetry["retries"] == 3
    assert telemetry["attempt_status_counts"] == {
        "http_200": 3,
        "http_429": 1,
        "http_503": 3,
    }
    assert telemetry["token_usage"]["input_tokens"] == {"responses": 2, "total": 1610}
    assert telemetry["token_usage"]["total_tokens"] == {"responses": 2, "total": 1800}
    assert telemetry["usage_coverage"]["attempts_with_usage"] == 2
    assert telemetry["latency_seconds"]["samples"] == 7
    assert telemetry["cost"] is None


def test_unreported_usage_is_null_never_zero():
    system, _ = predictor(content(VALID))
    system.predict(first_case().input)
    tokens = system.experiment_metadata()["telemetry"]["token_usage"]
    assert tokens["input_tokens"] == {"responses": 0, "total": None}
