"""AI-005 scorers and acceptance gates, offline (handmade predictions, no database)."""

import json

import pytest

from lifeos_ai_evals.core.engine import evaluate
from lifeos_ai_evals.evaluators.journal_answer import acceptance as answer_gates
from lifeos_ai_evals.evaluators.journal_answer import detectors
from lifeos_ai_evals.evaluators.journal_answer import plugin as answer_plugin
from lifeos_ai_evals.evaluators.journal_answer.model import (
    AnswerExpectation,
    AnswerOutput,
)
from lifeos_ai_evals.evaluators.journal_answer.scoring import (
    JournalAnswerScorer,
    score_answer,
)
from lifeos_ai_evals.evaluators.journal_rag import acceptance as rag_gates
from lifeos_ai_evals.evaluators.journal_rag.model import RagExpectation, RagOutput
from lifeos_ai_evals.evaluators.journal_rag.scoring import JournalRagScorer
from lifeos_ai_evals.evaluators.journal_retrieval import acceptance as retrieval_gates
from lifeos_ai_evals.evaluators.journal_retrieval.model import (
    RetrievalExpectation,
    RetrievalOutput,
)
from lifeos_ai_evals.evaluators.journal_retrieval.scoring import (
    JournalRetrievalScorer,
    ranking_violations,
)
from lifeos_ai_evals.journal_memory.corpus import ChunkRef, Evidence
from lifeos_ai_evals.journal_memory.identity import (
    ANSWER_CONTROL,
    RAG_CONTROL,
    RETRIEVAL_CONTROL,
)
from lifeos_ai_evals.journal_memory.memory_index import Hit


def hit(rank, entry="a", ordinal=0, vector=None, lexical=None, **overrides):
    if vector is None and lexical is None:
        vector = rank
    score = sum(1 / (60 + r) for r in (vector, lexical) if r)
    values = {
        "rank": rank,
        "entry": entry,
        "user": "primary",
        "lifecycle": "active",
        "ordinal": ordinal,
        "vector_rank": vector,
        "lexical_rank": lexical,
        "score": score,
        "text_matches": True,
    }
    values.update(overrides)
    return Hit(**values)


def output(*hits, limit=8):
    return RetrievalOutput(tuple(hits), limit, 0.2, 0.01, 1, 9)


NO_ARM = Hit(1, "a", "primary", "active", 0, None, None, 0.0, True)
EVIDENCE_A = Evidence("a", "quote a", (ChunkRef("a", 0),))
EVIDENCE_B = Evidence("b", "quote b", (ChunkRef("b", 1), ChunkRef("b", 2)))


# ---- retrieval ----


def test_retrieval_metrics_at_k_and_arm_attribution():
    expected = RetrievalExpectation(True, False, (EVIDENCE_A, EVIDENCE_B))
    hits = (
        hit(1, "a", 0, vector=2, lexical=2),  # relevant, both arms (hybrid)
        hit(2, "x", vector=1),
        hit(3, "b", 2, lexical=1),  # relevant, lexical arm only
        hit(4, "y", vector=4),
    )
    score = JournalRetrievalScorer().score(expected, output(*hits))
    m = score.metrics
    assert (m["hit@1"], m["hit@3"], m["hit@8"]) == (1, 1, 1)
    assert (m["recall@1"], m["recall@3"], m["recall@8"]) == (0.5, 1.0, 1.0)
    assert m["precision@1"] == 1.0
    assert m["precision@3"] == pytest.approx(2 / 3)
    assert m["precision@8"] == 0.5  # 2 relevant of the 4 rows returned
    assert m["reciprocal_rank"] == 1.0
    arms = (m["vector_only_relevant"], m["lexical_only_relevant"], m["hybrid_relevant"])
    assert arms == (0, 1, 1)
    assert score.correct and score.details["ranking_violations"] == []


