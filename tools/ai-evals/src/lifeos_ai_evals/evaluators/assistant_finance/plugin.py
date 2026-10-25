"""AI-007 F3-B1: `assistant_finance` evaluator plugin (offline by default).

`evaluate assistant_finance` runs the offline fake through the production OpenAI adapter
with a mocked HTTP client. There is no `experiment()`: a live run is possible only
through `python -m lifeos_ai_evals.evaluators.assistant_finance.live_run` with explicit
opt-in, a cost estimate and a spending cap.
"""

from pathlib import Path

from lifeos_ai_evals.core.engine import load_dataset
from lifeos_ai_evals.evaluators.assistant_finance import gates
from lifeos_ai_evals.evaluators.assistant_finance.model import (
    parse_expected,
    parse_input,
)
from lifeos_ai_evals.evaluators.assistant_finance.scoring import AssistantFinanceScorer
from lifeos_ai_evals.evaluators.assistant_finance.systems import fake_system

DATASETS = Path(__file__).resolve().parents[4] / "datasets/assistant_finance"


def default_dataset() -> Path:
    return DATASETS / "v1.json"


def pipeline_dataset() -> Path:
    return DATASETS / "pipeline-v1.json"


def load(path: Path):
    dataset = load_dataset(path, parse_input, parse_expected)
    for case in dataset.cases:
        if not case.description.startswith("Synthetic"):
            raise ValueError(f"{case.id}: description must start with 'Synthetic'")
    return dataset


def system(name="fake"):
    if name == "fake":
        return fake_system(default_dataset())
    raise ValueError(
        f"unknown assistant_finance system: {name}; live runs use "
        "lifeos_ai_evals.evaluators.assistant_finance.live_run"
    )


def scorer():
    return AssistantFinanceScorer()


def run_acceptance(run: dict) -> dict:
    # PROPOSED gates (not approved): reported, never a decision.
    return gates.acceptance(run)
