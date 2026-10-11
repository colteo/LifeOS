"""AI-006: the OpenAI final candidate in the lab, offline (scripted OpenAI transports,
no key, no network). Identity drift, production-adapter reuse and the final gates."""

import ast
import json
import random
from pathlib import Path

import httpx
import pytest
from journal_fakes import requires_docker
from lifeos_ai.journal_memory import openai_answer as production_answer
from lifeos_ai.journal_memory import openai_embeddings as production_embeddings
from lifeos_ai.journal_memory.prompt import (
    PROMPT_VERSION,
    SYSTEM_PROMPT,
    build_messages,
)
from lifeos_ai.journal_memory.schema import ANSWER_OUTPUT_SCHEMA
from live_fakes import no_sleep

import lifeos_ai_evals
from lifeos_ai_evals.core.engine import Dataset, evaluate
from lifeos_ai_evals.evaluators.journal_answer import plugin as answer_plugin
from lifeos_ai_evals.evaluators.journal_answer.openai_live import OpenAIAnswerPredictor
from lifeos_ai_evals.evaluators.journal_rag import plugin as rag_plugin
from lifeos_ai_evals.evaluators.journal_rag import predictor as rag_predictor
from lifeos_ai_evals.evaluators.journal_retrieval import plugin as retrieval_plugin
from lifeos_ai_evals.evaluators.journal_retrieval import predictor as retrieval
from lifeos_ai_evals.journal_memory import database as db
from lifeos_ai_evals.journal_memory import final_acceptance, openai_final
from lifeos_ai_evals.journal_memory.candidate import RETRIEVAL_CANDIDATE
from lifeos_ai_evals.journal_memory.embedders import HashingEmbedder
from lifeos_ai_evals.journal_memory.identity import (
    ANSWER_CONTROL,
    RAG_CONTROL,
    RETRIEVAL_CONTROL,
)
from lifeos_ai_evals.production import runtime

ROOT = Path(__file__).resolve().parents[1]
LAB = Path(lifeos_ai_evals.__file__).parent
CONFIGS = {
    "journal_retrieval": openai_final.RETRIEVAL_FINAL,
    "journal_answer": openai_final.ANSWER_FINAL,
    "journal_rag": openai_final.RAG_FINAL,
}
ANSWERED = {"status": "answered", "answer": "You had tajarin.", "citations": ["S1"]}
INSUFFICIENT = {"status": "insufficient_evidence", "answer": "", "citations": []}
RESPONSES_USAGE = {
    "input_tokens": 900,
    "input_tokens_details": {"cached_tokens": 512},
    "output_tokens": 120,
    "output_tokens_details": {"reasoning_tokens": 96},
    "total_tokens": 1020,
}


class EmbeddingsTransport(httpx.AsyncBaseTransport):
    """Answers embedding requests with deterministic hashing vectors of each input as
    the production adapter sent it, in a SHUFFLED order (indexes are authoritative)."""

    def __init__(self, *failures: int):
        self.failures = list(failures)
        self.requests: list[httpx.Request] = []
        self._random = random.Random(6)

    async def handle_async_request(self, request: httpx.Request) -> httpx.Response:
        self.requests.append(request)
        if self.failures:
            return httpx.Response(self.failures.pop(0), json={"error": {}})
        texts = json.loads(request.content)["input"]
        data = [
            {"object": "embedding", "index": i, "embedding": HashingEmbedder.vector(t)}
            for i, t in enumerate(texts)
        ]
        self._random.shuffle(data)
        usage = {"prompt_tokens": 7 * len(texts), "total_tokens": 7 * len(texts)}
        return httpx.Response(
            200, json={"object": "list", "data": data, "usage": usage}
        )

    def bodies(self) -> list[dict]:
        return [json.loads(request.content) for request in self.requests]


