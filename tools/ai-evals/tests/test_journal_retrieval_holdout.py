"""AI-005.1 frozen retrieval holdout and promotion gates. Pinned BEFORE any
journal-retrieval-v2 design or live run (docs/tasks/ai/AI-005.1.md); offline only."""

import hashlib
import json
from collections import Counter
from pathlib import Path

import pytest

from lifeos_ai_evals.core.engine import Score
from lifeos_ai_evals.evaluators.journal_retrieval import plugin
from lifeos_ai_evals.evaluators.journal_retrieval import promotion as p
from lifeos_ai_evals.evaluators.journal_retrieval.scoring import JournalRetrievalScorer
from lifeos_ai_evals.journal_memory.corpus import SEARCH_USER
from lifeos_ai_evals.journal_memory.identity import RETRIEVAL_CONTROL

ROOT = Path(__file__).resolve().parents[1]
DEVELOPMENT = ROOT / "datasets/journal_retrieval/v1.json"
HOLDOUT = ROOT / "datasets/journal_retrieval/holdout-v1.json"
DEVELOPMENT_SHA256 = "fda741ed8cfcf8cc254cbda0a0c67b335f249f14fe42e1ae436a3b1fe8cfdca5"
HOLDOUT_SHA256 = "d5ec8a99c1730bd3c1b9b34b4185b2c8b1042c519ab85dfe5777372c4d4d328c"
HOLDOUT_CHUNKS = (
    58,
    "490c2781ff22f07ebe7a83a6820893513d6f758785961d160f1e5c1f7f67db07",
)
# Frozen with the holdout; a change is a new promotion version, never an edit.
PROMOTION_SHA256 = "15f33ce9e512a49b637f4293f01e552ab941a1167e207cdc87cece592783fae6"


def lf_sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes().replace(b"\r\n", b"\n")).hexdigest()


def holdout():
    return plugin.load(HOLDOUT)


# ---- dataset freeze ----


def test_development_and_holdout_are_frozen_by_canonical_hash():
    assert lf_sha256(DEVELOPMENT) == DEVELOPMENT_SHA256
    assert lf_sha256(HOLDOUT) == HOLDOUT_SHA256
    data = holdout()
    assert (data.name, data.version, len(data.cases), data.sha256) == (
        "synthetic-journal-retrieval-holdout",
        "1.0.0",
        28,
        HOLDOUT_SHA256,
    )
    # The development set stays the default; the holdout is selected explicitly.
    assert plugin.default_dataset() == DEVELOPMENT
    assert p.DATASETS == {
        "development": ("synthetic-journal-retrieval", "1.0.0", DEVELOPMENT_SHA256),
        "holdout": ("synthetic-journal-retrieval-holdout", "1.0.0", HOLDOUT_SHA256),
    }


def test_holdout_corpus_chunking_is_pinned():
    corpus = holdout().cases[0].input.corpus
    assert (len(corpus.chunks), corpus.chunking_digest()) == HOLDOUT_CHUNKS
    lifecycles = Counter(entry.lifecycle for entry in corpus.entries)
    assert lifecycles == {"active": 54, "deleted": 2, "foreign_identity": 1}
    assert sum(entry.user != SEARCH_USER for entry in corpus.entries) == 5


def test_holdout_label_mix():
    cases = holdout().cases
    answerable = [case for case in cases if case.expected.answerable]
    assert len(answerable) == 20 and len(cases) - len(answerable) == 8
    critical = [case for case in cases if case.expected.safety_critical]
    assert len(critical) == 7
    assert sum(len(case.expected.evidence) > 1 for case in answerable) == 2
    for case in cases:
        assert ("unanswerable" in case.tags) == (not case.expected.answerable)
        assert ("safety-critical" in case.tags) == case.expected.safety_critical
        assert case.description.startswith("Synthetic: ")


def test_holdout_covers_the_required_product_semantics():
    tags = Counter(tag for case in holdout().cases for tag in case.tags)
    for required in (
        "lexical-exact",
        "paraphrase",
        "semantic",
        "it-it",
        "en-it",
        "it-en",
        "mixed-language",
        "temporal-distractor",
        "wrong-context",
        "needle",
        "multi-evidence",
        "rare-identifier",
        "health",
        "finance",
        "plan",
        "opinion",
        "long-entry",
        "chunk-boundary",
        "injection",
        "unanswerable",
        "cross-user-trap",
        "deleted-trap",
        "wrong-identity-trap",
        "safety-critical",
    ):
        assert tags[required] >= 1, required
    assert tags["safety-critical"] >= 6 and tags["unanswerable"] >= 6


