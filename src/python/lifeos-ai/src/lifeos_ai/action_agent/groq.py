"""Groq Action Agent step over the shared Chat Completions transport (native tool calling).

Same generation policy as the other capabilities (ADR-011): temperature 0, low reasoning effort
without returned reasoning, bounded attempts. Every step must be exactly ONE tool call
(tool_choice "required", no parallel calls). On the last allowed step only the terminal tools are
offered, so the model has to finish. Whatever the provider returns is validated here before it can
leave the service.
"""

import asyncio
import json
import logging
import time
from collections.abc import Awaitable, Callable

import httpx
from pydantic import ValidationError

from lifeos_ai.action_agent.agent import AgentStepFailed, ProviderUnavailable
from lifeos_ai.action_agent.prompt import PROMPT_VERSION, build_messages
from lifeos_ai.action_agent.schema import (
    FINISH_TOOL,
    PROPOSE_TOOL,
    TERMINAL_TOOL_DEFINITIONS,
    ActionAgentStepRequest,
    BudgetAdjustmentProposal,
    CallTool,
    FinishWithoutProposal,
    NoAction,
    ProposeBudgetAdjustment,
)
from lifeos_ai.groq_chat import CompletionFailed, GroqChatCompletions, GroqSettings

logger = logging.getLogger("lifeos_ai.action_agent")


def _function(definition: dict) -> dict:
    return {
        "type": "function",
        "function": {
            "name": definition["name"],
            "description": definition["description"],
            "parameters": definition["parameters"],
        },
    }


class GroqActionAgent:
    provider = "groq"
    configured = True

    def __init__(
        self,
        settings: GroqSettings,
        *,
        client: httpx.AsyncClient | None = None,
        sleep: Callable[[float], Awaitable[None]] = asyncio.sleep,
    ):
        self.model = settings.model
        self.prompt_version = PROMPT_VERSION
        self._settings = settings
        self._chat = GroqChatCompletions(settings, client=client, sleep=sleep)

    @staticmethod
    def offered_tools(request: ActionAgentStepRequest) -> list[str]:
        """Read tools while steps remain; on the last step only the terminal tools."""
        read = [] if request.is_final_step else [tool.name for tool in request.tools]
        return read + [PROPOSE_TOOL, FINISH_TOOL]

    def request_body(self, request: ActionAgentStepRequest) -> dict:
        read = [] if request.is_final_step else [tool.model_dump() for tool in request.tools]
        return {
            "model": self.model,
            "messages": build_messages(request),
            "temperature": 0,
            "max_completion_tokens": self._settings.max_completion_tokens,
            "reasoning_effort": "low",
            "include_reasoning": False,
            "tools": [_function(tool) for tool in read + TERMINAL_TOOL_DEFINITIONS],
            "tool_choice": "required",
            "parallel_tool_calls": False,
        }

    async def step(
        self, request: ActionAgentStepRequest
    ) -> CallTool | ProposeBudgetAdjustment | NoAction:
        started = time.monotonic()
        step_number = len(request.steps) + 1
        try:
            completion = await self._chat.complete_message(self.request_body(request))
        except CompletionFailed as failure:
            self._log(failure.outcome, "-", step_number, failure.attempts, started)
            if failure.unavailable:
                raise ProviderUnavailable(f"provider unavailable ({failure.outcome})") from None
            raise AgentStepFailed("provider output is not a valid decision") from None

        try:
            name, decision = self._decision(completion.message, self.offered_tools(request))
        except (AgentStepFailed, ValidationError, ValueError) as error:
            # Nothing partial is ever returned: one invalid field rejects the whole step.
            self._log("invalid_output", "-", step_number, completion.attempts, started)
            raise AgentStepFailed("provider output is not a valid decision") from error

        self._log(decision.type, name, step_number, completion.attempts, started)
        return decision

    async def aclose(self) -> None:
        await self._chat.aclose()

    @staticmethod
    def _decision(
        message: dict, offered: list[str]
    ) -> tuple[str, CallTool | ProposeBudgetAdjustment | NoAction]:
        calls = message.get("tool_calls")
        if not isinstance(calls, list) or len(calls) != 1:
            raise AgentStepFailed("exactly one tool call is required")
        function = calls[0].get("function") if isinstance(calls[0], dict) else None
        if not isinstance(function, dict):
            raise AgentStepFailed("malformed tool call")
        name = function.get("name")
        if not isinstance(name, str) or name not in offered:
            raise AgentStepFailed("the tool was not offered")
        raw = function.get("arguments") or "{}"
        arguments = json.loads(raw) if isinstance(raw, str) else raw
        if not isinstance(arguments, dict):
            raise AgentStepFailed("arguments must be a JSON object")

        if name == PROPOSE_TOOL:
            proposal = BudgetAdjustmentProposal.model_validate(arguments)
            return name, ProposeBudgetAdjustment(proposal=proposal)
        if name == FINISH_TOOL:
            return name, NoAction(reason=FinishWithoutProposal.model_validate(arguments).reason)
        # A read tool: LifeOS validates the arguments strictly and executes it.
        return name, CallTool(tool=name, arguments=arguments)

    def _log(self, outcome: str, tool: str, step: int, attempts: int, started: float) -> None:
        # Diagnostics only: tool names are fixed identifiers. Never the arguments, the tool
        # results, the prompt or the provider response body.
        logger.info(
            "action agent step outcome=%s tool=%s step=%d provider=%s model=%s prompt=%s "
            "attempts=%d seconds=%.3f",
            outcome,
            tool,
            step,
            self.provider,
            self.model,
            self.prompt_version,
            attempts,
            time.monotonic() - started,
        )