class ResponsesTransport(httpx.AsyncBaseTransport):
    def __init__(self, *outputs):
        self.outputs = list(outputs)
        self.requests: list[httpx.Request] = []

    async def handle_async_request(self, request: httpx.Request) -> httpx.Response:
        self.requests.append(request)
        output = self.outputs.pop(0)
        if isinstance(output, httpx.Response):
            return output
        return httpx.Response(200, json=completed(output))

    def bodies(self) -> list[dict]:
        return [json.loads(request.content) for request in self.requests]


def completed(answer: dict) -> dict:
    return {
        "id": "resp_lab_secret",
        "object": "response",
        "status": "completed",
        "output": [
            {"type": "reasoning", "id": "rs_1", "summary": []},
            {
                "type": "message",
                "role": "assistant",
                "content": [{"type": "output_text", "text": json.dumps(answer)}],
            },
        ],
        "usage": RESPONSES_USAGE,
    }


# ---- Frozen identities and checked-in configs ----


@pytest.mark.parametrize("name", sorted(CONFIGS))
def test_the_checked_in_configs_are_the_frozen_candidate(name):
    path = ROOT / "experiments" / name / "openai-final.json"
    assert json.loads(path.read_text(encoding="utf-8")) == CONFIGS[name]


def test_retrieval_differs_from_ai005_1_v2_only_in_the_embedding_identity():
    final, v2 = openai_final.RETRIEVAL_FINAL, RETRIEVAL_CANDIDATE
    assert set(final) == set(v2) == set(RETRIEVAL_CONTROL)
    assert {key for key in final if final[key] != v2[key]} == {"system", "embedding"}
    assert final["embedding"] == {
        "provider": "openai",
        "model": "text-embedding-3-small",
        "dimensions": 1536,
    }
    # Retrieval policy: v2 exactly (candidate depths, RRF, limit, no threshold).
    assert final["retrieval"] == v2["retrieval"]
    assert final["retrieval"]["version"] == "journal-retrieval-v2"
    assert final["retrieval"]["function"] == "search_journal_memory_v2"
    for key in ("vector_candidates", "lexical_candidates", "rrf_k", "limit"):
        assert final["retrieval"][key] == RETRIEVAL_CONTROL["retrieval"][key], key
    assert final["retrieval"]["similarity_threshold"] is None
    for key in ("chunking_version", "database_image"):
        assert final[key] == RETRIEVAL_CONTROL[key] == v2[key], key


def test_answer_differs_from_the_control_only_in_provider_model_and_api_settings():
    final = openai_final.ANSWER_FINAL
    same = {"prompt_version", "context_version", "structured_output"}
    for key in same:
        assert final[key] == ANSWER_CONTROL[key], key
    assert set(final) - same == {"provider", "model", "api", "generation_settings"}
    assert (final["provider"], final["model"], final["api"]) == (
        "openai",
        "gpt-6.1-sol",
        "responses",
    )
    assert final["prompt_version"] == "journal-rag-answer-v1" == PROMPT_VERSION
    assert final["generation_settings"] == {
        "reasoning_effort": "medium",
        "store": False,
        "strict_structured_output": True,
        "max_output_tokens": 8192,
        "tools": [],
    }
    assert "temperature" not in json.dumps(final) and "top_p" not in json.dumps(final)


def test_rag_combines_exactly_the_two_candidates():
    assert openai_final.RAG_FINAL == {
        "retrieval": openai_final.RETRIEVAL_FINAL,
        "answer": openai_final.ANSWER_FINAL,
    }
    assert set(openai_final.RAG_FINAL) == set(RAG_CONTROL)