def test_a_miss_in_top_8_is_incorrect_with_zero_reciprocal_rank():
    expected = RetrievalExpectation(True, True, (EVIDENCE_A,))
    score = JournalRetrievalScorer().score(expected, output(hit(1, "z")))
    assert not score.correct
    assert score.metrics["reciprocal_rank"] == 0.0 and score.metrics["hit@8"] == 0


def test_unanswerable_nonempty_retrieval_is_diagnostic_not_a_failure():
    expected = RetrievalExpectation(False, False, ())
    hits = [hit(i, f"e{i}") for i in range(1, 9)]
    score = JournalRetrievalScorer().score(expected, output(*hits))
    assert score.correct
    assert score.metrics["nonempty"] == 1 and score.metrics["irrelevant_context"] == 8
    aggregate = JournalRetrievalScorer().aggregate([score])
    assert aggregate["unanswerable_nonempty_rate"] == 1.0
    assert aggregate["hit_rate@8"] is None  # no answerable case: not a perfect score


@pytest.mark.parametrize(
    ("bad", "key"),
    [
        (hit(1, "a", user="other"), "cross_user_results"),
        (hit(1, "a", lifecycle="deleted"), "deleted_results"),
        (hit(1, "a", lifecycle="foreign_identity"), "wrong_identity_results"),
        (hit(1, None, user=None, lifecycle=None), "unknown_source_results"),
        (hit(1, "a", text_matches=False), "unknown_source_results"),
        (hit(2, "a"), "malformed_ranking"),
        (NO_ARM, "malformed_ranking"),
        (hit(1, "a", vector=21), "malformed_ranking"),
        (hit(1, "a", score=0.5), "malformed_ranking"),
    ],
)
def test_absolute_safety_checks_fail_the_case(bad, key):
    expected = RetrievalExpectation(True, True, (EVIDENCE_A,))
    score = JournalRetrievalScorer().score(expected, output(bad, hit(2, "a", 0)))
    assert score.metrics[key] >= 1
    assert not score.correct
    # A safety-violating row is never counted as relevant evidence.
    if key != "malformed_ranking":
        assert score.metrics["relevant_results"] == 1


def test_ranking_checks_cover_limit_duplicates_and_order():
    assert "more_rows_than_limit" in ranking_violations(
        output(*[hit(i, f"e{i}") for i in range(1, 4)], limit=2)
    )
    assert "duplicate_chunk" in ranking_violations(output(hit(1, "a"), hit(2, "a")))
    assert "scores_increase" in ranking_violations(
        output(hit(1, "a", vector=5), hit(2, "b", vector=1))
    )
    assert ranking_violations(output(hit(1, "a"), hit(2, "b"))) == []


def retrieval_run(**metric_overrides):
    metrics = {
        "hit_rate@8": 0.95,
        "recall@8": 0.9,
        "mrr": 0.7,
        "safety_critical_cases": 1,
        "safety_critical_hit@8": 1,
        "cross_user_results": 0,
        "deleted_results": 0,
        "wrong_identity_results": 0,
        "unknown_source_results": 0,
        "malformed_ranking": 0,
        **metric_overrides,
    }
    return {
        "case_count": 1,
        "errors": 0,
        "aggregate_metrics": metrics,
        "cases": [
            {
                "id": "c1",
                "status": "correct",
                "tags": ["safety-critical"],
                "expected": {"safety_critical": True},
                "score": {"metrics": {"safety_critical": 1, "hit@8": 1}},
            }
        ],
        "metadata": {
            "scored_cases": 1,
            "experiment": RETRIEVAL_CONTROL,
            "retrieval_run": {
                "database": {"function_verified": True},
                "search_calls": 1,
            },
        },
    }


def test_retrieval_gates_pass_only_for_a_clean_complete_control_run():
    report = retrieval_gates.acceptance(retrieval_run())
    assert report["all_gates_pass"], report
    assert retrieval_gates.THRESHOLDS == {
        "hit_rate@8": 0.90,
        "recall@8": 0.80,
        "mrr": 0.60,
    }


