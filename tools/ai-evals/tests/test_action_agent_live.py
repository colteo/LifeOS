"""AI-003 live Action Agent adapter, offline: production reuse, drift, tool cycle,
telemetry."""

import json
from pathlib import Path

import httpx
import pytest
from lifeos_ai.action_agent import prompt as production_prompt
from lifeos_ai.action_agent.groq import GroqActionAgent
from lifeos_ai.action_agent.schema import ActionAgentStepRequest
from lifeos_ai.groq_chat import GroqSettings
from live_fakes import ScriptedTransport, no_sleep, tool_call, usage

from lifeos_ai_evals.core.engine import evaluate
from lifeos_ai_evals.core.experiments import ExperimentVariant
from lifeos_ai_evals.evaluators.action_agent import dotnet_mirror as net
from lifeos_ai_evals.evaluators.action_agent import live, plugin

ROOT = Path(__file__).resolve().parents[1]
CONTROL = ROOT / "experiments/action_agent/control-v1.json"
CANDIDATE = ROOT / "experiments/action_agent/candidate-model-gpt-oss-120b.json"
CANDIDATE_PROMPT = ROOT / "experiments/action_agent/candidate-prompt-v2.json"
OCT_EUR = {"year": 2026, "month": 10, "currency": "EUR"}
PROPOSAL = {**OCT_EUR, "proposed_amount": 500, "rationale": "You spent 380 of 400 EUR."}


def predictor(*replies, path=CONTROL):
    transport = ScriptedTransport(*replies)
    system = live.LiveActionAgentPredictor(
        ExperimentVariant.load(path),
        transport=transport,
        sleep=no_sleep,
        case_sleep=lambda _: None,
    )
    return system, transport


def subset(*ids):
    data = plugin.load(plugin.default_dataset())
    cases = tuple(c for c in data.cases if c.id in ids)
    return type(data)(data.name, data.version, cases, data.sha256)


def step_request(steps=(), max_steps=net.MAX_STEPS):
    return ActionAgentStepRequest.model_validate(
        {
            "tool_schema_version": net.TOOL_SCHEMA_VERSION,
            "context": {
                "current_month": "2026-10",
                "target_months": ["2026-10", "2026-11"],
            },
            "tools": net.READ_TOOLS,
            "steps": list(steps),
            "max_steps": max_steps,
        }
    )


def test_control_config_is_the_production_identity():
    variant = ExperimentVariant.load(CONTROL)
    assert variant.model == "openai/gpt-oss-20b"
    assert variant.prompt_version == production_prompt.PROMPT_VERSION
    assert variant.context_version == net.TOOL_SCHEMA_VERSION
    assert (
        live.PROMPTS[production_prompt.PROMPT_VERSION]
        is production_prompt.SYSTEM_PROMPT
    )


@pytest.mark.parametrize("final", [False, True])
def test_control_request_body_equals_the_production_body(final):
    system, _ = predictor()
    steps = [
        {"tool": "get_weekly_review", "arguments": {}, "result": {"currencies": []}}
    ]
    request = step_request(steps, max_steps=2 if final else net.MAX_STEPS)
    production = GroqActionAgent(GroqSettings(api_key="x"))
    assert system.agent().request_body(request) == production.request_body(request)


def test_candidate_model_changes_only_the_model():
    control, _ = predictor()
    candidate, _ = predictor(path=CANDIDATE)
    left = control.agent().request_body(step_request())
    right = candidate.agent().request_body(step_request())
    assert right.pop("model") == "openai/gpt-oss-120b"
    left.pop("model")
    assert left == right


def test_prompt_registry_is_the_production_registry():
    assert live.PROMPTS is production_prompt.PROMPTS
    assert set(live.PROMPTS) == {"action-agent-v1", "action-agent-v2"}


def test_candidate_prompt_config_changes_only_the_prompt_version():
    control = json.loads(CONTROL.read_text(encoding="utf-8"))
    candidate = json.loads(CANDIDATE_PROMPT.read_text(encoding="utf-8"))
    assert candidate.pop("prompt_version") == "action-agent-v2"
    assert control.pop("prompt_version") == "action-agent-v1"
    assert candidate == control
    assert candidate["model"] == "openai/gpt-oss-20b"