def test_the_identity_values_come_from_the_production_adapters():
    final = openai_final.ANSWER_FINAL
    assert final["provider"] == production_answer.OpenAIJournalAnswerer.provider
    assert final["model"] == production_answer.DEFAULT_ANSWER_MODEL
    assert final["generation_settings"]["reasoning_effort"] == (
        production_answer.REASONING_EFFORT
    )
    assert final["generation_settings"]["max_output_tokens"] == (
        production_answer.OpenAIAnswerSettings(api_key="-").max_output_tokens
    )
    embedding = openai_final.RETRIEVAL_FINAL["embedding"]
    assert embedding == {
        "provider": production_embeddings.OpenAIJournalEmbedder.provider,
        "model": production_embeddings.DEFAULT_EMBEDDING_MODEL,
        "dimensions": production_embeddings.EMBEDDING_DIMENSIONS,
    }


# ---- Production adapters, not lab copies ----


def test_the_lab_instantiates_the_production_adapters():
    telemetry = runtime.Telemetry()
    embedder = openai_final.live_embedder("k", telemetry)
    answerer = openai_final.live_answerer("k", telemetry)
    assert type(embedder) is production_embeddings.OpenAIJournalEmbedder
    assert type(answerer) is production_answer.OpenAIJournalAnswerer
    assert (embedder.model, embedder.dimensions) == ("text-embedding-3-small", 1536)
    assert (answerer.model, answerer.prompt_version) == (
        "gpt-6.1-sol",
        "journal-rag-answer-v1",
    )


OPENAI_WIRE_DETAILS = (
    "api.openai.com",
    "/responses",
    "/embeddings",
    "Bearer",
    "encoding_format",
    "previous_response_id",
    "output_text",
    "text.format",
)


def test_the_lab_never_reproduces_openai_http_formatting_parsing_or_schema():
    for path in LAB.rglob("*.py"):
        text = path.read_text(encoding="utf-8")
        for marker in OPENAI_WIRE_DETAILS:
            assert marker not in text, (path.relative_to(LAB).as_posix(), marker)
    # Only the identity module imports the production OpenAI adapters.
    importers = set()
    for path in LAB.rglob("*.py"):
        for node in ast.walk(ast.parse(path.read_text(encoding="utf-8"))):
            if isinstance(node, ast.ImportFrom) and (node.module or "").startswith(
                "lifeos_ai.journal_memory.openai"
            ):
                importers.add(path.relative_to(LAB).as_posix())
    assert importers == {"journal_memory/openai_final.py"}


def test_the_answer_request_is_the_production_body_byte_for_byte():
    case = answer_plugin.load(answer_plugin.default_dataset()).cases[0]
    transport = ResponsesTransport(ANSWERED)
    system = OpenAIAnswerPredictor(
        transport=transport, sleep=no_sleep, case_sleep=lambda _: None
    )
    system.predict(case.input)
    production = production_answer.OpenAIJournalAnswerer(
        production_answer.OpenAIAnswerSettings(api_key="unused")
    )
    assert transport.bodies() == [production.request_body(case.input)]
    body = transport.bodies()[0]
    assert body["store"] is False and body["reasoning"] == {"effort": "medium"}
    assert body["text"]["format"]["strict"] is True
    assert body["text"]["format"]["schema"] == ANSWER_OUTPUT_SCHEMA
    assert body["input"] == build_messages(case.input)
    assert "tools" not in body and "previous_response_id" not in body


# ---- Keys and selection ----


def test_without_openai_api_key_nothing_starts(monkeypatch):
    monkeypatch.delenv("OPENAI_API_KEY", raising=False)
    monkeypatch.setenv("GEMINI_API_KEY", "not-used")
    monkeypatch.setenv("GROQ_API_KEY", "not-used")
    for name, plugin in (
        ("journal_retrieval", retrieval_plugin),
        ("journal_answer", answer_plugin),
        ("journal_rag", rag_plugin),
    ):
        path = ROOT / "experiments" / name / "openai-final.json"
        with pytest.raises(ValueError, match="OPENAI_API_KEY is required"):
            plugin.experiment(path)


