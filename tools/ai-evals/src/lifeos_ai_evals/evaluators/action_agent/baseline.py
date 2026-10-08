"""Deterministic rule baseline step model (no LLM, no network), run through the same
loop.

Policy (action-agent-rule-baseline v1.0.0), written from the AI-002 prompt's own rule,
not from the dataset labels: read the review; read the CURRENT month's budget for each
review currency; propose raising the first budget whose free-to-spend is negative to
cover spent + expected expenses, rounded up to a multiple of 50 and capped at double the
amount; otherwise finish. It never lowers a budget and never looks at the next month:
those are deliberate limits.
"""

import asyncio
import math

from lifeos_ai.action_agent.schema import (
    BudgetAdjustmentProposal,
    CallTool,
    NoAction,
    ProposeBudgetAdjustment,
)

from lifeos_ai_evals.core.engine import Prediction
from lifeos_ai_evals.evaluators.action_agent import dotnet_mirror as net
from lifeos_ai_evals.evaluators.action_agent.loop import run
from lifeos_ai_evals.evaluators.action_agent.model import RunTrace, Scenario

ROUNDING = 50


class RuleStepModel:
    async def step(self, request):
        steps = request.steps
        if not steps:
            return CallTool(tool=net.GET_WEEKLY_REVIEW, arguments={})
        review = steps[0].result
        year, month = (int(part) for part in request.context.current_month.split("-"))
        read = {
            s.arguments.get("currency")
            for s in steps
            if s.tool == net.GET_BUDGET_STATUS
        }
        if not request.is_final_step:
            for currency in review.get("currencies", []):
                if currency["currency"] not in read:
                    arguments = {
                        "year": year,
                        "month": month,
                        "currency": currency["currency"],
                    }
                    return CallTool(tool=net.GET_BUDGET_STATUS, arguments=arguments)
        for executed in steps[1:]:
            budget = executed.result
            if not budget.get("budget_set") or budget["free_to_spend"] >= 0:
                continue
            expected = (
                budget["expected_recurring_expenses"]
                + budget["expected_planned_expenses"]
            )
            needed = budget["spent"] + expected
            proposed = min(
                math.ceil(needed / ROUNDING) * ROUNDING, 2 * budget["amount"]
            )
            if proposed > budget["amount"]:
                return ProposeBudgetAdjustment(
                    proposal=BudgetAdjustmentProposal(
                        year=budget["year"],
                        month=budget["month"],
                        currency=budget["currency"],
                        proposed_amount=proposed,
                        rationale=(
                            f"You spent {budget['spent']} of {budget['amount']} "
                            f"{budget['currency']} with {expected} "
                            f"{budget['currency']} of "
                            "expenses still expected."
                        ),
                    )
                )
        return NoAction(reason="No budget change is clearly supported by the figures.")


class RuleBaseline:
    name = "action-agent-rule-baseline"
    version = "1.0.0"
    configuration = {
        "provider": "deterministic",
        "loop": net.LOOP_VERSION,
        "tool_schema_version": net.TOOL_SCHEMA_VERSION,
        "rounding": ROUNDING,
        "targets": "current month only; raise only",
    }

    def predict(self, value: Scenario) -> Prediction[RunTrace]:
        return Prediction(asyncio.run(run(value, RuleStepModel())), ("rule-baseline",))