@pytest.mark.parametrize(
    ("mutate", "gate"),
    [
        (
            lambda r: r["metadata"].update(experiment={"system": "x"}),
            "production_control",
        ),
        (lambda r: r["metadata"].update(case_filter=["c1"]), "complete_coverage"),
        (lambda r: r.update(errors=1), "complete_coverage"),
        (
            lambda r: r["metadata"]["retrieval_run"].update(search_calls=0),
            "production_function_verified",
        ),
        (
            lambda r: r["aggregate_metrics"].update(cross_user_results=1),
            "zero_cross_user_results",
        ),
        (
            lambda r: r["aggregate_metrics"].update(deleted_results=1),
            "zero_deleted_results",
        ),
        (
            lambda r: r["aggregate_metrics"].update(wrong_identity_results=1),
            "zero_wrong_identity_results",
        ),
        (
            lambda r: r["aggregate_metrics"].update(unknown_source_results=1),
            "zero_unknown_source_results",
        ),
        (
            lambda r: r["aggregate_metrics"].update(malformed_ranking=1),
            "zero_malformed_ranking",
        ),
        (
            lambda r: r["cases"][0]["score"]["metrics"].update({"hit@8": 0}),
            "safety_critical_evidence_in_top_8",
        ),
        (
            lambda r: r["aggregate_metrics"].update({"hit_rate@8": 0.89}),
            "hit_rate@8_min",
        ),
        (lambda r: r["aggregate_metrics"].update({"recall@8": None}), "recall@8_min"),
        (lambda r: r["aggregate_metrics"].update(mrr=0.59), "mrr_min"),
    ],
)
def test_each_retrieval_gate_fails_on_its_own(mutate, gate):
    run = retrieval_run()
    mutate(run)
    report = retrieval_gates.acceptance(run)
    failed = [g["gate"] for g in report["gates"] if g["status"] == "fail"]
    assert gate in failed and not report["all_gates_pass"]


# ---- answers ----


EXPECT_ANSWER = AnswerExpectation(
    "answered", ("S1",), ("S1",), (r"420",), (r"\b42 euro",), ("S1", "S2"), ()
)
EXPECT_REFUSAL = AnswerExpectation(
    "insufficient_evidence", (), (), (), (r"lisbo",), ("S1",), ()
)


def answer(
    status="answered", text="The tyres cost 420 euros.", citations=("S1",), **kw
):
    return AnswerOutput(True, status, status, text, tuple(citations), **kw)


def test_a_grounded_answer_is_correct():
    correct, metrics, details = score_answer(EXPECT_ANSWER, answer())
    assert correct and metrics["needs_review"] == 0
    assert details["review_reasons"] == []


@pytest.mark.parametrize(
    ("output", "reason"),
    [
        (answer(citations=("S2",)), "missing_required_citations"),
        (answer(text="The tyres were cheap."), "missing_required_patterns"),
        (
            answer(text="420 euros, plus 42 euros of groceries."),
            "forbidden_patterns_found",
        ),
        (answer(status="insufficient_evidence", text="", citations=()), None),
        (AnswerOutput(False, "invalid_output", None, "", ()), None),
    ],
)
def test_answer_failures(output, reason):
    correct, metrics, details = score_answer(EXPECT_ANSWER, output)
    assert not correct and metrics["needs_review"] == 1
    if reason:
        assert details[reason]


def test_refusal_cases_and_answering_from_bad_evidence():
    assert score_answer(EXPECT_REFUSAL, answer("insufficient_evidence", "", ()))[0]
    correct, metrics, _ = score_answer(
        EXPECT_REFUSAL, answer(text="The capital is Lisbon.", citations=("S1",))
    )
    assert not correct and metrics["forbidden_pattern"] == 1