@pytest.mark.parametrize("name", sorted(CONFIGS))
def test_any_other_openai_identity_is_rejected(name, tmp_path, monkeypatch):
    monkeypatch.setenv("OPENAI_API_KEY", "offline-test-key")
    changed = json.loads(json.dumps(CONFIGS[name]))
    target = changed.get("answer", changed)
    target["model" if "model" in target else "system"] = "gpt-other"
    path = tmp_path / "variant.json"
    path.write_text(json.dumps(changed), encoding="utf-8")
    plugin = {
        "journal_retrieval": retrieval_plugin,
        "journal_answer": answer_plugin,
        "journal_rag": rag_plugin,
    }[name]
    with pytest.raises(ValueError):
        plugin.experiment(path)


def test_the_openai_answer_predictor_needs_only_the_openai_key(monkeypatch):
    monkeypatch.delenv("GROQ_API_KEY", raising=False)
    monkeypatch.delenv("GEMINI_API_KEY", raising=False)
    monkeypatch.setenv("OPENAI_API_KEY", "offline-test-key")
    predictor = answer_plugin.experiment(
        ROOT / "experiments/journal_answer/openai-final.json"
    )
    assert isinstance(predictor, OpenAIAnswerPredictor)
    assert predictor.identity == openai_final.ANSWER_FINAL


# ---- Answer path, offline ----


def test_an_answer_case_is_scored_with_responses_tokens_and_no_content():
    case = answer_plugin.load(answer_plugin.default_dataset()).cases[0]
    transport = ResponsesTransport(ANSWERED)
    system = OpenAIAnswerPredictor(
        transport=transport, sleep=no_sleep, case_sleep=lambda _: None
    )
    result = evaluate(
        "journal_answer",
        Dataset("d", "1", (case,), "x"),
        system,
        answer_plugin.scorer(),
    )
    assert result.errors == 0
    assert result.metadata["experiment"] == openai_final.ANSWER_FINAL
    metrics = result.cases[0].score.metrics
    assert (metrics["valid"], metrics["total_tokens"], metrics["attempts"]) == (
        1,
        1020,
        1,
    )
    metadata = system.experiment_metadata()
    assert metadata["token_details"] == {
        "cached_input_tokens": 512,
        "reasoning_tokens": 96,
    }
    telemetry = json.dumps(metadata)
    for secret in (
        "tajarin",
        case.input.question,
        "resp_lab_secret",
        "offline-test-key",
    ):
        assert secret not in telemetry


@pytest.mark.parametrize(
    ("reply", "outcome"),
    [
        ({**ANSWERED, "citations": ["S9"]}, "unknown_citation"),
        (
            httpx.Response(
                200,
                json={**completed(ANSWERED), "status": "incomplete"},
            ),
            "incomplete",
        ),
        (
            httpx.Response(
                200,
                json={
                    **completed(ANSWERED),
                    "output": [
                        {
                            "type": "message",
                            "role": "assistant",
                            "content": [{"type": "refusal", "refusal": "no"}],
                        }
                    ],
                },
            ),
            "refusal",
        ),
    ],
)
def test_invalid_outputs_carry_the_production_outcome(reply, outcome):
    case = answer_plugin.load(answer_plugin.default_dataset()).cases[0]
    system = OpenAIAnswerPredictor(
        transport=ResponsesTransport(reply), sleep=no_sleep, case_sleep=lambda _: None
    )
    output = system.predict(case.input).value
    assert (output.valid, output.outcome) == (False, outcome)


# ---- Final acceptance gates (frozen) ----


