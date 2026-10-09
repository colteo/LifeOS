"""Journal retrieval systems: the live production control, the live AI-005.1
journal-retrieval-v2 candidate and the offline sanity system.

All run the same production path (memory_index.py) on a fresh disposable database. The
control and the candidate differ only in the SQL function that ranks (each verified
against its migration); the offline sanity system differs from the control only in the
embedder. The database is built on the first case and destroyed by `close()` (the CLI
calls it; tests use it as a context manager).
"""

from functools import partial

from lifeos_ai_evals.core.engine import Prediction
from lifeos_ai_evals.evaluators.journal_retrieval.model import (
    RetrievalOutput,
    RetrievalQuery,
)
from lifeos_ai_evals.journal_memory import embedders
from lifeos_ai_evals.journal_memory.database import DisposableDatabase
from lifeos_ai_evals.journal_memory.identity import RETRIEVAL, RETRIEVAL_CONTROL
from lifeos_ai_evals.journal_memory.memory_index import MemoryIndex
from lifeos_ai_evals.production import runtime


class RetrievalPredictor:
    version = "1.0.0"

    def __init__(
        self,
        *,
        name: str,
        identity: dict,
        index_embedder,
        query_embedder,
        database_factory=DisposableDatabase,
        schema: str | None = None,
        index_telemetry: runtime.Telemetry | None = None,
        query_telemetry: runtime.Telemetry | None = None,
        runtime_settings: dict | None = None,
    ):
        self.name = name
        self.identity = identity
        self._index_embedder = index_embedder
        self._query_embedder = query_embedder
        self._database_factory = database_factory
        self._schema = schema
        self.index_telemetry = index_telemetry
        self.query_telemetry = query_telemetry
        self.index: MemoryIndex | None = None
        self.configuration = {
            **identity,
            "runtime": runtime_settings or {},
            "path": (
                "production chunk_entry/index_entry/embed_query + disposable "
                "pgvector + "
                f"{identity['retrieval']['function']}"
            ),
        }

    def predict(self, value: RetrievalQuery) -> Prediction[RetrievalOutput]:
        if self.index is None:
            self.index = MemoryIndex(
                value.corpus,
                self._index_embedder,
                query_embedder_factory=self._query_embedder,
                database_factory=self._database_factory,
                schema=self._schema,
                telemetry=self.query_telemetry,
            )
            self.index.build()
            if self.index_telemetry is not None:
                # One logical index request per entry (production: one batch per entry).
                self.index_telemetry.logical_requests = len(value.corpus.entries)
        elif self.index.corpus is not value.corpus:
            raise ValueError("one run evaluates one frozen corpus")
        if self.query_telemetry is not None:
            self.query_telemetry.logical_requests += 1
        retrieval = self.index.retrieve(value.question, RETRIEVAL["limit"])
        return Prediction(
            RetrievalOutput(
                retrieval.hits,
                RETRIEVAL["limit"],
                retrieval.embedding_seconds,
                retrieval.retrieval_seconds,
                retrieval.embedding_attempts,
                retrieval.embedding_input_tokens,
            )
        )

    def close(self) -> None:
        if self.index is not None:
            self.index.close()

    def __enter__(self):
        return self

    def __exit__(self, *_exc):
        self.close()

    def experiment_metadata(self) -> dict:
        index = self.index.metadata() if self.index else {}
        telemetry = {}
        if self.index_telemetry is not None:
            telemetry["index_embedding"] = self.index_telemetry.summary()
        if self.query_telemetry is not None:
            telemetry["query_embedding"] = self.query_telemetry.summary()
        return {
            "reproducibility": (
                "experiment identity; provider embeddings may vary slightly "
                "between runs"
            ),
            "retrieval_run": index,
            "telemetry": telemetry,
        }


def offline_sanity(**kwargs) -> RetrievalPredictor:
    """Lab hashing embedder: no provider call. Validates the pipeline and the scorer
    only."""
    identity = {
        **RETRIEVAL_CONTROL,
        "system": "journal-retrieval-offline-sanity",
        "embedding": {
            "provider": embedders.HashingEmbedder.provider,
            "model": embedders.HashingEmbedder.model,
            "dimensions": embedders.HashingEmbedder.dimensions,
        },
    }
    return RetrievalPredictor(
        name="journal-retrieval-offline-sanity",
        identity=identity,
        index_embedder=embedders.HashingEmbedder,
        query_embedder=embedders.HashingEmbedder,
        **kwargs,
    )


def production_control(control: dict, **kwargs) -> RetrievalPredictor:
    """The live control: production Gemini adapter; GEMINI_API_KEY from the
    environment."""
    return _live("journal-retrieval-production-control", control, **kwargs)


def retrieval_v2_candidate(candidate: dict, **kwargs) -> RetrievalPredictor:
    """AI-005.1: the control's live path (same adapter, embedding, chunking, corpus
    indexing) ranked by the candidate's own migration-verified SQL function."""
    return _live("journal-retrieval-v2-candidate", candidate, **kwargs)


def _live(
    name: str,
    identity: dict,
    *,
    api_key: str | None = None,
    transport=None,
    sleep=None,
    **kwargs,
) -> RetrievalPredictor:
    key = api_key or (
        "offline-test-key" if transport else embedders.gemini_key_from_environment()
    )
    model = identity["embedding"]["model"]
    index_telemetry, query_telemetry = runtime.Telemetry(), runtime.Telemetry()

    def factory(telemetry):
        return lambda: embedders.live_embedder(key, model, telemetry, transport, sleep)

    kwargs.setdefault(
        "database_factory",
        partial(DisposableDatabase, function=identity["retrieval"]["function"]),
    )
    predictor = RetrievalPredictor(
        name=name,
        identity=identity,
        index_embedder=factory(index_telemetry),
        query_embedder=factory(query_telemetry),
        index_telemetry=index_telemetry,
        query_telemetry=query_telemetry,
        runtime_settings={
            "embedding": dict(embedders.EMBEDDING_RUNTIME),
            "adapter": (
                "lifeos_ai.journal_memory.gemini_embeddings.GeminiJournalEmbedder"
            ),
        },
        **kwargs,
    )
    return predictor
