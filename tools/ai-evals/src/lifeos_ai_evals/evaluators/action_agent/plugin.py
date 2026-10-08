from dataclasses import asdict, replace
from pathlib import Path

from lifeos_ai_evals.core.engine import Dataset, load_dataset
from lifeos_ai_evals.evaluators.action_agent import acceptance as gates
from lifeos_ai_evals.evaluators.action_agent.baseline import RuleBaseline
from lifeos_ai_evals.evaluators.action_agent.model import parse_expected, parse_input
from lifeos_ai_evals.evaluators.action_agent.scoring import ActionAgentScorer

METRICS_NOTE = "per-run metrics; grounding checks mirror .NET validation, see AI-003"
INTERPRETATION = (
    "Signed deltas relative to run 0. Inspect scored/error coverage first. Safety "
    "gates are "
    "absolute; other metrics are compared one by one. No overall winner."
)


def default_dataset() -> Path:
    return Path(__file__).resolve().parents[4] / "datasets/action_agent/v1.json"


def load(path: Path) -> Dataset:
    dataset = load_dataset(path, parse_input, parse_expected)
    # The scorer checks proposals against the scenario; carry it with the expectations
    # so the generic Scorer contract (expected, predicted) stays unchanged.
    cases = tuple(
        replace(case, expected=replace(case.expected, source=asdict(case.input)))
        for case in dataset.cases
    )
    return replace(dataset, cases=cases)


def system(name="baseline"):
    if name == "baseline":
        return RuleBaseline()
    raise ValueError(
        f"unknown action_agent system: {name}; use --variant for live models"
    )


def scorer():
    return ActionAgentScorer()


def experiment(path: Path):
    from lifeos_ai_evals.core.experiments import create_experiment
    from lifeos_ai_evals.evaluators.action_agent.live import LiveActionAgentPredictor

    return create_experiment(path, {"groq": LiveActionAgentPredictor})


def acceptance(reference: dict, candidate: dict) -> dict:
    return gates.acceptance(reference, candidate)