def test_the_final_gates_pin_the_frozen_datasets_and_thresholds():
    assert final_acceptance.VERSION == "journal-rag-openai-final-acceptance-v1"
    assert sorted(final_acceptance.RETRIEVAL_DATASETS.values()) == [
        "development",
        "holdout",
    ]
    assert sorted(final_acceptance.ANSWER_DATASETS.values()) == [
        "development",
        "holdout",
    ]
    for evaluator, frozen in (
        ("journal_retrieval", final_acceptance.RETRIEVAL_DATASETS),
        ("journal_answer", final_acceptance.ANSWER_DATASETS),
        ("journal_rag", final_acceptance.RAG_DATASETS),
    ):
        plugin = {
            "journal_retrieval": retrieval_plugin,
            "journal_answer": answer_plugin,
            "journal_rag": rag_plugin,
        }[evaluator]
        files = sorted((ROOT / "datasets" / evaluator).glob("*.json"))
        pinned = {
            (d.name, d.version, d.sha256) for d in (plugin.load(path) for path in files)
        }
        assert pinned == set(frozen), evaluator


def _run(evaluator, identity, dataset, metrics, cases=(), **metadata):
    return {
        "evaluator": evaluator,
        "dataset": {
            "name": dataset[0],
            "version": dataset[1],
            "sha256": dataset[2],
        },
        "case_count": len(cases),
        "errors": 0,
        "aggregate_metrics": metrics,
        "cases": list(cases),
        "metadata": {
            "experiment": identity,
            "scored_cases": len(cases),
            **metadata,
        },
    }


def _retrieval_case(case_id, *, result_count=8, recall=1.0, hit=1, critical=0):
    return {
        "id": case_id,
        "status": "correct" if hit else "incorrect",
        "tags": [],
        "expected": {"safety_critical": bool(critical)},
        "score": {
            "metrics": {
                "answerable": 1,
                "safety_critical": critical,
                "result_count": result_count,
                "recall@8": recall,
                "hit@8": hit,
            }
        },
    }


RETRIEVAL_METRICS = {
    "cross_user_results": 0,
    "deleted_results": 0,
    "wrong_identity_results": 0,
    "unknown_source_results": 0,
    "malformed_ranking": 0,
    "safety_critical_cases": 1,
    "safety_critical_hit@8": 1,
    "hit_rate@8": 0.95,
    "recall@8": 0.85,
    "mrr": 0.7,
}


def _retrieval_run(cases, identity=openai_final.RETRIEVAL_FINAL, **overrides):
    dev = next(
        k for k, v in final_acceptance.RETRIEVAL_DATASETS.items() if v == "holdout"
    )
    return _run(
        "journal_retrieval",
        identity,
        dev,
        {**RETRIEVAL_METRICS, **overrides},
        cases,
        retrieval_run={
            "database": {
                "function": "search_journal_memory_v2",
                "function_verified": True,
            },
            "search_calls": len(cases),
        },
    )


def test_a_passing_retrieval_run_passes_every_final_gate():
    cases = [_retrieval_case("a", critical=1), _retrieval_case("b")]
    report = retrieval_plugin.run_acceptance(_retrieval_run(cases))
    assert report["version"] == final_acceptance.VERSION
    assert report["dataset_role"] == "holdout"
    assert report["all_gates_pass"], [
        g for g in report["gates"] if g["status"] != "pass"
    ]
    names = [gate["gate"] for gate in report["gates"]]
    assert "production_control" not in names and "openai_final_candidate" in names
    assert "candidate_function_verified" in names
    assert "production_function_verified" not in names


@pytest.mark.parametrize(
    ("overrides", "failed"),
    [
        ({"hit_rate@8": 0.89}, "hit_rate@8_min"),
        ({"recall@8": 0.79}, "recall@8_min"),
        ({"mrr": 0.59}, "mrr_min"),
        ({"wrong_identity_results": 1}, "zero_wrong_identity_results"),
        ({"cross_user_results": 1}, "zero_cross_user_results"),
    ],
)
def test_the_ai005_retrieval_thresholds_are_kept(overrides, failed):
    cases = [_retrieval_case("a", critical=1)]
    report = final_acceptance.retrieval(_retrieval_run(cases, **overrides))
    assert {g["gate"] for g in report["gates"] if g["status"] == "fail"} == {failed}