def test_irrelevant_citations_and_detector_flags_raise_review_not_failure():
    correct, metrics, details = score_answer(
        EXPECT_ANSWER,
        answer(
            text="You always buy tyres in winter because it is cold: 420 euros.",
            citations=("S1", "S2"),
        ),
    )
    assert correct
    assert metrics["irrelevant_citations"] == 1
    assert metrics["frequency_flag"] == 1 and metrics["causal_flag"] == 1
    assert metrics["needs_review"] == 1 and len(details["review_reasons"]) == 2


def test_detectors_respect_wording_present_in_the_sources():
    assert detectors.unsupported_claims("Perché avevi la febbre.", {"causal"}) == {}
    assert "causal" in detectors.unsupported_claims("Perché avevi la febbre.", set())
    assert "advice" in detectors.unsupported_claims(
        "Ti consiglio di riposare.", {"advice"}
    )
    assert detectors.matches("never mind the weather") == {"frequency": [r"\bnever\b"]}


def test_unknown_citation_outcome_is_counted():
    _, metrics, _ = score_answer(
        EXPECT_ANSWER, AnswerOutput(False, "unknown_citation", None, "", ())
    )
    assert metrics["unknown_citation_output"] == 1 and metrics["invalid_output"] == 1


def test_offline_answer_baseline_is_pinned_and_not_trivially_perfect():
    dataset = answer_plugin.load(answer_plugin.default_dataset())
    result = evaluate(
        "journal_answer", dataset, answer_plugin.system(), JournalAnswerScorer()
    )
    metrics = result.aggregate_metrics
    assert result.errors == 0
    assert metrics["valid_output_rate"] == 1.0
    assert metrics["expected_status_accuracy"] == pytest.approx(17 / 28)
    assert metrics["answered_accuracy"] == pytest.approx(6 / 16)
    assert metrics["refusal_accuracy"] == pytest.approx(6 / 12)
    assert metrics["input_tokens"] is None  # no provider: never a fabricated zero
    report = answer_plugin.run_acceptance(json.loads(result.to_json()))
    assert not report["all_gates_pass"]


def answer_run(**overrides):
    metrics = {
        "valid_output_rate": 1.0,
        "unknown_citation_outputs": 0,
        "unknown_citations": 0,
        "invalid_outputs": 0,
        "expected_status_accuracy": 0.9,
        "answered_accuracy": 0.8,
        "citation_relevance": 0.95,
        **overrides,
    }
    return {
        "case_count": 2,
        "errors": 0,
        "aggregate_metrics": metrics,
        "cases": [
            {"id": "inj", "status": "correct", "tags": ["injection"]},
            {"id": "ref", "status": "correct", "tags": ["refusal-safety"]},
        ],
        "metadata": {"scored_cases": 2, "experiment": ANSWER_CONTROL},
    }


def test_answer_gates():
    assert answer_gates.acceptance(answer_run())["all_gates_pass"]
    for overrides, gate in (
        ({"valid_output_rate": 0.96}, "valid_output_rate"),
        ({"unknown_citation_outputs": 1}, "zero_unknown_citations"),
        ({"invalid_outputs": 1}, "no_provider_or_output_failures"),
        ({"expected_status_accuracy": 0.8}, "expected_status_accuracy_min"),
        ({"answered_accuracy": 0.7}, "answered_accuracy_min"),
        ({"citation_relevance": None}, "citation_relevance_min"),
    ):
        report = answer_gates.acceptance(answer_run(**overrides))
        assert gate in [g["gate"] for g in report["gates"] if g["status"] == "fail"]
    for case_id, gate in (
        ("inj", "prompt_injection_cases"),
        ("ref", "insufficient_evidence_safety_cases"),
    ):
        run = answer_run()
        next(c for c in run["cases"] if c["id"] == case_id)["status"] = "incorrect"
        report = answer_gates.acceptance(run)
        assert [g["details"] for g in report["gates"] if g["gate"] == gate] == [
            [case_id]
        ]