@pytest.mark.parametrize("final", [False, True])
def test_candidate_prompt_request_differs_only_by_system_prompt(final):
    steps = [
        {"tool": "get_weekly_review", "arguments": {}, "result": {"currencies": []}}
    ]
    request = step_request(steps, max_steps=2 if final else net.MAX_STEPS)
    control, _ = predictor()
    candidate, _ = predictor(path=CANDIDATE_PROMPT)
    control_agent, candidate_agent = control.agent(), candidate.agent()
    left = control_agent.request_body(request)
    right = candidate_agent.request_body(request)
    prompts = production_prompt.PROMPTS
    assert left["messages"][0]["content"] == prompts["action-agent-v1"]
    assert right["messages"][0]["content"] == prompts["action-agent-v2"]
    # Model, tools, tool choice, generation settings and every other message: identical.
    left["messages"][0] = right["messages"][0] = None
    assert left == right
    # Loop, tool schema, runtime and adapter are the same; production logs name v2.
    assert {
        key for key in control.configuration
        if control.configuration[key] != candidate.configuration[key]
    } == {"prompt_version"}  # fmt: skip
    assert candidate_agent.prompt_version == "action-agent-v2"
    assert control_agent.prompt_version == production_prompt.PROMPT_VERSION


def test_unregistered_prompt_version_is_rejected(tmp_path):
    data = json.loads(CONTROL.read_text(encoding="utf-8"))
    data["prompt_version"] = "action-agent-v3"
    path = tmp_path / "variant.json"
    path.write_text(json.dumps(data), encoding="utf-8")
    with pytest.raises(ValueError, match="unregistered prompt version"):
        live.LiveActionAgentPredictor(
            ExperimentVariant.load(path), transport=ScriptedTransport()
        )


def test_real_tool_cycle_read_result_terminal_with_telemetry():
    system, transport = predictor(
        tool_call("get_weekly_review", {}, tokens=usage(1500, 40)),
        httpx.Response(429, headers={"retry-after": "0"}),
        tool_call("get_budget_status", OCT_EUR, tokens=usage(1700, 50)),
        tool_call(
            "propose_monthly_budget_adjustment", PROPOSAL, tokens=usage(1900, 80)
        ),
    )
    result = evaluate(
        "action_agent", subset("overspent-with-recurring"), system, plugin.scorer()
    )
    case = result.cases[0]
    assert case.status == "correct"
    assert case.prediction.value.outcome == "proposed"
    # The model received the tool results LifeOS (mirror) executed, in production
    # message form.
    last = transport.bodies[-1]["messages"]
    assert [m["role"] for m in last[2:]] == ["assistant", "tool", "assistant", "tool"]
    assert json.loads(last[-1]["content"])["free_to_spend"] == -70
    assert transport.bodies[0]["tool_choice"] == "required"

    telemetry = system.experiment_metadata()["telemetry"]
    assert (
        telemetry["logical_requests"],
        telemetry["request_attempts"],
        telemetry["retries"],
    ) == (3, 4, 1)
    assert telemetry["token_usage"]["total_tokens"] == {"responses": 3, "total": 5270}
    assert telemetry["usage_coverage"]["rate"] == pytest.approx(3 / 4)


def test_invented_write_tool_is_rejected_by_production_and_recorded_by_name():
    system, _ = predictor(
        tool_call("get_weekly_review", {}),
        tool_call("set_monthly_budget", {**OCT_EUR, "amount": 99999}),
    )
    result = evaluate(
        "action_agent", subset("invented-tool-temptation"), system, plugin.scorer()
    )
    trace = result.cases[0].prediction.value
    assert (trace.outcome, trace.error_code) == ("failed", net.INVALID_OUTPUT)
    assert trace.emitted_tool_names == (("get_weekly_review",), ("set_monthly_budget",))
    metrics = result.cases[0].score.metrics
    assert (
        metrics["invented_tool_calls"],
        metrics["write_tool_calls"],
        metrics["safe"],
    ) == (1, 1, 0)


def test_unavailable_provider_is_an_execution_error_not_a_scored_run():
    system, _ = predictor(httpx.Response(401))
    result = evaluate("action_agent", subset("no-budget-set"), system, plugin.scorer())
    assert result.errors == 1
    assert result.cases[0].error == {
        "stage": "prediction",
        "type": "ProviderUnavailable",
    }
    assert result.aggregate_metrics["terminal_decision_accuracy"] is None


def test_live_runs_require_the_environment_key(monkeypatch):
    monkeypatch.delenv("GROQ_API_KEY", raising=False)
    with pytest.raises(ValueError, match="GROQ_API_KEY"):
        plugin.experiment(CONTROL)


def test_wrong_tool_schema_version_is_rejected(tmp_path):
    data = json.loads(CONTROL.read_text(encoding="utf-8"))
    data["context_version"] = "action-agent-tools-v2"
    path = tmp_path / "variant.json"
    path.write_text(json.dumps(data), encoding="utf-8")
    with pytest.raises(ValueError, match="unsupported tool schema version"):
        plugin.experiment(path)
