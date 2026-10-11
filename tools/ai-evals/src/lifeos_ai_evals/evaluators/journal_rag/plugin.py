import json
from pathlib import Path

from lifeos_ai_evals.evaluators.journal_rag import acceptance as gates
from lifeos_ai_evals.evaluators.journal_rag.model import load_rag_dataset
from lifeos_ai_evals.evaluators.journal_rag.predictor import (
    offline_sanity,
    openai_final_candidate,
    production_control,
)
from lifeos_ai_evals.evaluators.journal_rag.scoring import JournalRagScorer
from lifeos_ai_evals.journal_memory import final_acceptance
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
    # AI-006: exactly the frozen OpenAI final candidate; otherwise the control only.
    try:
        data = json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise ValueError("invalid journal_rag experiment JSON") from exc
    if data == final_acceptance.RAG_FINAL:
        return openai_final_candidate(data)
    return production_control(load_control(path, RAG_CONTROL, "journal_rag"))


def run_acceptance(run: dict) -> dict:
    # AI-006: the OpenAI final candidate is judged by its own frozen absolute gates.
    if final_acceptance.is_candidate(run, final_acceptance.RAG_FINAL):
        return final_acceptance.rag(run)
    return gates.acceptance(run)
