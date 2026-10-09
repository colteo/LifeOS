"""AI-005: the production retrieval path over a frozen synthetic corpus.

Indexing replays what `POST /api/journal/memory/sync` does for each entry, with
production code wherever it exists:
- `lifeos_ai.journal_memory.indexing.index_entry` (production chunker + embedder, one
  batch per entry) builds the chunks and vectors;
- the answer is checked like .NET `JournalMemoryValidation.IsValidIndex` (pinned
  identity, contiguous ordinals, chunks are substrings of the content, 1536 finite
  values);
- chunks are inserted with the .NET store's columns into the real schema.

Retrieval replays `JournalMemoryRetrieval.RetrieveAsync`: the trimmed question is
embedded by `lifeos_ai.journal_memory.indexing.embed_query`, the query identity is
checked like `IsValidQueryEmbedding`, and the database's own `search_journal_memory_v1`
ranks the chunks. Nothing here ranks, filters, fuses or thresholds results.
"""

import asyncio
import time
from collections.abc import Callable
from dataclasses import dataclass

from lifeos_ai.journal_memory.chunking import CHUNKING_VERSION
from lifeos_ai.journal_memory.indexing import embed_query, index_entry
from lifeos_ai.journal_memory.schema import EmbedQueryRequest, IndexEntryRequest

from lifeos_ai_evals.journal_memory.corpus import FOREIGN_IDENTITY, SEARCH_USER, Corpus
from lifeos_ai_evals.journal_memory.database import DisposableDatabase, SearchRow
from lifeos_ai_evals.journal_memory.identity import RETRIEVAL


@dataclass(frozen=True)
class Hit:
    """One returned row, attributed to the frozen corpus (no text: exports stay
    small)."""

    rank: int
    entry: str | None  # None: the row is not from the frozen corpus
    user: str | None
    lifecycle: str | None
    ordinal: int
    vector_rank: int | None
    lexical_rank: int | None
    score: float
    text_matches: bool


@dataclass(frozen=True)
class Retrieval:
    question: str
    rows: tuple[SearchRow, ...]
    hits: tuple[Hit, ...]
    embedding_seconds: float
    retrieval_seconds: float
    embedding_attempts: int | None
    embedding_input_tokens: int | None


