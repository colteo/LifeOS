import json
from pathlib import Path

from lifeos_ai_evals.evaluators.journal_retrieval import acceptance as gates
from lifeos_ai_evals.evaluators.journal_retrieval import promotion as promotion_gates
from lifeos_ai_evals.evaluators.journal_retrieval.model import load_retrieval_dataset
from lifeos_ai_evals.evaluators.journal_retrieval.predictor import (
    offline_sanity,
    production_control,
    retrieval_v2_candidate,
)
from lifeos_ai_evals.evaluators.journal_retrieval.scoring import JournalRetrievalScorer
from lifeos_ai_evals.journal_memory.candidate import RETRIEVAL_CANDIDATE
from lifeos_ai_evals.journal_memory.identity import RETRIEVAL_CONTROL, load_control


def default_dataset() -> Path:
    return Path(__file__).resolve().parents[4] / "datasets/journal_retrieval/v1.json"


def load(path: Path):
    return load_retrieval_dataset(path)


def system(name="offline-sanity"):
    """Offline: lab hashing embedder over the real schema and SQL function (Docker
    needed)."""
    if name == "offline-sanity":
        return offline_sanity()
    raise ValueError(
        f"unknown journal_retrieval system: {name}; use --variant for the live control"
    )


def scorer():
    return JournalRetrievalScorer()


def experiment(path: Path):
    """The production control, or (AI-005.1) exactly the journal-retrieval-v2
    candidate; any other identity is rejected before any provider call."""
    try:
        data = json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise ValueError("invalid journal_retrieval experiment JSON") from exc
    if data == RETRIEVAL_CANDIDATE:
        return retrieval_v2_candidate(data)
    return production_control(
        load_control(path, RETRIEVAL_CONTROL, "journal_retrieval")
    )


def run_acceptance(run: dict) -> dict:
    return gates.acceptance(run)


INTERPRETATION = (
    "Signed deltas relative to run 0 (the production control). No overall retrieval "
    "score: promotion requires every predeclared AI-005.1 gate to pass."
)


def acceptance(reference: dict, candidate: dict) -> dict:
    """AI-005.1: `compare` reports the frozen promotion gates (control vs candidate)."""
    return promotion_gates.promotion(reference, candidate)
