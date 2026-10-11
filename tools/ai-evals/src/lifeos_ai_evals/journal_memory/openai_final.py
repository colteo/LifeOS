"""AI-006: the ONE OpenAI final candidate (docs/tasks/ai/AI-006.md), frozen before any
live run.

- retrieval: the AI-005.1 journal-retrieval-v2 candidate (same chunking, SQL function,
  candidate depths, RRF, Ask context) with ONLY its embedding identity replaced by
  OpenAI text-embedding-3-small / 1536;
- answer: the production prompt journal-rag-answer-v1 on OpenAI gpt-6.1-sol over the
  Responses API (reasoning medium, store false, strict structured output);
- journal_rag: exactly those two.

Every identity value is imported from the production adapters (lifeos_ai) or from the
frozen AI-005 / AI-005.1 identities; the lab builds no OpenAI request, formats no input,
parses no response and owns no schema: it instantiates the production
`OpenAIJournalEmbedder` / `OpenAIJournalAnswerer` with an observing HTTP transport. The
key comes from OPENAI_API_KEY in the process environment only.
"""

import os

import httpx
from lifeos_ai.journal_memory.openai_answer import (
    DEFAULT_ANSWER_MODEL,
    REASONING_EFFORT,
    OpenAIAnswerSettings,
    OpenAIJournalAnswerer,
)
from lifeos_ai.journal_memory.openai_embeddings import (
    DEFAULT_EMBEDDING_MODEL,
    EMBEDDING_DIMENSIONS,
    OpenAIEmbeddingSettings,
    OpenAIJournalEmbedder,
)
from lifeos_ai.journal_memory.openai_http import OPENAI_BASE_URL
from lifeos_ai.journal_memory.prompt import PROMPT_VERSION

from lifeos_ai_evals.journal_memory.candidate import RETRIEVAL_CANDIDATE
from lifeos_ai_evals.journal_memory.identity import ANSWER_CONTROL
from lifeos_ai_evals.production import runtime

API_KEY_VARIABLE = "OPENAI_API_KEY"
_ANSWER_DEFAULTS = OpenAIAnswerSettings(api_key="-")

RETRIEVAL_FINAL = {
    **RETRIEVAL_CANDIDATE,
    "system": "journal-memory-openai-final-candidate",
    "embedding": {
        "provider": OpenAIJournalEmbedder.provider,
        "model": DEFAULT_EMBEDDING_MODEL,
        "dimensions": EMBEDDING_DIMENSIONS,
    },
}

ANSWER_FINAL = {
    "provider": OpenAIJournalAnswerer.provider,
    "model": DEFAULT_ANSWER_MODEL,
    "prompt_version": PROMPT_VERSION,
    "context_version": ANSWER_CONTROL["context_version"],
    "structured_output": ANSWER_CONTROL["structured_output"],
    "api": "responses",
    "generation_settings": {
        "reasoning_effort": REASONING_EFFORT,
        "store": False,
        "strict_structured_output": True,
        "max_output_tokens": _ANSWER_DEFAULTS.max_output_tokens,
        "tools": [],
    },
}

RAG_FINAL = {"retrieval": RETRIEVAL_FINAL, "answer": ANSWER_FINAL}

# Lab runtime (not identity): the production timeouts and attempt counts, a longer
# Retry-After cap so a rate limit waits instead of failing a case, and the AI-005 pause
# between answer cases. Recorded in each export.
EMBEDDING_RUNTIME = {
    "timeout_seconds": OpenAIEmbeddingSettings(api_key="-").timeout_seconds,
    "max_attempts": OpenAIEmbeddingSettings(api_key="-").max_attempts,
    "max_retry_wait_seconds": 60.0,
}
ANSWER_RUNTIME = {
    "timeout_seconds": _ANSWER_DEFAULTS.timeout_seconds,
    "max_attempts": _ANSWER_DEFAULTS.max_attempts,
    "max_retry_wait_seconds": 60.0,
    "inter_case_delay_seconds": runtime.RUNTIME["inter_case_delay_seconds"],
}
EMBEDDING_ADAPTER = "lifeos_ai.journal_memory.openai_embeddings.OpenAIJournalEmbedder"
ANSWER_ADAPTER = "lifeos_ai.journal_memory.openai_answer.OpenAIJournalAnswerer"


def key_from_environment() -> str:
    key = os.environ.get(API_KEY_VARIABLE, "").strip()
    if not key:
        raise ValueError(
            f"{API_KEY_VARIABLE} is required for the AI-006 OpenAI final run"
        )
    return key


def responses_usage(body) -> dict | None:
    """Telemetry only: Responses API token counts; unknown (None), never 0, when
    absent."""
    usage = body.get("usage") if isinstance(body, dict) else None
    if not isinstance(usage, dict):
        return None

    def count(key, detail=None):
        value = usage.get(key)
        if detail is not None:
            value = value.get(detail) if isinstance(value, dict) else None
        return value if type(value) is int and value >= 0 else None

    found = {
        "input_tokens": count("input_tokens"),
        "output_tokens": count("output_tokens"),
        "total_tokens": count("total_tokens"),
        "cached_input_tokens": count("input_tokens_details", "cached_tokens"),
        "reasoning_tokens": count("output_tokens_details", "reasoning_tokens"),
    }
    return found if any(value is not None for value in found.values()) else None


def _client(telemetry, transport, timeout, usage) -> httpx.AsyncClient:
    return httpx.AsyncClient(
        base_url=OPENAI_BASE_URL,
        timeout=timeout,
        transport=runtime.ObservingTransport(telemetry, transport, usage=usage),
    )


def live_embedder(
    api_key: str, telemetry: runtime.Telemetry, transport=None, sleep=None
) -> OpenAIJournalEmbedder:
    """The production adapter; embeddings usage (prompt/total tokens) is the
    OpenAI-compatible shape the default telemetry parser reads."""
    kwargs = {"sleep": sleep} if sleep is not None else {}
    return OpenAIJournalEmbedder(
        OpenAIEmbeddingSettings(
            api_key=api_key,
            model=RETRIEVAL_FINAL["embedding"]["model"],
            dimensions=RETRIEVAL_FINAL["embedding"]["dimensions"],
            **EMBEDDING_RUNTIME,
        ),
        client=_client(
            telemetry,
            transport,
            EMBEDDING_RUNTIME["timeout_seconds"],
            runtime.provider_usage,
        ),
        **kwargs,
    )


def live_answerer(
    api_key: str, telemetry: runtime.Telemetry, transport=None, sleep=None
) -> OpenAIJournalAnswerer:
    """The production adapter, unchanged: request body, prompt, schema and parsing are
    its own."""
    kwargs = {"sleep": sleep} if sleep is not None else {}
    return OpenAIJournalAnswerer(
        OpenAIAnswerSettings(
            api_key=api_key,
            model=ANSWER_FINAL["model"],
            timeout_seconds=ANSWER_RUNTIME["timeout_seconds"],
            max_attempts=ANSWER_RUNTIME["max_attempts"],
            max_retry_wait_seconds=ANSWER_RUNTIME["max_retry_wait_seconds"],
            max_output_tokens=ANSWER_FINAL["generation_settings"]["max_output_tokens"],
        ),
        client=_client(
            telemetry, transport, ANSWER_RUNTIME["timeout_seconds"], responses_usage
        ),
        **kwargs,
    )
