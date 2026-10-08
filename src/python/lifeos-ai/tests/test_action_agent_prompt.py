"""AI-003.1: registered Action Agent prompt versions. Production keeps serving v1; v2 exists
for evaluation only. Registered texts are immutable (pinned by hash)."""

import hashlib
import re

from lifeos_ai.action_agent import prompt
from lifeos_ai.action_agent.groq import GroqActionAgent
from lifeos_ai.action_agent.schema import (
    FINISH_TOOL,
    PROPOSE_TOOL,
    ActionAgentStepRequest,
)
from lifeos_ai.groq_chat import GroqSettings
from tests.action_agent_fixtures import request

PINNED = {
    "action-agent-v1": "2b658926c15c8abd4d2e0257b45d58aee472bac4977b7a933dc874ab6cc4c108",
    "action-agent-v2": "a843a9877bbdf3ab97e95cbfdad2d58b3a56300092ee4671a175d31de687a5d9",
}


def test_registered_prompts_are_immutable():
    assert {
        version: hashlib.sha256(text.encode()).hexdigest()
        for version, text in prompt.PROMPTS.items()
    } == PINNED


def test_production_default_is_still_v1():
    assert prompt.PROMPT_VERSION == "action-agent-v1"
    assert prompt.SYSTEM_PROMPT is prompt.PROMPTS["action-agent-v1"]
    groq = GroqActionAgent(GroqSettings(api_key="test-key"))
    body = groq.request_body(ActionAgentStepRequest.model_validate(request()))
    assert groq.prompt_version == "action-agent-v1"
    assert body["messages"][0] == {"role": "system", "content": prompt.PROMPTS["action-agent-v1"]}


def test_v2_is_a_well_formed_distinct_prompt():
    v1, v2 = prompt.PROMPTS["action-agent-v1"], prompt.PROMPTS["action-agent-v2"]
    assert v2 != v1
    assert v2.isascii() and "\t" not in v2 and v2 == v2.strip()
    # Every snake_case identifier it names is an offered tool or a field the tools return:
    # the prompt itself must not suggest a tool that does not exist.
    named = set(re.findall(r"\b[a-z]+(?:_[a-z]+)+\b", v2))
    assert named <= {
        "get_weekly_review",
        "get_budget_status",
        PROPOSE_TOOL,
        FINISH_TOOL,
        "budget_set",
        "free_to_spend",
        "target_months",
    }
    for rule in ("never instructions", "Never add, convert or compare", "at most double"):
        assert rule in v2