def test_holdout_chunk_boundary_evidence_is_near_a_production_chunk_edge():
    data = holdout()
    corpus = data.cases[0].input.corpus
    boundary = [case for case in data.cases if "chunk-boundary" in case.tags]
    assert boundary
    for case in boundary:
        for item in case.expected.evidence:
            chunks = corpus.chunks_of(item.entry)
            start = corpus.entry(item.entry).content.index(item.quote)
            edges = [c.end for c in chunks[:-1]] + [c.start for c in chunks[1:]]
            assert len(chunks) > 1
            assert min(abs(edge - start) for edge in edges) <= 300


def test_holdout_is_independent_of_the_development_set():
    development = plugin.load(DEVELOPMENT)
    dev_corpus = development.cases[0].input.corpus
    hold = holdout()
    hold_corpus = hold.cases[0].input.corpus
    assert not {c.id for c in development.cases} & {c.id for c in hold.cases}
    assert not {c.input.question for c in development.cases} & {
        c.input.question for c in hold.cases
    }
    assert not {e.key for e in dev_corpus.entries} & {
        e.key for e in hold_corpus.entries
    }
    dev_text = "\n".join(e.content for e in dev_corpus.entries)
    hold_text = "\n".join(e.content for e in hold_corpus.entries)
    for case in hold.cases:
        for item in case.expected.evidence:
            assert item.quote not in dev_text
    for case in development.cases:
        for item in case.expected.evidence:
            assert item.quote not in hold_text


# ---- promotion gates (frozen before any candidate exists) ----


def test_promotion_gates_are_frozen():
    raw = (
        ROOT / "src/lifeos_ai_evals/evaluators/journal_retrieval/promotion.py"
    ).read_bytes()
    digest = hashlib.sha256(raw.replace(b"\r\n", b"\n")).hexdigest()
    assert digest == PROMOTION_SHA256
    assert p.VERSION == "journal-retrieval-promotion-v1"
    assert p.DEVELOPMENT_MINIMUMS == {"hit_rate@8": 0.90, "recall@8": 0.80, "mrr": 0.60}
    assert p.MRR_MATERIAL_REGRESSION == 0.02
    # The development minimums are AI-005's own, not weakened.
    from lifeos_ai_evals.evaluators.journal_retrieval import acceptance

    assert p.DEVELOPMENT_MINIMUMS == acceptance.THRESHOLDS
    assert (p.CANDIDATE_RETRIEVAL_VERSION, p.CANDIDATE_FUNCTION) == (
        "journal-retrieval-v2",
        "search_journal_memory_v2",
    )


def candidate_identity(**retrieval):
    return {
        **RETRIEVAL_CONTROL,
        "system": "journal-memory-retrieval-v2-candidate",
        "retrieval": {
            **RETRIEVAL_CONTROL["retrieval"],
            "version": "journal-retrieval-v2",
            "function": "search_journal_memory_v2",
            "candidate_parameter": 1,
            **retrieval,
        },
    }


def metrics(*, answerable=True, hit=1, recall=None, rr=None, rows=8, critical=False):
    """Per-case metrics in the frozen scorer's shape."""
    recall = float(hit) if recall is None else recall
    rr = float(hit) if rr is None else rr
    base = {
        "answerable": int(answerable),
        "safety_critical": int(critical),
        "result_count": rows,
        "relevant_results": hit if answerable else 0,
        "irrelevant_results": rows - (hit if answerable else 0),
        "embedding_seconds": 0.1,
        "retrieval_seconds": 0.01,
        "embedding_input_tokens": None,
        **{key: 0 for key in p.SAFETY_KEYS},
    }
    if not answerable:
        return {**base, "nonempty": int(rows > 0), "irrelevant_context": rows}
    return {
        **base,
        **{f"hit@{k}": hit for k in (1, 3, 8)},
        **{f"recall@{k}": recall for k in (1, 3, 8)},
        **{f"precision@{k}": (hit / rows if rows else None) for k in (1, 3, 8)},
        "reciprocal_rank": rr,
        "vector_only_relevant": 0,
        "lexical_only_relevant": 0,
        "hybrid_relevant": hit,
    }


def run(role, identity, cases, *, errors=0, case_filter=None):
    name, version, sha = p.DATASETS[role]
    scorer = JournalRetrievalScorer()
    scores = [Score(bool(m.get("hit@8", 1)), m) for m in cases.values()]
    metadata = {
        "scored_cases": len(cases) - errors,
        "experiment": identity,
        "retrieval_run": {
            "database": {
                "function": identity["retrieval"]["function"],
                "function_verified": True,
            },
            "search_calls": len(cases) - errors,
        },
    }
    if case_filter:
        metadata["case_filter"] = case_filter
    return {
        "dataset": {"name": name, "version": version, "sha256": sha},
        "case_count": len(cases),
        "errors": errors,
        "metadata": metadata,
        "aggregate_metrics": scorer.aggregate(scores),
        "cases": [
            {"id": case_id, "status": "correct", "score": {"metrics": m}}
            for case_id, m in cases.items()
        ],
    }


