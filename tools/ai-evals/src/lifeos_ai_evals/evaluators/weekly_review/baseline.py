"""Deterministic template baseline: grounded by construction, no model, no network.

It quotes figures exactly as supplied and only states facts the AI-001 prompt allows. It
is a reference point for the scorers (it should be clean) and for live runs, not a
product candidate: it has no judgement about what matters. Output passes the production
Pydantic contract.
"""

from lifeos_ai.weekly_review.schema import (
    InterpretWeeklyReviewRequest,
    WeeklyReviewInsights,
)

from lifeos_ai_evals.core.engine import Prediction
from lifeos_ai_evals.evaluators.weekly_review.model import InsightsOutput


def figure(value: float) -> str:
    return str(int(value)) if float(value).is_integer() else str(value)


class TemplateInsights:
    name = "weekly-review-template-baseline"
    version = "1.0.0"
    configuration = {"provider": "deterministic", "max_items": 3}

    def predict(
        self, value: InterpretWeeklyReviewRequest
    ) -> Prediction[InsightsOutput]:
        finance, gym, nutrition = value.finance, value.gym, value.nutrition
        wins, attention, patterns, focus = [], [], [], []
        for currency in finance.currencies:
            if currency.net_flow < 0:
                attention.append(
                    f"Your {currency.currency} net flow was "
                    f"{figure(currency.net_flow)}."
                )
            elif currency.net_flow > 0:
                wins.append(
                    f"Your {currency.currency} net flow was "
                    f"{figure(currency.net_flow)}."
                )
        if gym.completed_workouts:
            wins.append(
                f"You completed {gym.completed_workouts} workouts with "
                f"{gym.completed_sets} of "
                f"{gym.prescribed_sets} prescribed sets."
            )
            if gym.completed_sets < gym.prescribed_sets:
                attention.append(
                    f"{gym.prescribed_sets - gym.completed_sets} prescribed sets were "
                    f"not completed."
                )
        if nutrition.meal_count:
            patterns.append(
                f"You logged {nutrition.meal_count} meals on "
                f"{nutrition.days_with_meals} days."
            )
            unanalyzed = nutrition.meal_count - nutrition.analyzed_meal_count
            if unanalyzed:
                attention.append(
                    f"{unanalyzed} of {nutrition.meal_count} meals are not analyzed, "
                    f"so nutrition "
                    "totals are partial."
                )
                focus.append(
                    "Analyze the remaining meals so the nutrition totals are complete."
                )

        recorded = [
            label
            for label, present in (
                ("finance", bool(finance.currencies)),
                ("workout", bool(gym.completed_workouts)),
                ("meal", bool(nutrition.meal_count)),
            )
            if present
        ]
        summary = (
            f"This week has {', '.join(recorded)} records."
            if recorded
            else "No finance, workout or meal records were logged this week."
        )
        insights = WeeklyReviewInsights(
            summary=summary,
            wins=wins[:3],
            attention=attention[:3],
            patterns=patterns[:3],
            next_week_focus=focus[:3],
        )
        return Prediction(InsightsOutput.from_insights(insights), ("template",))
