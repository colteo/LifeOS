"""AI-008 G1/G2: `assistant_v2` evaluator plugin (offline by default).

`evaluate assistant_v2` runs the offline fake through the production OpenAI adapter in
protocol 2 with a mocked HTTP client. Datasets:

- G2 (default): `datasets/assistant_v2/hydration-meal-v1.json`, and its held-out
  `hydration-meal-holdout-v1.json`;
- G1: `--dataset datasets/assistant_finance/v1.json` or `.../holdout-v1.json`, the
  frozen Finance files, converted to protocol 2 by `adapt.py` (same identity).

There is no `experiment()`: a live run is possible only through
`python -m lifeos_ai_evals.evaluators.assistant_v2.live_run` with explicit opt-in, a
cost estimate and a spending cap.
"""

from pathlib import Path

from lifeos_ai_evals.core.engine import load_dataset
from lifeos_ai_evals.evaluators.assistant_finance import plugin as finance_plugin
from lifeos_ai_evals.evaluators.assistant_v2 import adapt, gates
from lifeos_ai_evals.evaluators.assistant_v2.model import parse_expected, parse_input
from lifeos_ai_evals.evaluators.assistant_v2.scoring import AssistantV2Scorer
from lifeos_ai_evals.evaluators.assistant_v2.systems import fake_system

DATASETS = Path(__file__).resolve().parents[4] / "datasets/assistant_v2"


def default_dataset() -> Path:
    return DATASETS / "hydration-meal-v1.json"


# Frozen before any model saw it; never used to write or tune a prompt.
def holdout_dataset() -> Path:
    return DATASETS / "hydration-meal-holdout-v1.json"


def _finance_source(path: Path) -> str | None:
    sources = {
        finance_plugin.default_dataset().resolve(): "v1",
        finance_plugin.holdout_dataset().resolve(): "holdout-v1",
    }
    return sources.get(Path(path).resolve())


def load(path: Path):
    finance = _finance_source(path)
    if finance is not None:
        return adapt.finance_dataset(finance)
    dataset = load_dataset(path, parse_input, parse_expected)
    for case in dataset.cases:
        if not case.description.startswith("Synthetic"):
            raise ValueError(f"{case.id}: description must start with 'Synthetic'")
    return dataset


def all_datasets():
    """The four G1/G2 datasets, as the fake and the live run see them."""
    return (
        adapt.finance_dataset("v1"),
        adapt.finance_dataset("holdout-v1"),
        load(default_dataset()),
        load(holdout_dataset()),
    )


def system(name="fake"):
    if name == "fake":
        return fake_system(all_datasets())
    raise ValueError(
        f"unknown assistant_v2 system: {name}; live runs use "
        "lifeos_ai_evals.evaluators.assistant_v2.live_run"
    )


def scorer():
    return AssistantV2Scorer()


def run_acceptance(run: dict) -> dict:
    return gates.acceptance(run)