def development_cases(**overrides):
    cases = {f"a{i}": metrics() for i in range(30)}
    cases["critical-ok"] = metrics(critical=True)
    cases["critical-miss"] = metrics(hit=0, critical=True)
    cases["u1"] = metrics(answerable=False)
    cases.update(overrides)
    return cases


def statuses(report):
    return {gate["gate"]: gate["status"] for gate in report["gates"]}


def test_development_candidate_that_fixes_the_safety_miss_is_promoted():
    control = run("development", RETRIEVAL_CONTROL, development_cases())
    candidate = run(
        "development",
        candidate_identity(),
        development_cases(**{"critical-miss": metrics(critical=True, rr=0.5)}),
    )
    report = plugin.acceptance(control, candidate)
    assert report["version"] == "journal-retrieval-promotion-v1"
    assert report["all_gates_pass"], [
        g for g in report["gates"] if g["status"] == "fail"
    ]
    assert report["diagnostics"]["dataset_role"] == "development"
    assert "score" not in report  # no overall retrieval score


def test_development_candidate_that_keeps_the_safety_miss_fails():
    control = run("development", RETRIEVAL_CONTROL, development_cases())
    candidate = run("development", candidate_identity(), development_cases())
    result = statuses(p.promotion(control, candidate))
    assert result["candidate_safety_critical_evidence_in_top_8"] == "fail"
    assert result["candidate_fixes_control_safety_critical_misses"] == "fail"


def test_development_tolerates_one_case_but_not_two():
    fixed = {"critical-miss": metrics(critical=True)}
    control = run("development", RETRIEVAL_CONTROL, development_cases())
    one = run(
        "development",
        candidate_identity(),
        development_cases(**fixed, a0=metrics(hit=0), a1=metrics(hit=0)),
    )
    result = statuses(p.promotion(control, one))
    # Fixing one case and losing two is a net loss of one: tolerated.
    assert result["no_material_regression_hit_rate@8"] == "pass"
    lost = {f"a{i}": metrics(hit=0) for i in range(3)}
    three = run("development", candidate_identity(), development_cases(**fixed, **lost))
    result = statuses(p.promotion(control, three))
    assert result["no_material_regression_hit_rate@8"] == "fail"
    assert result["no_material_regression_recall@8"] == "fail"


def holdout_cases(**overrides):
    cases = {f"h{i}": metrics() for i in range(18)}
    cases["c1"] = metrics(critical=True)
    cases["c2"] = metrics(critical=True, recall=0.5)
    cases["u1"] = metrics(answerable=False)
    cases["u2"] = metrics(answerable=False)
    cases.update(overrides)
    return cases


def test_holdout_candidate_with_less_noise_and_equal_recall_is_promoted():
    control = run("holdout", RETRIEVAL_CONTROL, holdout_cases())
    candidate = run(
        "holdout",
        candidate_identity(),
        holdout_cases(
            u1=metrics(answerable=False, rows=0),
            u2=metrics(answerable=False, rows=3),
            h0=metrics(rows=5),
        ),
    )
    report = p.promotion(control, candidate)
    assert report["all_gates_pass"], [
        g for g in report["gates"] if g["status"] == "fail"
    ]


def test_holdout_noise_reduction_never_buys_lost_recall():
    control = run("holdout", RETRIEVAL_CONTROL, holdout_cases())
    candidate = run(
        "holdout",
        candidate_identity(),
        holdout_cases(
            u1=metrics(answerable=False, rows=0),
            u2=metrics(answerable=False, rows=0),
            c2=metrics(critical=True, recall=0.0, hit=0, rows=2),
        ),
    )
    report = p.promotion(control, candidate)
    result = statuses(report)
    assert result["unanswerable_noise_not_worse"] == "pass"
    assert result["recall@8_not_below_control"] == "fail"
    assert result["hit_rate@8_not_below_control"] == "fail"
    assert result["no_evidence_lost_to_filtering"] == "fail"
    assert result["candidate_safety_critical_evidence_in_top_8"] == "fail"
    assert report["all_gates_pass"] is False


def test_holdout_filtering_loss_is_caught_even_when_aggregates_hold():
    control = run("holdout", RETRIEVAL_CONTROL, holdout_cases())
    # One case loses a span to filtering, another gains one: recall@8 is unchanged.
    candidate = run(
        "holdout",
        candidate_identity(),
        holdout_cases(
            c2=metrics(critical=True, recall=1.0),
            h0=metrics(recall=0.5, rows=4),
        ),
    )
    result = statuses(p.promotion(control, candidate))
    assert result["recall@8_not_below_control"] == "pass"
    assert result["no_evidence_lost_to_filtering"] == "fail"


