"""AI-002: the Action Agent step contract (request bounds, strict final proposal schema)."""

import pytest
from pydantic import ValidationError

from lifeos_ai.action_agent.schema import (
    FINISH_TOOL,
    MAX_RESULT_CHARS,
    PROPOSE_TOOL,
    TERMINAL_TOOL_DEFINITIONS,
    ActionAgentStepRequest,
    ActionAgentStepResponse,
    BudgetAdjustmentProposal,
    FinishWithoutProposal,
)
from tests.action_agent_fixtures import (
    BUDGET_STEP,
    PROPOSAL,
    READ_TOOLS,
    REVIEW_STEP,
    SCENARIOS,
    request,
    step,
)

# ---- Request ----


def test_a_typical_request_is_valid():
    parsed = ActionAgentStepRequest.model_validate(request([REVIEW_STEP, BUDGET_STEP]))

    assert [tool.name for tool in parsed.tools] == ["get_weekly_review", "get_budget_status"]
    assert len(parsed.steps) == 2
    assert not parsed.is_final_step


def test_the_last_allowed_step_is_final():
    assert ActionAgentStepRequest.model_validate(
        request([REVIEW_STEP, BUDGET_STEP], max_steps=3)
    ).is_final_step


@pytest.mark.parametrize(
    "change",
    [
        {"user_id": "u-1"},
        {"max_steps": 7},
        {"max_steps": 0},
        {"max_steps": "5"},
        {"tools": []},
        {"tools": READ_TOOLS * 3},
        {"tools": [READ_TOOLS[0], READ_TOOLS[0]]},
        {"tools": [{**READ_TOOLS[0], "name": PROPOSE_TOOL}]},
        {"tools": [{**READ_TOOLS[0], "name": "Run SQL"}]},
        {"tools": [{**READ_TOOLS[0], "parameters": {"type": "array"}}]},
        {"context": {"current_month": "2026-13", "target_months": ["2026-10"]}},
        {"context": {"current_month": "2026-10", "target_months": []}},
        {"tool_schema_version": ""},
    ],
)
def test_invalid_requests_are_rejected(change):
    with pytest.raises(ValidationError):
        ActionAgentStepRequest.model_validate(request() | change)


def test_steps_must_use_offered_tools_and_leave_a_step():
    with pytest.raises(ValidationError):
        ActionAgentStepRequest.model_validate(request([step("run_sql", {}, {})]))
    with pytest.raises(ValidationError):
        ActionAgentStepRequest.model_validate(request([REVIEW_STEP, BUDGET_STEP], max_steps=2))


def test_tool_results_are_bounded():
    huge = step("get_weekly_review", {}, {"blob": "x" * MAX_RESULT_CHARS})

    with pytest.raises(ValidationError):
        ActionAgentStepRequest.model_validate(request([huge]))


# ---- Terminal tools (the strict final schema) ----


def test_a_valid_proposal():
    proposal = BudgetAdjustmentProposal.model_validate(PROPOSAL)

    assert (proposal.year, proposal.month, proposal.currency, proposal.proposed_amount) == (
        2026,
        10,
        "EUR",
        500,
    )


@pytest.mark.parametrize(
    "change",
    [
        {"proposed_amount": 0},
        {"proposed_amount": -10},
        {"proposed_amount": "500"},
        {"proposed_amount": True},
        {"proposed_amount": 500.123},
        {"proposed_amount": 1e13},
        {"month": 13},
        {"month": "10"},
        {"year": 1999},
        {"currency": "eur"},
        {"currency": "EURO"},
        {"rationale": ""},
        {"rationale": "Two\nlines."},
        {"rationale": "x" * 301},
        {"category": "Food"},
        {"amount": 500},
    ],
)
def test_invalid_proposals_are_rejected_whole(change):
    with pytest.raises(ValidationError):
        BudgetAdjustmentProposal.model_validate(PROPOSAL | change)


def test_a_proposal_needs_every_field():
    for field in PROPOSAL:
        data = dict(PROPOSAL)
        del data[field]
        with pytest.raises(ValidationError):
            BudgetAdjustmentProposal.model_validate(data)


def test_finish_requires_a_short_plain_reason():
    assert FinishWithoutProposal.model_validate({"reason": " Fine. "}).reason == "Fine."
    with pytest.raises(ValidationError):
        FinishWithoutProposal.model_validate({"reason": ""})
    with pytest.raises(ValidationError):
        FinishWithoutProposal.model_validate({"reason": "ok", "proposal": {}})


def test_terminal_tool_schemas_match_the_pydantic_models():
    by_name = {tool["name"]: tool["parameters"] for tool in TERMINAL_TOOL_DEFINITIONS}

    assert set(by_name) == {PROPOSE_TOOL, FINISH_TOOL}
    assert set(by_name[PROPOSE_TOOL]["required"]) == set(BudgetAdjustmentProposal.model_fields)
    assert set(by_name[FINISH_TOOL]["required"]) == set(FinishWithoutProposal.model_fields)
    assert all(schema["additionalProperties"] is False for schema in by_name.values())


# ---- Response ----


def test_the_response_is_a_discriminated_decision():
    response = ActionAgentStepResponse.model_validate(
        {
            "output_version": 1,
            "provider": "groq",
            "model": "m",
            "prompt_version": "action-agent-v1",
            "decision": {"type": "propose_budget_adjustment", "proposal": PROPOSAL},
        }
    )

    assert response.decision.type == "propose_budget_adjustment"
    with pytest.raises(ValidationError):
        ActionAgentStepResponse.model_validate(
            response.model_dump() | {"decision": {"type": "execute", "tool": "x"}}
        )


def test_scenarios_are_labelled_and_synthetic():
    assert {scenario["expected"] for scenario in SCENARIOS} == {"propose", "no_action"}
    assert len({scenario["id"] for scenario in SCENARIOS}) == len(SCENARIOS)
