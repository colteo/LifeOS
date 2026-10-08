"""End-to-end systems: synthetic entries -> production chunker -> embeddings ->
disposable pgvector -> search_journal_memory_v1 -> Ask mirror -> answerer -> structured
answer + citations.

- live control: production OpenAI embeddings + production Groq answerer (both keys
  required);
- offline sanity: lab hashing embedder + the extractive baseline answerer (no provider).
  Retrieval and answer timings and token counts are kept separate.
"""

import time

from lifeos_ai_evals.core.engine import Prediction
from lifeos_ai_evals.evaluators.journal_answer.baseline import ExtractiveBaseline
from lifeos_ai_evals.evaluators.journal_answer.detectors import claim_classes
from lifeos_ai_evals.evaluators.journal_answer.live import LiveAnswerSession
from lifeos_ai_evals.evaluators.journal_answer.model import AnswerOutput
from lifeos_ai_evals.evaluators.journal_rag.model import RagOutput, RagQuery
from lifeos_ai_evals.journal_memory import ask_mirror, embedders
from lifeos_ai_evals.journal_memory.database import DisposableDatabase
from lifeos_ai_evals.journal_memory.identity import RAG_CONTROL
from lifeos_ai_evals.journal_memory.memory_index import MemoryIndex
from lifeos_ai_evals.production import runtime


class RagPredictor:
    version = "1.0.0"

    def __init__(
        self,
        *,
        name: str,
        identity: dict,
        index_embedder,
        query_embedder,
        answer,
        database_factory=DisposableDatabase,
        schema: str | None = None,
        index_telemetry: runtime.Telemetry | None = None,
        query_telemetry: runtime.Telemetry | None = None,
        answer_session: LiveAnswerSession | None = None,
        runtime_settings: dict | None = None,
    ):
        self.name = name
        self.identity = identity
        self._index_embedder = index_embedder
        self._query_embedder = query_embedder
        self._answer = answer
        self._database_factory = database_factory
        self._schema = schema
        self.index_telemetry = index_telemetry
        self.query_telemetry = query_telemetry
        self.answer_session = answer_session
        self.index: MemoryIndex | None = None
        self.configuration = {
            **identity,
            "ask_mirror": ask_mirror.VERSION,
            "runtime": runtime_settings or {},
        }

    def predict(self, value: RagQuery) -> Prediction[RagOutput]:
        started = time.perf_counter()
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
                self.index_telemetry.logical_requests = len(value.corpus.entries)
            started = (
                time.perf_counter()
            )  # indexing is not part of a question's latency
        elif self.index.corpus is not value.corpus:
            raise ValueError("one run evaluates one frozen corpus")
        if self.query_telemetry is not None:
            self.query_telemetry.logical_requests += 1
        retrieval = self.index.retrieve(
            value.question, ask_mirror.ANSWER_CONTEXT_CHUNKS
        )
        if not retrieval.rows:
            # AskJournalMemoryHandler: no evidence -> insufficient_evidence, no model
            # call.
            answer = AnswerOutput(
                True, "insufficient_evidence", "insufficient_evidence", "", ()
            )
            answer_seconds, called, classes = None, False, ()
        else:
            request = ask_mirror.answer_request(retrieval.question, retrieval.rows)
            answer_started = time.perf_counter()
            answer = self._answer(request)
            answer_seconds = time.perf_counter() - answer_started
            called = True
            classes = tuple(
                sorted(
                    claim_classes(" ".join(row.chunk_text for row in retrieval.rows))
                )
            )
        cited = tuple(ask_mirror.cited_hits(answer.citations, retrieval.hits))
        return Prediction(
            RagOutput(
                retrieval.hits,
                called,
                answer,
                cited,
                retrieval.embedding_seconds,
                retrieval.retrieval_seconds,
                answer_seconds,
                time.perf_counter() - started,
                retrieval.embedding_input_tokens,
                classes,
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
        telemetry = {}
        if self.index_telemetry is not None:
            telemetry["index_embedding"] = self.index_telemetry.summary()
        if self.query_telemetry is not None:
            telemetry["query_embedding"] = self.query_telemetry.summary()
        if self.answer_session is not None:
            telemetry["answer"] = self.answer_session.telemetry.summary()
        return {
            "reproducibility": "experiment identity; probabilistic outputs",
            "retrieval_run": self.index.metadata() if self.index else {},
            "telemetry": telemetry,
        }


def offline_sanity(**kwargs) -> RagPredictor:
    baseline = ExtractiveBaseline()
    return RagPredictor(
        name="journal-rag-offline-sanity",
        identity={
            "system": "journal-rag-offline-sanity",
            "embedding": embedders.HashingEmbedder.model,
            "answer": baseline.name,
        },
        index_embedder=embedders.HashingEmbedder,
        query_embedder=embedders.HashingEmbedder,
        answer=lambda request: baseline.predict(request).value,
        **kwargs,
    )


def production_control(
    control: dict,
    *,
    openai_key: str | None = None,
    groq_key: str | None = None,
    embedding_transport=None,
    answer_transport=None,
    sleep=None,
    case_sleep=time.sleep,
    **kwargs,
) -> RagPredictor:
    """Live: OPENAI_API_KEY and GROQ_API_KEY from the environment (checked before any
    call)."""
    if control != RAG_CONTROL:
        raise ValueError("AI-005 evaluates only the production control")
    key = openai_key or (
        "offline-test-key"
        if embedding_transport
        else embedders.openai_key_from_environment()
    )
    session = LiveAnswerSession(
        api_key=groq_key,
        transport=answer_transport,
        sleep=sleep,
        case_sleep=case_sleep,
    )
    model = control["retrieval"]["embedding"]["model"]
    index_telemetry, query_telemetry = runtime.Telemetry(), runtime.Telemetry()

    def factory(telemetry):
        return lambda: embedders.live_embedder(
            key, model, telemetry, embedding_transport, sleep
        )

    return RagPredictor(
        name="journal-rag-production-control",
        identity=control,
        index_embedder=factory(index_telemetry),
        query_embedder=factory(query_telemetry),
        answer=session.answer,
        index_telemetry=index_telemetry,
        query_telemetry=query_telemetry,
        answer_session=session,
        runtime_settings={
            "embedding": dict(embedders.EMBEDDING_RUNTIME),
            "answer": dict(runtime.RUNTIME),
        },
        **kwargs,
    )
