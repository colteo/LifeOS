"""AI-002: POST /v1/action-agent/step through the real FastAPI app (fake agent model)."""

from fastapi.testclient import TestClient

from lifeos_ai.action_agent.agent import AgentStepFailed, ProviderUnavailable
from lifeos_ai.action_agent.schema import (
    ActionAgentStepRequest,
    BudgetAdjustmentProposal,
    CallTool,
    NoAction,
    ProposeBudgetAdjustment,
)
from lifeos_ai.app import create_app
from lifeos_ai.config import action_agent_from_environment
from tests.action_agent_fixtures import BUDGET_STEP, PROPOSAL, REVIEW_STEP, request
from tests.test_api import SERVICE_KEY, FakeEstimator
from tests.test_weekly_review_api import FakeInterpreter

STEP_PATH = "/v1/action-agent/step"


class FakeAgent:
    provider = "fake"
    model = "fake-model"
    prompt_version = "action-agent-v1"
    configured = True

    def __init__(self, outcome=None):
        self.outcome = outcome or CallTool(tool="get_weekly_review", arguments={})
        self.requests: list[ActionAgentStepRequest] = []
        self.closed = False

    async def step(self, request):
        self.requests.append(request)
        if isinstance(self.outcome, Exception):
            raise self.outcome
        return self.outcome

    async def aclose(self):
        self.closed = True


def client(agent, *, authorized=True):
    headers = {"Authorization": f"Bearer {SERVICE_KEY}"} if authorized else {}
    return TestClient(
        create_app(
            FakeEstimator(), interpreter=FakeInterpreter(), agent=agent, service_key=SERVICE_KEY
        ),
        headers=headers,
    )


def identity() -> dict:
    return {
        "output_version": 1,
        "provider": "fake",
        "model": "fake-model",
        "prompt_version": "action-agent-v1",
    }


def test_a_tool_call_decision():
    fake = FakeAgent()

    with client(fake) as http:
        response = http.post(STEP_PATH, json=request())

    assert response.status_code == 200
    assert response.json() == identity() | {
        "decision": {"type": "call_tool", "tool": "get_weekly_review", "arguments": {}}
    }
    assert fake.requests == [ActionAgentStepRequest.model_validate(request())]
    assert fake.closed


def test_a_proposal_and_a_no_action_decision():
    proposal = ProposeBudgetAdjustment(proposal=BudgetAdjustmentProposal.model_validate(PROPOSAL))

    with client(FakeAgent(proposal)) as http:
        proposed = http.post(STEP_PATH, json=request([REVIEW_STEP, BUDGET_STEP])).json()
    with client(FakeAgent(NoAction(reason="Nothing to change."))) as http:
        finished = http.post(STEP_PATH, json=request()).json()

    assert proposed["decision"] == {"type": "propose_budget_adjustment", "proposal": PROPOSAL}
    assert finished["decision"] == {"type": "no_action", "reason": "Nothing to change."}


def test_the_service_key_is_required_before_validation():
    fake = FakeAgent()

    with client(fake, authorized=False) as http:
        response = http.post(STEP_PATH, json=request())

    assert response.status_code == 401
    assert fake.requests == []


def test_invalid_requests_are_422_without_echoing_tool_results():
    fake = FakeAgent()
    data = request([REVIEW_STEP])
    data["steps"][0]["result"]["currencies"][0]["top_expense_categories"][0]["name"] = "Private"
    data["user_id"] = "u-1"

    with client(fake) as http:
        response = http.post(STEP_PATH, json=data)

    assert response.status_code == 422
    assert response.json() == {"error": {"code": "invalid_request", "fields": ["user_id"]}}
    assert "Private" not in response.text
    assert fake.requests == []


def test_failures_are_stable_codes():
    with client(FakeAgent(AgentStepFailed("x"))) as http:
        invalid = http.post(STEP_PATH, json=request())
    with client(FakeAgent(ProviderUnavailable("y"))) as http:
        unavailable = http.post(STEP_PATH, json=request())
    with client(FakeAgent(RuntimeError("boom with data"))) as http:
        unexpected = http.post(STEP_PATH, json=request())

    assert (invalid.status_code, invalid.json()) == (
        502,
        {"error": {"code": "agent_step_failed"}},
    )
    assert (unavailable.status_code, unavailable.json()) == (
        503,
        {"error": {"code": "provider_unavailable"}},
    )
    assert (unexpected.status_code, unexpected.json()) == (
        500,
        {"error": {"code": "internal_error"}},
    )
    assert "boom" not in unexpected.text


def test_without_credentials_steps_are_unavailable_and_health_reports_it():
    unconfigured = action_agent_from_environment({})

    with client(unconfigured) as http:
        response = http.post(STEP_PATH, json=request())
        health = http.get("/health").json()

    assert response.status_code == 503
    assert health["action_agent"] == {
        "provider": "groq",
        "model": "openai/gpt-oss-20b",
        "prompt_version": "action-agent-v1",
        "configured": False,
    }


def test_the_model_is_configurable_independently():
    configured = action_agent_from_environment(
        {"GROQ_API_KEY": "k", "LIFEOS_AI_ACTION_AGENT_MODEL": "other/model"}
    )

    assert (configured.model, configured.configured) == ("other/model", True)
