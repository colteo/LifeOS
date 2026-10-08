from dataclasses import replace
from pathlib import Path

from lifeos_ai_evals.core.engine import Dataset, load_dataset
from lifeos_ai_evals.evaluators.weekly_review import acceptance as gates
from lifeos_ai_evals.evaluators.weekly_review.baseline import TemplateInsights
from lifeos_ai_evals.evaluators.weekly_review.grounding import source_of
from lifeos_ai_evals.evaluators.weekly_review.model import parse_expected, parse_input
from lifeos_ai_evals.evaluators.weekly_review.scoring import WeeklyReviewScorer

METRICS_NOTE = "rates over valid outputs; detectors are lexicons, see AI-003"
INTERPRETATION = (
    "Signed deltas relative to run 0. Inspect scored/error coverage first. Detector "
    "rates are "
    "lexicon flags to review by hand. No overall winner."
)


def default_dataset() -> Path:
    return Path(__file__).resolve().parents[4] / "datasets/weekly_review/v1.json"


def load(path: Path) -> Dataset:
    dataset = load_dataset(path, parse_input, parse_expected)
    # The scorer derives grounding from the input; carry it with the expectations so the
    # generic Scorer contract (expected, predicted) stays unchanged.
    cases = tuple(
        replace(case, expected=replace(case.expected, source=source_of(case.input)))
        for case in dataset.cases
    )
    return replace(dataset, cases=cases)


def system(name="baseline"):
    if name == "baseline":
        return TemplateInsights()
    raise ValueError(
        f"unknown weekly_review system: {name}; use --variant for live models"
    )


def scorer():
    return WeeklyReviewScorer()


def experiment(path: Path):
    from lifeos_ai_evals.core.experiments import create_experiment
    from lifeos_ai_evals.evaluators.weekly_review.live import LiveWeeklyReviewPredictor

    return create_experiment(path, {"groq": LiveWeeklyReviewPredictor})


def acceptance(reference: dict, candidate: dict) -> dict:
    return gates.acceptance(reference, candidate)