class MemoryIndex:
    def __init__(
        self,
        corpus: Corpus,
        embedder_factory: Callable[[], object],
        *,
        query_embedder_factory: Callable[[], object] | None = None,
        database_factory: Callable[[], DisposableDatabase] = DisposableDatabase,
        schema: str | None = None,
        telemetry=None,
    ):
        self.corpus = corpus
        self._embedder_factory = embedder_factory
        # Index and query embeddings may record separate telemetry; same identity
        # required.
        self._query_embedder_factory = query_embedder_factory or embedder_factory
        self._database_factory = database_factory
        self._schema = schema
        self._telemetry = telemetry
        self.database: DisposableDatabase | None = None
        probe = embedder_factory()
        self.identity = {
            "chunking_version": CHUNKING_VERSION,
            "embedding_provider": probe.provider,
            "embedding_model": probe.model,
            "embedding_dimensions": probe.dimensions,
        }
        asyncio.run(probe.aclose())
        self._by_id = {corpus.entry_id(entry.key): entry for entry in corpus.entries}
        self._texts = {(c.entry, c.ordinal): c.text for c in corpus.chunks}
        self.indexing: dict = {}

    # -- build --

    def build(self) -> None:
        database = self._database_factory()
        database.start()
        self.database = database
        try:
            database.apply_schema(self._schema)
            database.assert_empty()
            for user in sorted({entry.user for entry in self.corpus.entries}):
                database.insert_user(self.corpus.user_id(user))
            started = time.perf_counter()
            indexed = asyncio.run(self._index_all())
            seconds = time.perf_counter() - started
            for entry in self.corpus.entries:
                if entry.lifecycle == "deleted":
                    database.delete_entry(
                        self.corpus.entry_id(entry.key), self.corpus.user_id(entry.user)
                    )
            self.indexing = {
                "entries": len(self.corpus.entries),
                "chunks": indexed,
                "embedding_requests": len(self.corpus.entries),
                "seconds": seconds,
                "lifecycles": {
                    lifecycle: sum(
                        e.lifecycle == lifecycle for e in self.corpus.entries
                    )
                    for lifecycle in ("active", "deleted", "foreign_identity")
                },
            }
        except BaseException:
            self.close()
            raise

    async def _index_all(self) -> int:
        embedder = self._embedder_factory()
        total = 0
        try:
            for entry in self.corpus.entries:
                entry_id = self.corpus.entry_id(entry.key)
                user_id = self.corpus.user_id(entry.user)
                self.database.insert_entry(
                    entry_id, user_id, entry.occurred_at_utc, entry.title, entry.content
                )
                response = await index_entry(
                    embedder,
                    IndexEntryRequest(title=entry.title, content=entry.content),
                )
                self._validate_index(entry, response)
                identity = (
                    FOREIGN_IDENTITY
                    if entry.lifecycle == "foreign_identity"
                    else self.identity
                )
                self.database.insert_chunks(
                    entry_id,
                    user_id,
                    [
                        (
                            self.corpus.chunk_id(entry.key, chunk.ordinal),
                            chunk.ordinal,
                            chunk.text,
                            chunk.embedding,
                        )
                        for chunk in response.chunks
                    ],
                    identity,
                )
                total += len(response.chunks)
        finally:
            await embedder.aclose()
        return total

    def _validate_index(self, entry, response) -> None:
        # .NET JournalMemoryValidation.IsValidIndex, plus agreement with the frozen
        # chunk map.
        expected = [c.text for c in self.corpus.chunks_of(entry.key)]
        if (
            response.chunking_version != self.identity["chunking_version"]
            or response.embedding_provider != self.identity["embedding_provider"]
            or response.embedding_model != self.identity["embedding_model"]
            or response.embedding_dimensions != self.identity["embedding_dimensions"]
            or [c.ordinal for c in response.chunks] != list(range(len(response.chunks)))
            or any(c.text not in entry.content for c in response.chunks)
            or [c.text for c in response.chunks] != expected
        ):
            raise ValueError("index answer does not match the production contract")

    # -- retrieval --

    def retrieve(self, question: str, limit: int = RETRIEVAL["limit"]) -> Retrieval:
        if self.database is None:
            self.build()
        text = question.strip()
        mark = self._telemetry.mark() if self._telemetry is not None else None
        started = time.perf_counter()
        embedded = asyncio.run(self._embed(text))
        embedding_seconds = time.perf_counter() - started
        if (
            embedded.provider != self.identity["embedding_provider"]
            or embedded.model != self.identity["embedding_model"]
            or embedded.dimensions != self.identity["embedding_dimensions"]
        ):
            raise ValueError("query embedding identity differs from the index identity")
        started = time.perf_counter()
        rows = self.database.search(
            self.corpus.user_id(SEARCH_USER),
            embedded.embedding,
            text,
            self.identity,
            limit,
        )
        retrieval_seconds = time.perf_counter() - started
        attempts, tokens = None, None
        if mark is not None:
            observed = self._telemetry.since(mark)
            attempts = len(observed)
            counts = [
                a.usage["input_tokens"]
                for a in observed
                if a.usage and a.usage.get("input_tokens") is not None
            ]
            tokens = sum(counts) if counts else None
        return Retrieval(
            text,
            tuple(rows),
            tuple(self._hit(row) for row in rows),
            embedding_seconds,
            retrieval_seconds,
            attempts,
            tokens,
        )

    async def _embed(self, text: str):
        embedder = self._query_embedder_factory()
        try:
            return await embed_query(embedder, EmbedQueryRequest(question=text))
        finally:
            await embedder.aclose()

    def _hit(self, row: SearchRow) -> Hit:
        entry = self._by_id.get(row.entry_id)
        key = entry.key if entry else None
        return Hit(
            rank=row.retrieval_rank,
            entry=key,
            user=entry.user if entry else None,
            lifecycle=entry.lifecycle if entry else None,
            ordinal=row.chunk_ordinal,
            vector_rank=row.vector_rank,
            lexical_rank=row.lexical_rank,
            score=row.rrf_score,
            text_matches=self._texts.get((key, row.chunk_ordinal)) == row.chunk_text,
        )

    def metadata(self) -> dict:
        return {
            "database": dict(self.database.metadata) if self.database else None,
            "index_identity": dict(self.identity),
            "indexing": dict(self.indexing),
            "search_calls": self.database.search_calls if self.database else 0,
            "corpus_chunking_digest": self.corpus.chunking_digest(),
        }

    def close(self) -> None:
        if self.database is not None:
            self.database.close()
