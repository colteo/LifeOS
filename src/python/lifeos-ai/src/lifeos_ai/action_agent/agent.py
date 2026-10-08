"""Provider-neutral Action Agent step port (AI-002) and its failure kinds."""

from typing import Protocol

from lifeos_ai.action_agent.schema import ActionAgentStepRequest, CallTool, NoAction
from lifeos_ai.action_agent.schema import ProposeBudgetAdjustment as Proposal
from lifeos_ai.errors import ProviderUnavailable, ServiceError

__all__ = [
    "ActionAgentModel",
    "AgentStepFailed",
    "ProviderUnavailable",
    "UnconfiguredAgent",
]


class AgentStepFailed(ServiceError):
    """The provider answered, but not with exactly one valid decision (no or several tool calls,
    a tool that was not offered, malformed or out-of-bounds arguments)."""

    code = "agent_step_failed"
    status = 502


class ActionAgentModel(Protocol):
    provider: str
    model: str
    prompt_version: str

    @property
    def configured(self) -> bool: ...

    async def step(self, request: ActionAgentStepRequest) -> CallTool | Proposal | NoAction: ...

    async def aclose(self) -> None: ...


class UnconfiguredAgent:
    """Used when no provider credentials are set: every step is unavailable."""

    configured = False

    def __init__(self, *, provider: str, model: str, prompt_version: str):
        self.provider = provider
        self.model = model
        self.prompt_version = prompt_version

    async def step(self, request: ActionAgentStepRequest) -> CallTool | Proposal | NoAction:
        raise ProviderUnavailable("provider credentials are not configured")

    async def aclose(self) -> None:
        return None