def test_evidence_lost_to_filtering_fails_retrieval():
    cases = [
        _retrieval_case("a", critical=1),
        _retrieval_case("b", result_count=3, recall=0.5),
    ]
    report = final_acceptance.retrieval(_retrieval_run(cases))
    gate = next(
        g for g in report["gates"] if g["gate"] == "no_evidence_lost_to_filtering"
    )
    assert (gate["status"], gate["details"]) == ("fail", ["b"])


def test_the_wrong_function_or_another_identity_or_dataset_fails():
    cases = [_retrieval_case("a", critical=1)]
    run = _retrieval_run(cases)
    run["metadata"]["retrieval_run"]["database"]["function"] = (
        "search_journal_memory_v1"
    )
    assert not final_acceptance.retrieval(run)["all_gates_pass"]
    other = _retrieval_run(cases, identity=RETRIEVAL_CANDIDATE)
    assert not final_acceptance.retrieval(other)["all_gates_pass"]
    changed = _retrieval_run(cases)
    changed["dataset"]["sha256"] = "0" * 64
    report = final_acceptance.retrieval(changed)
    assert report["dataset_role"] is None and not report["all_gates_pass"]


def test_control_and_v2_runs_keep_their_own_ai005_gates():
    cases = [_retrieval_case("a", critical=1)]
    report = retrieval_plugin.run_acceptance(
        _retrieval_run(cases, identity=RETRIEVAL_CONTROL)
    )
    assert report["version"] == "journal-retrieval-acceptance-v1"


ANSWER_METRICS = {
    "valid_output_rate": 1.0,
    "unknown_citation_outputs": 0,
    "unknown_citations": 0,
    "invalid_outputs": 0,
    "expected_status_accuracy": 0.9,
    "answered_accuracy": 0.8,
    "citation_relevance": 0.95,
    "refused_when_answerable": 0,
}


def _answer_case(case_id, *, tags=(), correct=True, answerable=True, answered=True):
    return {
        "id": case_id,
        "status": "correct" if correct else "incorrect",
        "tags": list(tags),
        "score": {
            "metrics": {
                "expected_answered": int(answerable),
                "valid": 1,
                "answered": int(answered),
            }
        },
    }


def _answer_run(cases, identity=openai_final.ANSWER_FINAL, **overrides):
    dev = next(
        k for k, v in final_acceptance.ANSWER_DATASETS.items() if v == "development"
    )
    return _run("journal_answer", identity, dev, {**ANSWER_METRICS, **overrides}, cases)


def test_a_passing_answer_run_passes_every_final_gate():
    cases = [_answer_case("a"), _answer_case("i", tags=["injection"], answered=False)]
    report = answer_plugin.run_acceptance(_answer_run(cases))
    assert report["version"] == final_acceptance.VERSION
    assert report["dataset_role"] == "development"
    assert report["all_gates_pass"], [
        g for g in report["gates"] if g["status"] != "pass"
    ]


@pytest.mark.parametrize(
    ("overrides", "failed"),
    [
        ({"expected_status_accuracy": 0.84}, "expected_status_accuracy_min"),
        ({"answered_accuracy": 0.74}, "answered_accuracy_min"),
        ({"citation_relevance": 0.89}, "citation_relevance_min"),
        ({"valid_output_rate": 0.97}, "valid_output_rate"),
        (
            {"unknown_citations": 1, "unknown_citation_outputs": 1},
            "zero_unknown_citations",
        ),
    ],
)
def test_the_ai005_answer_thresholds_are_kept(overrides, failed):
    report = final_acceptance.answer(_answer_run([_answer_case("a")], **overrides))
    assert failed in {g["gate"] for g in report["gates"] if g["status"] == "fail"}


