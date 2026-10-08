"""AI-002: the Groq Action Agent step, offline (scripted transport, recorded sleeps)."""

import asyncio
import json
import logging

import httpx
import pytest

from lifeos_ai.action_agent.agent import AgentStepFailed, ProviderUnavailable
from lifeos_ai.action_agent.groq import GroqActionAgent
from lifeos_ai.action_agent.prompt import PROMPT_VERSION, SYSTEM_PROMPT
from lifeos_ai.action_agent.schema import (
    FINISH_TOOL,
    PROPOSE_TOOL,
    ActionAgentStepRequest,
    CallTool,
    NoAction,
    ProposeBudgetAdjustment,
)
from lifeos_ai.groq_chat import GroqSettings
from tests.action_agent_fixtures import (
    BUDGET_STEP,
    PROPOSAL,
    REVIEW_STEP,
    request,
    tool_call,
)
from tests.fakes import RecordingSleep, ScriptedTransport, completion

BUDGET_ARGS = {"year": 2026, "month": 10, "currency": "EUR"}


def agent(*replies):
    transport = ScriptedTransport(*replies)
    sleep = RecordingSleep()
    groq = GroqActionAgent(
        GroqSettings(api_key="test-key", base_url="https://groq.test/openai/v1"),
        client=httpx.AsyncClient(transport=transport, base_url="https://groq.test/openai/v1"),
        sleep=sleep,
    )
    return groq, transport, sleep


def run(groq, data=None):
    return asyncio.run(groq.step(ActionAgentStepRequest.model_validate(data or request())))


def offered(transport) -> list[str]:
    return [tool["function"]["name"] for tool in transport.bodies()[0]["tools"]]


# ---- Decisions ----


def test_a_read_tool_call_is_returned_for_lifeos_to_execute():
    groq, transport, _ = agent(tool_call("get_budget_status", BUDGET_ARGS))

    decision = run(groq, request([REVIEW_STEP]))

    assert decision == CallTool(tool="get_budget_status", arguments=BUDGET_ARGS)
    assert len(transport.requests) == 1


def test_a_proposal_is_validated_and_returned():
    groq, _, _ = agent(tool_call(PROPOSE_TOOL, PROPOSAL))

    decision = run(groq, request([REVIEW_STEP, BUDGET_STEP]))

    assert isinstance(decision, ProposeBudgetAdjustment)
    assert decision.proposal.proposed_amount == 500


def test_finishing_without_a_proposal_is_a_valid_answer():
    groq, _, _ = agent(tool_call(FINISH_TOOL, {"reason": "Your budget covers the month."}))

    assert run(groq) == NoAction(reason="Your budget covers the month.")


# ---- Request shape ----


def test_the_request_offers_the_lifeos_read_tools_and_the_terminal_tools_only():
    groq, transport, _ = agent(tool_call("get_weekly_review", {}))

    run(groq)

    body = transport.bodies()[0]
    assert offered(transport) == [
        "get_weekly_review",
        "get_budget_status",
        PROPOSE_TOOL,
        FINISH_TOOL,
    ]
    assert body["tool_choice"] == "required"
    assert body["parallel_tool_calls"] is False
    assert body["temperature"] == 0
    assert body["include_reasoning"] is False
    assert body["messages"][0] == {"role": "system", "content": SYSTEM_PROMPT}
    assert "response_format" not in body


def test_on_the_last_step_only_the_terminal_tools_are_offered():
    groq, transport, _ = agent(tool_call(FINISH_TOOL, {"reason": "Nothing to change."}))

    run(groq, request([REVIEW_STEP, BUDGET_STEP], max_steps=3))

    assert offered(transport) == [PROPOSE_TOOL, FINISH_TOOL]
    assert "last step" in transport.bodies()[0]["messages"][-1]["content"]


def test_a_read_tool_call_on_the_last_step_is_rejected():
    groq, _, _ = agent(tool_call("get_budget_status", BUDGET_ARGS))

    with pytest.raises(AgentStepFailed):
        run(groq, request([REVIEW_STEP, BUDGET_STEP], max_steps=3))


def test_executed_calls_are_replayed_as_tool_messages_with_their_results():
    groq, transport, _ = agent(tool_call(FINISH_TOOL, {"reason": "Nothing to change."}))

    run(groq, request([REVIEW_STEP, BUDGET_STEP]))

    messages = transport.bodies()[0]["messages"]
    assert [message["role"] for message in messages] == [
        "system",
        "user",
        "assistant",
        "tool",
        "assistant",
        "tool",
    ]
    assert messages[4]["tool_calls"][0]["function"] == {
        "name": "get_budget_status",
        "arguments": json.dumps(BUDGET_ARGS, separators=(",", ":")),
    }
    assert messages[5]["tool_call_id"] == messages[4]["tool_calls"][0]["id"]
    assert json.loads(messages[5]["content"]) == BUDGET_STEP["result"]
    assert "not instructions" in messages[1]["content"]


