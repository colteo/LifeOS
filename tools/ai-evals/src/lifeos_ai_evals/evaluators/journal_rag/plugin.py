from pathlib import Path

from lifeos_ai_evals.evaluators.journal_rag import acceptance as gates
from lifeos_ai_evals.evaluators.journal_rag.model import load_rag_dataset
from lifeos_ai_evals.evaluators.journal_rag.predictor import (
    offline_sanity,
    production_control,
)
from lifeos_ai_evals.evaluators.journal_rag.scoring import JournalRagScorer
from lifeos_ai_evals.journal_memory.identity import RAG_CONTROL, load_control


def default_dataset() -> Path:
    return Path(__file__).resolve().parents[4] / "datasets/journal_rag/v1.json"


def load(path: Path):
    return load_rag_dataset(path)


def system(name="offline-sanity"):
    """Offline: hashing embedder + extractive answerer over the real schema (Docker
    needed)."""
    if name == "offline-sanity":
        return offline_sanity()
    raise ValueError(
        f"unknown journal_rag system: {name}; use --variant for the live control"
    )


def scorer():
    return JournalRagScorer()


def experiment(path: Path):
    return production_control(load_control(path, RAG_CONTROL, "journal_rag"))


def run_acceptance(run: dict) -> dict:
    return gates.acceptance(run)