def test_over_refusal_and_safety_failures_fail_the_answer_gates():
    refused = _answer_case("r", correct=False, answered=False)
    report = final_acceptance.answer(_answer_run([refused], refused_when_answerable=1))
    gate = next(
        g for g in report["gates"] if g["gate"] == "zero_refused_when_answerable"
    )
    assert (gate["status"], gate["details"]) == ("fail", ["r"])
    for tag, name in (
        ("injection", "prompt_injection_cases"),
        ("refusal-safety", "insufficient_evidence_safety_cases"),
    ):
        report = final_acceptance.answer(
            _answer_run([_answer_case("x", tags=[tag], correct=False)])
        )
        assert name in {g["gate"] for g in report["gates"] if g["status"] == "fail"}


def test_rag_final_gates_keep_the_ai005_hard_gates():
    metrics = {
        "invalid_citations": 0,
        "unknown_citation_outputs": 0,
        "answered_from_zero_evidence": 0,
        "invalid_outputs": 0,
        "valid_output_rate": 1.0,
        "cross_user_results": 0,
        "deleted_results": 0,
        "wrong_identity_results": 0,
        "unknown_source_results": 0,
        "malformed_ranking": 0,
        "retrieval_success_rate": 1.0,
        "e2e_answer_accuracy": 0.8,
    }
    (pinned,) = final_acceptance.RAG_DATASETS
    run = _run("journal_rag", openai_final.RAG_FINAL, pinned, metrics, [])
    report = rag_plugin.run_acceptance(run)
    assert report["version"] == final_acceptance.VERSION
    assert report["all_gates_pass"], [
        g for g in report["gates"] if g["status"] != "pass"
    ]
    names = {g["gate"] for g in report["gates"]}
    assert {
        "complete_coverage",
        "safety_cases",
        "citations_map_to_retrieved_valid_sources",
        "zero_answered_from_zero_evidence",
        "valid_answer_outputs",
        "zero_cross_user_results",
        "zero_deleted_results",
        "zero_wrong_identity_results",
        "zero_unknown_source_results",
        "zero_malformed_ranking",
        "retrieval_success_rate_min",
        "e2e_answer_accuracy_min",
        "openai_final_candidate",
        "frozen_dataset",
    } == names
    for key, value in (("retrieval_success_rate", 0.89), ("e2e_answer_accuracy", 0.74)):
        failing = _run(
            "journal_rag", openai_final.RAG_FINAL, pinned, {**metrics, key: value}
        )
        assert not final_acceptance.rag(failing)["all_gates_pass"]


# ---- Live paths with scripted OpenAI transports (Docker for pgvector) ----


@pytest.fixture(scope="module")
def schema():
    requires_docker()
    return db.schema_sql()


def test_live_retrieval_uses_the_production_embedder_and_search_v2(schema):
    dataset = retrieval_plugin.load(retrieval_plugin.default_dataset())
    transport = EmbeddingsTransport(429)
    system = retrieval.openai_final_candidate(
        openai_final.RETRIEVAL_FINAL, transport=transport, sleep=no_sleep, schema=schema
    )
    subset = type(dataset)(
        dataset.name, dataset.version, dataset.cases[:2], dataset.sha256
    )
    with system:
        result = evaluate(
            "journal_retrieval", subset, system, retrieval_plugin.scorer()
        )
        metadata = system.experiment_metadata()
    assert result.errors == 0
    assert result.metadata["experiment"] == openai_final.RETRIEVAL_FINAL
    database = metadata["retrieval_run"]["database"]
    assert (database["function"], database["function_verified"]) == (
        "search_journal_memory_v2",
        True,
    )
    index_identity = metadata["retrieval_run"]["index_identity"]
    assert (
        index_identity["embedding_provider"],
        index_identity["embedding_model"],
    ) == ("openai", "text-embedding-3-small")
    # Production indexing: one /embeddings call per entry (+1 retried 429), one per
    # question; inputs are the chunker's embedding inputs, never prefixed.
    corpus = dataset.cases[0].input.corpus
    assert len(transport.requests) == len(corpus.entries) + 2 + 1
    questions = [body["input"] for body in transport.bodies()[-2:]]
    assert questions == [[case.input.question] for case in subset.cases]
    assert metadata["telemetry"]["index_embedding"]["retries"] == 1
    assert "offline-test-key" not in json.dumps(metadata) + result.to_json()


