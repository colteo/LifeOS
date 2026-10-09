"""AI-005: the frozen synthetic journal corpus and its labelled evidence.

Evidence is labelled as SOURCE SPANS (entry key + an exact quote of its content), never
as chunk ids picked after looking at retrieval output. The production chunker
(`journal-chunking-v1`, imported, not copied) deterministically maps each span to the
chunk(s) that carry it:

- a chunk is relevant to an evidence span when it contains the whole quote;
- when no chunk contains the whole quote (it straddles a chunk boundary), every chunk
  overlapping it is relevant.

Entry lifecycles exercise the absolute safety checks:
- `active`: indexed with the run's embedding identity (the searchable corpus);
- `deleted`: indexed, then hard-deleted (cascade) before any search; must never be
  returned;
- `foreign_identity`: indexed under a different chunking/model identity; must never be
  returned.

Entries of the `other` user must never be returned for the `primary` user.
"""

import hashlib
import json
import uuid
from dataclasses import dataclass
from datetime import UTC, datetime

from lifeos_ai.journal_memory import chunking
from lifeos_ai.journal_memory.chunking import (
    CHUNKING_VERSION,
    MAX_CONTENT_CHARS,
    MAX_TITLE_CHARS,
    chunk_entry,
)

USERS = ("primary", "other")
SEARCH_USER = "primary"
LIFECYCLES = ("active", "deleted", "foreign_identity")

# A second, deliberately different index identity for the wrong-identity trap rows.
FOREIGN_IDENTITY = {
    "chunking_version": "journal-chunking-ai005-trap",
    "embedding_provider": "openai",
    "embedding_model": "ai005-foreign-identity-trap",
}

# Deterministic ids (uuid5) keep the function's id tie-breaks reproducible between runs.
_NAMESPACE = uuid.UUID("5a1f6c2e-0a05-4e1b-9d1c-a10050000005")


@dataclass(frozen=True)
class ChunkRef:
    entry: str
    ordinal: int


@dataclass(frozen=True)
class CorpusChunk:
    entry: str
    ordinal: int
    start: int
    end: int
    text: str
    embedding_input: str


@dataclass(frozen=True)
class Entry:
    key: str
    user: str
    occurred_at: str
    title: str | None
    content: str
    lifecycle: str

    @property
    def occurred_at_utc(self) -> datetime:
        return datetime.fromisoformat(
            self.occurred_at.replace("Z", "+00:00")
        ).astimezone(UTC)


@dataclass(frozen=True)
class Evidence:
    entry: str
    quote: str
    relevant_chunks: tuple[ChunkRef, ...]


@dataclass(frozen=True)
class Corpus:
    dataset: str
    entries: tuple[Entry, ...]
    chunks: tuple[CorpusChunk, ...]

    def entry(self, key: str) -> Entry:
        for entry in self.entries:
            if entry.key == key:
                return entry
        raise KeyError(key)

    def chunks_of(self, key: str) -> tuple[CorpusChunk, ...]:
        return tuple(chunk for chunk in self.chunks if chunk.entry == key)

    def user_id(self, user: str) -> uuid.UUID:
        return uuid.uuid5(_NAMESPACE, f"{self.dataset}/user/{user}")

    def entry_id(self, key: str) -> uuid.UUID:
        return uuid.uuid5(_NAMESPACE, f"{self.dataset}/entry/{key}")

    def chunk_id(self, key: str, ordinal: int) -> uuid.UUID:
        return uuid.uuid5(_NAMESPACE, f"{self.dataset}/chunk/{key}/{ordinal}")

    def chunking_digest(self) -> str:
        """SHA-256 of every (entry, ordinal, start, end) the production chunker
        produced."""
        spans = [[c.entry, c.ordinal, c.start, c.end] for c in self.chunks]
        payload = json.dumps(
            {"chunking_version": CHUNKING_VERSION, "spans": spans},
            separators=(",", ":"),
        )
        return hashlib.sha256(payload.encode("utf-8")).hexdigest()


def _text(value, label: str, *, limit: int | None = None) -> str:
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"{label} must be a nonempty string")
    if limit is not None and len(value) > limit:
        raise ValueError(f"{label} exceeds {limit} characters")
    return value


def parse_corpus(dataset: str, raw) -> Corpus:
    if not isinstance(raw, dict) or set(raw) != {"entries"}:
        raise ValueError("corpus requires exactly: entries")
    if not isinstance(raw["entries"], list) or not raw["entries"]:
        raise ValueError("corpus entries must be a nonempty list")
    entries = []
    keys = set()
    for item in raw["entries"]:
        required = {"key", "user", "occurred_at", "title", "content", "lifecycle"}
        if not isinstance(item, dict) or set(item) != required:
            raise ValueError(f"corpus entry requires exactly {sorted(required)}")
        key = _text(item["key"], "entry key")
        if key in keys:
            raise ValueError(f"duplicate entry key: {key}")
        keys.add(key)
        if item["user"] not in USERS:
            raise ValueError(f"{key}: unknown user")
        if item["lifecycle"] not in LIFECYCLES:
            raise ValueError(f"{key}: unknown lifecycle")
        title = item["title"]
        if title is not None:
            _text(title, f"{key} title", limit=MAX_TITLE_CHARS)
            if title != title.strip():
                raise ValueError(
                    f"{key}: title must be trimmed (JRN-001 stores it trimmed)"
                )
        content = _text(item["content"], f"{key} content", limit=MAX_CONTENT_CHARS)
        occurred = _text(item["occurred_at"], f"{key} occurred_at")
        if not occurred.endswith("Z"):
            raise ValueError(f"{key}: occurred_at must be UTC (Z)")
        datetime.fromisoformat(occurred.replace("Z", "+00:00"))
        entries.append(
            Entry(key, item["user"], occurred, title, content, item["lifecycle"])
        )
    chunks = []
    for entry in entries:
        produced = chunk_entry(entry.title, entry.content)
        # The production chunker's own spans locate each chunk exactly; they must agree
        # with its public output (repeated text would make str.find ambiguous).
        spans = chunking._spans(entry.content)
        if [entry.content[start:end] for start, end in spans] != [
            c.text for c in produced
        ]:
            raise ValueError(f"{entry.key}: chunk spans disagree with chunk_entry")
        chunks.extend(
            CorpusChunk(entry.key, c.ordinal, start, end, c.text, c.embedding_input)
            for c, (start, end) in zip(produced, spans, strict=True)
        )
    return Corpus(dataset, tuple(entries), tuple(chunks))


def resolve_evidence(corpus: Corpus, raw, *, allowed_users=(SEARCH_USER,)) -> Evidence:
    if not isinstance(raw, dict) or set(raw) != {"entry", "quote"}:
        raise ValueError("evidence requires exactly: entry, quote")
    entry = corpus.entry(_text(raw["entry"], "evidence entry"))
    if entry.user not in allowed_users or entry.lifecycle != "active":
        raise ValueError(
            f"evidence {entry.key} must be an active entry of the searched user"
        )
    quote = _text(raw["quote"], "evidence quote")
    if entry.content.count(quote) != 1:
        raise ValueError(f"evidence quote must occur exactly once in {entry.key}")
    start = entry.content.index(quote)
    end = start + len(quote)
    chunks = corpus.chunks_of(entry.key)
    containing = [c for c in chunks if c.start <= start and end <= c.end]
    relevant = containing or [c for c in chunks if c.start < end and start < c.end]
    if not relevant:
        raise ValueError(f"evidence quote in {entry.key} is in no chunk")
    return Evidence(
        entry.key, quote, tuple(ChunkRef(c.entry, c.ordinal) for c in relevant)
    )