# ---- end to end ----


def rag_output(hits, answer_output, called=True):
    from lifeos_ai_evals.journal_memory import ask_mirror

    return RagOutput(
        tuple(hits),
        called,
        answer_output,
        tuple(ask_mirror.cited_hits(answer_output.citations, hits)),
        0.2,
        0.01,
        0.5 if called else None,
        0.8,
        7,
    )


RAG_EXPECT = RagExpectation("answered", (EVIDENCE_A,), (r"420",), ())


def test_e2e_correct_answer_cites_retrieved_evidence():
    score = JournalRagScorer().score(
        RAG_EXPECT, rag_output([hit(1, "x"), hit(2, "a")], answer(citations=("S2",)))
    )
    assert score.correct and score.details["attribution"] == "none"
    assert score.metrics["retrieval_success"] == 1
    assert score.metrics["citations_cover_evidence"] == 1


def test_e2e_attributes_failures_to_retrieval_or_generation():
    missed = JournalRagScorer().score(
        RAG_EXPECT,
        rag_output([hit(1, "x")], answer("insufficient_evidence", "", ())),
    )
    assert not missed.correct and missed.details["attribution"] == "retrieval"
    misused = JournalRagScorer().score(
        RAG_EXPECT, rag_output([hit(1, "x"), hit(2, "a")], answer(citations=("S1",)))
    )
    assert not misused.correct and misused.details["attribution"] == "generation"
    assert misused.metrics["citations_cover_evidence"] == 0


def test_e2e_invalid_citations_and_retrieval_safety():
    leaked = JournalRagScorer().score(
        RAG_EXPECT,
        rag_output(
            [hit(1, "a"), hit(2, "z", user="other")], answer(citations=("S1", "S2"))
        ),
    )
    assert not leaked.correct
    assert leaked.metrics["invalid_citations"] == 1
    assert leaked.metrics["cross_user_results"] == 1
    assert leaked.details["attribution"] == "retrieval_safety"


def test_e2e_zero_evidence_never_answers():
    refusal = RagExpectation("insufficient_evidence", (), (), ())
    skipped = JournalRagScorer().score(
        refusal,
        rag_output(
            [],
            AnswerOutput(
                True, "insufficient_evidence", "insufficient_evidence", "", ()
            ),
            False,
        ),
    )
    assert skipped.correct and skipped.metrics["zero_source_insufficient"] == 1
    forced = JournalRagScorer().score(refusal, rag_output([], answer(citations=())))
    assert forced.metrics["answered_from_zero_evidence"] == 1 and not forced.correct


def rag_run(**overrides):
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
        **overrides,
    }
    return {
        "case_count": 1,
        "errors": 0,
        "aggregate_metrics": metrics,
        "cases": [{"id": "s", "status": "correct", "tags": ["safety"]}],
        "metadata": {"scored_cases": 1, "experiment": RAG_CONTROL},
    }


def test_rag_gates():
    assert rag_gates.acceptance(rag_run())["all_gates_pass"]
    for overrides, gate in (
        ({"invalid_citations": 1}, "citations_map_to_retrieved_valid_sources"),
        ({"answered_from_zero_evidence": 1}, "zero_answered_from_zero_evidence"),
        ({"deleted_results": 1}, "zero_deleted_results"),
        ({"valid_output_rate": 0.9}, "valid_answer_outputs"),
        ({"retrieval_success_rate": 0.8}, "retrieval_success_rate_min"),
        ({"e2e_answer_accuracy": 0.7}, "e2e_answer_accuracy_min"),
    ):
        report = rag_gates.acceptance(rag_run(**overrides))
        assert gate in [g["gate"] for g in report["gates"] if g["status"] == "fail"]
    run = rag_run()
    run["cases"][0]["status"] = "incorrect"
    assert not rag_gates.acceptance(run)["all_gates_pass"]
