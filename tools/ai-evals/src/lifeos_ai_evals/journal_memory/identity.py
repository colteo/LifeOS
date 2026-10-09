"""AI-005: the ONE system under evaluation, the AI-004 production control.

Every value is either imported from the production package or pinned here and
drift-tested against the production sources (.NET policy, the AddJournalMemory
migration, the integration test image): see tests/test_journal_drift.py. AI-005 has no
candidate. A configuration that differs from the control in any field is rejected before
any provider call or container start.
"""

import json
from pathlib import Path

from lifeos_ai.groq_chat import DEFAULT_MODEL as ANSWER_MODEL
from lifeos_ai.journal_memory.chunking import CHUNKING_VERSION
from lifeos_ai.journal_memory.gemini_embeddings import (
    DEFAULT_EMBEDDING_MODEL,
    EMBEDDING_DIMENSIONS,
    GeminiJournalEmbedder,
)
from lifeos_ai.journal_memory.prompt import PROMPT_VERSION as ANSWER_PROMPT_VERSION

REPOSITORY_ROOT = Path(__file__).resolve().parents[5]

# The PostgreSQL image of the AI-004 integration tests and docker-compose
# (drift-tested).
DATABASE_IMAGE = "pgvector/pgvector:0.8.6-pg18-trixie"

# The production adapter's own provider id (google), drift-tested against the .NET
# policy.
EMBEDDING_PROVIDER = GeminiJournalEmbedder.provider

# journal-retrieval-v1, documented constants of search_journal_memory_v1 (drift-tested
# against the migration). The lab never re-implements them: ranking comes from the SQL
# function only.
RETRIEVAL = {
    "version": "journal-retrieval-v1",
    "function": "search_journal_memory_v1",
    "vector_candidates": 20,
    "lexical_candidates": 20,
    "rrf_k": 60,
    "limit": 8,
    "similarity_threshold": None,
}

RETRIEVAL_CONTROL = {
    "system": "journal-memory-production-control",
    "embedding": {
        "provider": EMBEDDING_PROVIDER,
        "model": DEFAULT_EMBEDDING_MODEL,
        "dimensions": EMBEDDING_DIMENSIONS,
    },
    "chunking_version": CHUNKING_VERSION,
    "retrieval": RETRIEVAL,
    "database_image": DATABASE_IMAGE,
}

# The production answer request settings (GroqJournalAnswerer.request_body,
# GroqSettings).
ANSWER_GENERATION = {
    "temperature": 0,
    "max_completion_tokens": 2048,
    "reasoning_effort": "low",
    "include_reasoning": False,
}

ANSWER_CONTROL = {
    "provider": "groq",
    "model": ANSWER_MODEL,
    "prompt_version": ANSWER_PROMPT_VERSION,
    "context_version": "journal-answer-request-v1",
    "structured_output": "journal-answer-output-v1",
    "generation_settings": ANSWER_GENERATION,
}

RAG_CONTROL = {"retrieval": RETRIEVAL_CONTROL, "answer": ANSWER_CONTROL}


def load_control(path: Path, expected: dict, label: str) -> dict:
    """Loads an experiment file and requires it to BE the production control."""
    try:
        data = json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise ValueError(f"invalid {label} experiment JSON") from exc
    if data != expected:
        raise ValueError(
            f"{label}: AI-005 evaluates only the frozen AI-004 production control; "
            "any other identity (threshold, retrieval-v2, prompt-v2, model) is out "
            "of scope"
        )
    return data
