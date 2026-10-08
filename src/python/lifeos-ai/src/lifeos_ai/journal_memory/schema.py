"""AI-004: the journal-memory contracts (index-entry, embed-query, answer).

Requests carry journal text only: no user id, entry id or other LifeOS identifier exists in any
model, and unknown fields are rejected so nothing else can be sent by mistake. Every response is
validated here before it leaves the service, and LifeOS validates it again.
"""

import math
from datetime import UTC, datetime
from typing import Annotated, Literal

from pydantic import (
    AfterValidator,
    AwareDatetime,
    BaseModel,
    ConfigDict,
    Field,
    StringConstraints,
    field_validator,
    model_validator,
)

from lifeos_ai.journal_memory.chunking import (
    MAX_CHUNK_CHARS,
    MAX_CHUNKS,
    MAX_CONTENT_CHARS,
    MAX_TITLE_CHARS,
)

OUTPUT_VERSION = 1
EMBEDDING_DIMENSIONS = 1536

MAX_QUESTION_CHARS = 500
MAX_SOURCES = 8
MAX_SOURCE_TEXT_CHARS = MAX_CHUNK_CHARS
MAX_ANSWER_CHARS = 1200


def _not_blank(text: str) -> str:
    if not text.strip():
        raise ValueError("must not be blank")
    return text


def _optional_title(text: str | None) -> str | None:
    if text is None:
        return None
    text = text.strip()
    return text or None


class _Strict(BaseModel):
    model_config = ConfigDict(extra="forbid")


# Content keeps its exact characters (chunks are substrings of it); the title is display data.
Content = Annotated[
    str, StringConstraints(max_length=MAX_CONTENT_CHARS), AfterValidator(_not_blank)
]
Title = Annotated[
    str | None,
    StringConstraints(max_length=MAX_TITLE_CHARS),
    AfterValidator(_optional_title),
]
Question = Annotated[
    str,
    StringConstraints(strip_whitespace=True, min_length=1, max_length=MAX_QUESTION_CHARS),
]


def _finite_vector(values: list[float]) -> list[float]:
    if len(values) != EMBEDDING_DIMENSIONS:
        raise ValueError(f"embedding must have exactly {EMBEDDING_DIMENSIONS} dimensions")
    if not all(math.isfinite(value) for value in values):
        raise ValueError("embedding values must be finite")
    return values


Vector = Annotated[list[Annotated[float, Field(strict=True)]], AfterValidator(_finite_vector)]


# ---- POST /v1/journal-memory/index-entry ----


class IndexEntryRequest(_Strict):
    """One journal entry's text. Nothing identifies the entry or its owner."""

    title: Title = None
    content: Content


class IndexedChunk(_Strict):
    ordinal: int = Field(ge=0, lt=MAX_CHUNKS)
    text: Annotated[str, StringConstraints(min_length=1, max_length=MAX_CHUNK_CHARS)]
    embedding: Vector


class IndexEntryResponse(_Strict):
    output_version: Literal[1]
    chunking_version: str
    embedding_provider: str
    embedding_model: str
    embedding_dimensions: Literal[1536]
    chunks: list[IndexedChunk] = Field(min_length=1, max_length=MAX_CHUNKS)

    @model_validator(mode="after")
    def _contiguous(self) -> "IndexEntryResponse":
        if [chunk.ordinal for chunk in self.chunks] != list(range(len(self.chunks))):
            raise ValueError("chunk ordinals must be contiguous from 0")
        return self


# ---- POST /v1/journal-memory/embed-query ----


class EmbedQueryRequest(_Strict):
    question: Question


class EmbedQueryResponse(_Strict):
    output_version: Literal[1]
    provider: str
    model: str
    dimensions: Literal[1536]
    embedding: Vector


# ---- POST /v1/journal-memory/answer ----

SourceLabel = Annotated[str, StringConstraints(pattern=r"^S[1-9][0-9]?$")]


class AnswerSource(_Strict):
    """One retrieved passage. `label` is request-local and opaque (S1, S2, ...)."""

    label: SourceLabel
    title: Title = None
    occurred_at: AwareDatetime
    text: Annotated[
        str, StringConstraints(max_length=MAX_SOURCE_TEXT_CHARS), AfterValidator(_not_blank)
    ]


class AnswerRequest(_Strict):
    question: Question
    sources: list[AnswerSource] = Field(min_length=1, max_length=MAX_SOURCES)

    @field_validator("sources")
    @classmethod
    def _distinct_labels(cls, sources: list[AnswerSource]) -> list[AnswerSource]:
        labels = [source.label for source in sources]
        if len(set(labels)) != len(labels):
            raise ValueError("source labels must be distinct")
        return sources

    def labels(self) -> set[str]:
        return {source.label for source in self.sources}


def _plain_answer(text: str) -> str:
    # Plain text: line breaks allowed, other control characters and markup are not.
    if any((ord(char) < 32 and char != "\n") or ord(char) == 127 for char in text):
        raise ValueError("answer must be plain text")
    if any(fragment in text for fragment in ("**", "__", "`", "](")):
        raise ValueError("answer must not contain markup")
    return text


Answer = Annotated[
    str,
    StringConstraints(strip_whitespace=True, max_length=MAX_ANSWER_CHARS),
    AfterValidator(_plain_answer),
]


class JournalAnswer(_Strict):
    """The model's structured output. Citation membership is checked against the request."""

    status: Literal["answered", "insufficient_evidence"]
    answer: Answer
    citations: list[SourceLabel] = Field(max_length=MAX_SOURCES)

    @model_validator(mode="after")
    def _consistent(self) -> "JournalAnswer":
        if len(set(self.citations)) != len(self.citations):
            raise ValueError("citations must be distinct")
        if self.status == "answered" and (not self.answer or not self.citations):
            raise ValueError("an answer needs text and at least one citation")
        if self.status == "insufficient_evidence" and (self.answer or self.citations):
            raise ValueError("insufficient evidence carries no answer and no citations")
        return self

    def cites_only(self, labels: set[str]) -> bool:
        return set(self.citations) <= labels


class AnswerResponse(_Strict):
    output_version: Literal[1]
    provider: str
    model: str
    prompt_version: str
    result: JournalAnswer


# Strict structured-output schema for the provider; Pydantic (above) re-validates every answer.
ANSWER_OUTPUT_SCHEMA = {
    "type": "object",
    "additionalProperties": False,
    "required": ["status", "answer", "citations"],
    "properties": {
        "status": {"type": "string", "enum": ["answered", "insufficient_evidence"]},
        "answer": {"type": "string"},
        "citations": {"type": "array", "items": {"type": "string"}},
    },
}


def occurred_at_text(value: datetime) -> str:
    """The source time as the prompt shows it: UTC, minute precision."""
    return value.astimezone(UTC).strftime("%Y-%m-%d %H:%M UTC")