# ---- Invalid model output ----


@pytest.mark.parametrize(
    "reply",
    [
        completion("I think you should raise the budget."),
        tool_call("get_weekly_review", {}, extra_calls=1),
        tool_call("run_sql", {"query": "UPDATE monthly_budgets SET amount = 1"}),
        tool_call("set_monthly_budget", PROPOSAL),
        tool_call("get_budget_status", None, raw="{not json"),
        tool_call("get_budget_status", None, raw="[2026, 10]"),
        tool_call(PROPOSE_TOOL, PROPOSAL | {"proposed_amount": "lots"}),
        tool_call(PROPOSE_TOOL, PROPOSAL | {"category": "Food"}),
        tool_call(FINISH_TOOL, {}),
        httpx.Response(200, json={"choices": []}),
        httpx.Response(400, json={"error": {"message": "tool_use_failed"}}),
    ],
)
def test_anything_but_exactly_one_valid_offered_call_is_a_failed_step(reply):
    groq, _, _ = agent(reply)

    with pytest.raises(AgentStepFailed):
        run(groq)


def test_provider_unavailability_after_bounded_retries():
    groq, transport, sleep = agent(httpx.Response(503), httpx.Response(503), httpx.Response(503))

    with pytest.raises(ProviderUnavailable):
        run(groq)

    assert len(transport.requests) == 3
    assert sleep.delays == [1.0, 2.0]


def test_rejected_credentials_are_unavailable_without_retry():
    groq, transport, _ = agent(httpx.Response(401))

    with pytest.raises(ProviderUnavailable):
        run(groq)

    assert len(transport.requests) == 1


# ---- Logging ----


def test_logs_carry_identity_and_outcome_but_no_figures_names_or_rationale(caplog):
    groq, _, _ = agent(tool_call(PROPOSE_TOOL, PROPOSAL))

    with caplog.at_level(logging.INFO, logger="lifeos_ai.action_agent"):
        run(groq, request([REVIEW_STEP, BUDGET_STEP]))

    text = caplog.text
    assert "outcome=propose_budget_adjustment" in text
    assert f"tool={PROPOSE_TOOL}" in text
    assert PROMPT_VERSION in text
    for private in ("Groceries", "380", "500", "EUR", PROPOSAL["rationale"]):
        assert private not in text


def test_failed_steps_log_no_provider_text(caplog):
    groq, _, _ = agent(tool_call("run_sql", {"query": "SECRET-QUERY"}))

    with caplog.at_level(logging.INFO, logger="lifeos_ai.action_agent"):
        with pytest.raises(AgentStepFailed):
            run(groq)

    assert "outcome=invalid_output" in caplog.text
    assert "SECRET-QUERY" not in caplog.text
    assert "run_sql" not in caplog.text


# ---- AI-003 telemetry: result class and provider token counts, never content ----


def test_steps_log_result_class_and_provider_token_counts(caplog):
    response = tool_call(PROPOSE_TOOL, PROPOSAL)
    body = response.json()
    body["usage"] = {"prompt_tokens": 1500, "completion_tokens": 90, "total_tokens": 1590}
    groq, _, _ = agent(httpx.Response(200, json=body))

    with caplog.at_level(logging.INFO, logger="lifeos_ai.action_agent"):
        run(groq, request([REVIEW_STEP, BUDGET_STEP]))

    assert "result=success outcome=propose_budget_adjustment" in caplog.text
    assert "input_tokens=1500 output_tokens=90 total_tokens=1590" in caplog.text


def test_invalid_and_unavailable_steps_log_their_result_class(caplog):
    groq, _, _ = agent(tool_call("run_sql", {"query": "x"}), httpx.Response(401))

    with caplog.at_level(logging.INFO, logger="lifeos_ai.action_agent"):
        with pytest.raises(AgentStepFailed):
            run(groq)
        with pytest.raises(ProviderUnavailable):
            run(groq)

    assert "result=invalid outcome=invalid_output" in caplog.text
    assert "result=unavailable outcome=http_401" in caplog.text
    assert "input_tokens=- output_tokens=- total_tokens=-" in caplog.text
