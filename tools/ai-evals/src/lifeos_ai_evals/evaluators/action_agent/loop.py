"""Replays one scenario through the AI-002 loop: mirrored .NET loop + a step model.

The step model is anything with the production `ActionAgentModel.step` signature: the
production GroqActionAgent (live) or the deterministic rule baseline. Each step request
is built and validated with the production `ActionAgentStepRequest`. `AgentStepFailed`
ends the run as .NET does (invalid_output); `ProviderUnavailable` propagates as an
execution error.
"""

from decimal import Decimal

from lifeos_ai.action_agent.agent import AgentStepFailed
from lifeos_ai.action_agent.schema import (
    FINISH_TOOL,
    PROPOSE_TOOL,
    ActionAgentStepRequest,
    CallTool,
    NoAction,
    ProposeBudgetAdjustment,
)

from lifeos_ai_evals.evaluators.action_agent import dotnet_mirror as net
from lifeos_ai_evals.evaluators.action_agent.model import (
    ProposalRecord,
    ReadCall,
    RunTrace,
    Scenario,
)


def decision_name(decision) -> str:
    if isinstance(decision, CallTool):
        return decision.tool
    return (
        PROPOSE_TOOL if isinstance(decision, ProposeBudgetAdjustment) else FINISH_TOOL
    )


class _DecisionNames:
    """Default observer: the names of the decisions the step model returned."""

    def __init__(self):
        self.last: tuple[str, ...] = ()

    def mark(self):
        self.last = ()

    def names(self, _mark) -> tuple[str, ...]:
        return self.last


async def run(scenario: Scenario, model, observer=None) -> RunTrace:
    names = observer or _DecisionNames()
    executed: list[dict] = []
    reads: list[ReadCall] = []
    keys: set[str] = set()
    emitted: list[tuple[str, ...]] = []
    observations: dict[tuple[str, str], Decimal | None] = {}
    steps = 0

    def end(outcome, error_code=None, proposal=None) -> RunTrace:
        return RunTrace(
            outcome,
            error_code,
            steps,
            tuple(reads),
            tuple(emitted),
            tuple(
                (month, currency, None if amount is None else str(amount))
                for (month, currency), amount in observations.items()
            ),
            proposal,
        )

    for step in range(1, net.MAX_STEPS + 1):
        request = ActionAgentStepRequest.model_validate(
            {
                "tool_schema_version": net.TOOL_SCHEMA_VERSION,
                "context": scenario.context,
                "tools": net.READ_TOOLS,
                "steps": executed,
                "max_steps": net.MAX_STEPS,
            }
        )
        mark = names.mark()
        steps = step
        try:
            decision = await model.step(request)
        except AgentStepFailed:
            emitted.append(names.names(mark))
            return end("failed", net.INVALID_OUTPUT)
        if observer is None:
            names.last = (decision_name(decision),)
        emitted.append(names.names(mark))

        if isinstance(decision, CallTool):
            if step == net.MAX_STEPS:
                return end("failed", net.MAX_STEPS_REACHED)
            if decision.tool not in net.READ_TOOL_NAMES:
                return end("failed", net.UNKNOWN_TOOL)
            key = net.call_key(decision.tool, decision.arguments)
            if key in keys:
                return end("failed", net.REPEATED_TOOL_CALL)
            keys.add(key)
            result, observation = net.execute(
                scenario, decision.tool, decision.arguments
            )
            reads.append(ReadCall(decision.tool, "error" not in result, key))
            executed.append(
                {
                    "tool": decision.tool,
                    "arguments": decision.arguments,
                    "result": result,
                }
            )
            if observation is not None:
                observations[(observation[0], observation[1])] = observation[2]
            continue

        if isinstance(decision, ProposeBudgetAdjustment):
            proposal = decision.proposal
            check = net.validate_proposal(scenario, proposal, observations)
            record = ProposalRecord(
                proposal.year,
                proposal.month,
                proposal.currency,
                str(Decimal(str(proposal.proposed_amount))),
                check,
            )
            return end("proposed" if check is None else "failed", check, record)

        if isinstance(decision, NoAction):
            return end("no_action")
        return end("failed", net.INVALID_OUTPUT)

    return end("failed", net.MAX_STEPS_REACHED)