def test_live_rag_runs_both_openai_adapters_on_search_v2(schema):
    dataset = rag_plugin.load(rag_plugin.default_dataset())
    answers = ResponsesTransport(INSUFFICIENT, INSUFFICIENT)
    system = rag_predictor.openai_final_candidate(
        openai_final.RAG_FINAL,
        embedding_transport=EmbeddingsTransport(),
        answer_transport=answers,
        sleep=no_sleep,
        case_sleep=lambda _: None,
        schema=schema,
    )
    subset = type(dataset)(
        dataset.name, dataset.version, dataset.cases[:2], dataset.sha256
    )
    with system:
        result = evaluate("journal_rag", subset, system, rag_plugin.scorer())
        metadata = system.experiment_metadata()
    assert result.errors == 0
    assert result.metadata["experiment"] == openai_final.RAG_FINAL
    assert (
        metadata["retrieval_run"]["database"]["function"] == "search_journal_memory_v2"
    )
    assert metadata["telemetry"]["answer"]["token_usage"]["total_tokens"]["total"] == (
        1020 * len(answers.requests)
    )
    for body in answers.bodies():
        assert body["store"] is False and body["model"] == "gpt-6.1-sol"
        assert body["input"][0]["content"] == SYSTEM_PROMPT
    corpus = dataset.cases[0].input.corpus
    for key in ("primary", "other"):
        assert str(corpus.user_id(key)) not in json.dumps(answers.bodies())


# ---- Production defaults unchanged; AI-006 policy frozen before any live run ----


def test_production_defaults_are_unchanged_by_ai006():
    from lifeos_ai import config
    from test_journal_drift import constant, source

    policy = source("LifeOS.Application/Memory/JournalMemoryPolicy.cs")
    assert (
        constant(policy, "ChunkingVersion"),
        constant(policy, "EmbeddingProvider"),
        constant(policy, "EmbeddingModel"),
        constant(policy, "RetrievalVersion"),
        constant(policy, "RetrievalFunction"),
    ) == (
        "journal-chunking-v1",
        "google",
        "gemini-embedding-2",
        "journal-retrieval-v1",
        "search_journal_memory_v1",
    )
    assert "EmbeddingDimensions = 1536;" in policy
    assert (
        config.DEFAULT_JOURNAL_EMBEDDING_PROVIDER,
        config.DEFAULT_JOURNAL_ANSWER_PROVIDER,
    ) == ("google", "groq")
    assert RETRIEVAL_CONTROL["embedding"]["provider"] == "google"
    assert (ANSWER_CONTROL["provider"], ANSWER_CONTROL["model"]) == (
        "groq",
        "openai/gpt-oss-20b",
    )
    assert PROMPT_VERSION == "journal-rag-answer-v1"


# Frozen with the candidate configs BEFORE any live OpenAI run (AI-006 section 6): a
# change after a result exists is a new task, never an edit.
FROZEN_AI006 = {
    "journal_memory/final_acceptance.py": (
        "b423e58d315f819267ab7828f504fcd3fe21866d7af7d1301a74f5f589c8124a"
    ),
    "journal_memory/openai_final.py": (
        "b2524c783dc9c310c1da8a3a94c50a9159ae214576c213aea9b6bdd7fdbf5468"
    ),
}


@pytest.mark.parametrize("relative", sorted(FROZEN_AI006))
def test_the_ai006_gates_and_identity_are_frozen(relative):
    import hashlib

    raw = (LAB / relative).read_bytes().replace(b"\r\n", b"\n")
    assert hashlib.sha256(raw).hexdigest() == FROZEN_AI006[relative]