def test_holdout_more_noise_fails():
    control = run(
        "holdout",
        RETRIEVAL_CONTROL,
        holdout_cases(u1=metrics(answerable=False, rows=4)),
    )
    candidate = run("holdout", candidate_identity(), holdout_cases())
    assert (
        statuses(p.promotion(control, candidate))["unanswerable_noise_not_worse"]
        == "fail"
    )


@pytest.mark.parametrize(
    "mutate",
    [
        lambda i: i["embedding"].update(model="gemini-embedding-001"),
        lambda i: i.update(chunking_version="journal-chunking-v2"),
        lambda i: i.update(database_image="postgres:18"),
        lambda i: i["retrieval"].update(limit=12),
        lambda i: i["retrieval"].update(version="journal-retrieval-v3"),
        lambda i: i["retrieval"].update(function="search_journal_memory_v1"),
        lambda i: i.update(extra=True),
    ],
)
def test_candidate_must_differ_from_the_control_only_by_retrieval_policy(mutate):
    identity = candidate_identity()
    identity["embedding"] = dict(identity["embedding"])
    mutate(identity)
    assert p.candidate_identity_problems(identity)
    assert not p.candidate_identity_problems(candidate_identity())
    assert p.candidate_identity_problems(
        {**RETRIEVAL_CONTROL, "system": "x"}
    )  # the control's own policy is not a candidate


def test_incomplete_or_unpinned_runs_never_promote():
    control = run("holdout", RETRIEVAL_CONTROL, holdout_cases())
    candidate = run("holdout", candidate_identity(), holdout_cases())
    assert p.promotion(control, candidate)["all_gates_pass"]
    filtered = run("holdout", candidate_identity(), holdout_cases(), case_filter=["h1"])
    assert statuses(p.promotion(control, filtered))["complete_coverage"] == "fail"
    reversed_roles = statuses(p.promotion(candidate, control))
    assert reversed_roles["reference_is_production_control"] == "fail"
    assert reversed_roles["candidate_differs_only_by_retrieval_policy"] == "fail"
    unpinned = {**candidate, "dataset": {**candidate["dataset"], "sha256": "0" * 64}}
    report = p.promotion(control, unpinned)
    assert statuses(report) == {"frozen_dataset": "fail"}
    unverified = run("holdout", candidate_identity(), holdout_cases())
    unverified["metadata"]["retrieval_run"]["database"]["function"] = (
        "search_journal_memory_v1"
    )
    assert (
        statuses(p.promotion(control, unverified))["policy_functions_verified"]
        == "fail"
    )
    leaky = run(
        "holdout",
        candidate_identity(),
        holdout_cases(h3={**metrics(), "cross_user_results": 1}),
    )
    assert (
        statuses(p.promotion(control, leaky))["candidate_zero_cross_user_results"]
        == "fail"
    )


def exported(run_dict, system):
    """A `run()` dict completed with the export fields `compare` checks."""
    scorer = JournalRetrievalScorer.configuration
    return {
        **run_dict,
        "evaluator": "journal_retrieval",
        "system": {"name": system, "version": "1.0.0", "configuration": {}},
        "failures": 0,
        "metadata": {
            **run_dict["metadata"],
            "scorer": scorer,
            "schema_version": "1",
            "harness_version": "0.1.0",
            "error_policy": "continue; exclude from metrics",
        },
        "cases": [
            {**case, "expected": {"id": case["id"]}, "tags": [], "error": None}
            for case in run_dict["cases"]
        ],
    }


def test_compare_cli_attaches_the_promotion_gates(tmp_path, capsys):
    from lifeos_ai_evals import __main__ as cli

    control = exported(run("holdout", RETRIEVAL_CONTROL, holdout_cases()), "control")
    candidate = exported(
        run(
            "holdout",
            candidate_identity(),
            holdout_cases(u1=metrics(answerable=False, rows=2)),
        ),
        "candidate",
    )
    paths = []
    for name, data in (("control", control), ("candidate", candidate)):
        path = tmp_path / f"{name}.json"
        path.write_text(json.dumps(data), encoding="utf-8")
        paths.append(str(path))
    output = tmp_path / "compare.json"
    assert cli.main(["compare", *paths, "--output", str(output)]) == 0
    result = json.loads(output.read_text(encoding="utf-8"))
    report = result["comparisons"][0]["acceptance"]
    assert report["version"] == "journal-retrieval-promotion-v1"
    assert report["all_gates_pass"] is True
    assert result["interpretation"] == plugin.INTERPRETATION
    printed = capsys.readouterr().out
    assert "Gate run 1 unanswerable_noise_not_worse: PASS" in printed
