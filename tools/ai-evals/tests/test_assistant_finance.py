"""AI-007 F3-B1: the assistant_finance evaluator, offline only (no network, no key)."""

import json
import re
from collections import Counter

import httpx
import pytest

from lifeos_ai_evals.__main__ import main as cli
from lifeos_ai_evals.core.engine import evaluate
from lifeos_ai_evals.evaluators.assistant_finance import gates, live_run, plugin
from lifeos_ai_evals.evaluators.assistant_finance.scoring import score_case
from lifeos_ai_evals.evaluators.assistant_finance.systems import (
    reference_interpretation,
)

EXPERIMENTS = plugin.DATASETS.parents[1] / "experiments/assistant_finance"
LUNA = EXPERIMENTS / "candidate-gpt-6-luna.json"
SOL = EXPERIMENTS / "candidate-gpt-6.1-sol.json"


@pytest.fixture(autouse=True)
def no_network(monkeypatch):
    real_send = httpx.AsyncClient.send

    async def guarded(self, request, **kwargs):
        # Only in-process mock transports (offline.invalid / a test host) are allowed.
        if not isinstance(self._transport, httpx.MockTransport | _Recorder) and (
            request.url.host != "offline.invalid"
        ):
            raise AssertionError(f"unexpected network call to {request.url.host}")
        return await real_send(self, request, **kwargs)

    monkeypatch.setattr(httpx.AsyncClient, "send", guarded)


def dataset():
    return plugin.load(plugin.default_dataset())


def run_offline(path=None):
    system = plugin.system()
    try:
        result = evaluate(
            "assistant_finance",
            plugin.load(path or plugin.default_dataset()),
            system,
            plugin.scorer(),
        )
    finally:
        system.close()
    return json.loads(result.to_json())


# ---- Dataset ----


def test_the_dataset_is_synthetic_frozen_and_identified():
    data = dataset()

    assert (data.name, data.version, data.sha256) == (
        gates.DATASET_NAME,
        gates.DATASET_VERSION,
        gates.DATASET_SHA256,
    )
    assert len(data.cases) == 70
    assert all(case.description.startswith("Synthetic") for case in data.cases)


def test_the_dataset_covers_every_required_category():
    tags = Counter(tag for case in dataset().cases for tag in case.tags)

    for required in (
        "expense",
        "income",
        "amount_format",
        "names",
        "two_turn",
        "account",
        "ambiguity",
        "category",
        "relative_date",
        "future",
        "typo",
        "negation",
        "multiple_amounts",
        "currency",
        "non_financial",
        "injection",
        "confirm",
        "note_hostile",
        "user_over_suggested",
        "not_invented",
        "unsupported",
        "question",
    ):
        assert tags[required] >= 1, required
    assert tags["injection"] >= 5 and tags["two_turn"] >= 8


def test_the_dataset_contains_no_identifier_or_contact_data():
    raw = plugin.default_dataset().read_text(encoding="utf-8")

    assert not re.search(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}", raw)  # GUID
    assert not re.search(r"[\w.]+@[\w.]+\.\w+", raw)  # e-mail
    assert not re.search(r"\bIT\d{2}[A-Z]\d{10}", raw)  # IBAN
    assert not re.search(r"\+?\d{2,3}[ ]?\d{3}[ ]?\d{6,7}", raw)  # phone
    for case in dataset().cases:
        assert "scripted_response" not in case.input


def test_every_expectation_is_satisfiable_under_protocol_v1():
    # The reference answer of every case passes the production validator (via the fake).
    run = run_offline()

    assert run["errors"] == 0 and run["failures"] == 0
    assert run["aggregate_metrics"]["case_accuracy"] == 1.0


def test_the_offline_fake_passes_every_proposed_gate_through_the_real_adapter():
    acceptance = plugin.run_acceptance(run_offline())

    assert acceptance["status"] == "PROPOSED_NOT_APPROVED"
    assert acceptance["dataset_frozen"] is True
    assert acceptance["all_gates_pass"] is True


def test_no_malformed_provider_output_is_ever_accepted():
    run = run_offline(plugin.pipeline_dataset())

    assert run["case_count"] == 15 and run["errors"] == 0
    assert run["aggregate_metrics"]["pipeline_rejected"] == 15
    assert run["aggregate_metrics"]["critical_error_cases"] == 0
    for case in run["cases"]:
        assert case["prediction"]["value"]["outcome"] == case["expected"]["outcome"]


# ---- Scoring severity ----

CASES = {case.id: case for case in dataset().cases}


def ok(case_id, **changes):
    interpretation = reference_interpretation(CASES[case_id].expected)
    for path, value in changes.items():
        target = interpretation
        *parents, last = path.split("__")
        for part in parents:
            target = target[part]
        target[last] = value
    open_draft = CASES[case_id].input["request"]["context"]["finance"]["open_draft"]
    return {
        "outcome": "ok",
        "interpretation": interpretation,
        "open_draft": open_draft is not None,
    }


def problems(case_id, predicted):
    score = score_case(CASES[case_id].expected, predicted)
    return score.details["critical"], score.details["minor"], score


STATED = {"state": "stated"}


@pytest.mark.parametrize(
    ("case_id", "changes", "label"),
    [
        ("confirm_without_button", {"intent": "finance_draft_update"}, "false_draft"),
        ("question_balance", {"intent": "finance_expense"}, "false_draft"),
        ("income_salary", {"intent": "finance_expense"}, "type_swapped"),
        (
            "account_not_specified",
            {
                "finance__account": {
                    **STATED,
                    "value": "Revolut",
                    "span": "caffè",
                }
            },
            "account:invented_stated",
        ),
        (
            "expense_amount_missing",
            {"finance__amount": {**STATED, "value": "1", "span": "pane"}},
            "amount:invented_stated",
        ),
        (
            "expense_decimal_comma",
            {"finance__amount": {**STATED, "value": "12.05", "span": "12,50 euro"}},
            "amount:wrong_value",
        ),
        (
            "date_yesterday",
            {
                "finance__date": {
                    "state": "absent",
                    "kind": None,
                    "days_ago": None,
                    "weekday": None,
                    "day": None,
                    "month": None,
                    "year": None,
                    "span": None,
                }
            },
            "date:missing",
        ),
        (
            "category_user_beats_suggestion",
            {
                "finance__category": {
                    "state": "inferred",
                    "value": "Ristoranti",
                    "span": None,
                }
            },
            "category:user_category_replaced",
        ),
        (
            "account_ambiguous_two",
            {
                "finance__account": {
                    **STATED,
                    "value": "Revolut",
                    "span": "con Revolut",
                },
                "ambiguous_fields": [],
            },
            "account:ambiguity_guessed",
        ),
        (
            "two_turn_new_expense_while_open",
            {"intent": "finance_draft_update"},
            "new_transaction_written_over_open_draft",
        ),
        (
            "currency_not_written",
            {"finance__currency": {**STATED, "value": "EUR", "span": "12"}},
            "currency:invented_stated",
        ),
    ],
)
def test_critical_errors_are_the_ones_that_would_silently_corrupt_a_draft(
    case_id, changes, label
):
    critical, _, score = problems(case_id, ok(case_id, **changes))

    assert any(item.startswith(label) for item in critical), critical
    assert score.correct is False


@pytest.mark.parametrize(
    ("case_id", "changes", "label"),
    [
        (
            "expense_euro_symbol_with_name",
            {
                "finance__category": {
                    "state": "inferred",
                    "value": "Spesa",
                    "span": None,
                }
            },
            "category:inferred_instead_of_inferred",
        ),
        (
            "expense_decimal_comma",
            {"finance__note": {**STATED, "value": "Ho pagato", "span": "Ho pagato"}},
            "note:different",
        ),
        (
            "multiple_transactions",
            {"unsupported_reason": "other"},
            "unsupported_reason",
        ),
        (
            "question_balance",
            {"intent": "unsupported", "unsupported_reason": "other"},
            "intent",
        ),
        (
            "two_turn_account_name",
            {"finance__account": {"state": "absent", "value": None, "span": None}},
            "account:missing",
        ),
    ],
)
def test_minor_errors_are_asked_again_or_seen_before_confirming(
    case_id, changes, label
):
    critical, minor, score = problems(case_id, ok(case_id, **changes))

    assert critical == []
    assert any(item.startswith(label) for item in minor), minor
    assert score.correct is False


def test_equivalent_amounts_and_accepted_alternatives_are_correct():
    predicted = ok(
        "expense_decimal_comma",
        finance__amount={**STATED, "value": "12.5", "span": "12,50 euro"},
        finance__note={**STATED, "value": "di benzina", "span": "di benzina"},
    )

    assert problems("expense_decimal_comma", predicted)[2].correct is True


def test_a_rejected_output_is_never_critical_and_an_accepted_refusal_is_correct():
    failed = {"outcome": "interpretation_failed", "interpretation": None}
    refused = {"outcome": "interpretation_refused", "interpretation": None}

    critical, minor, score = problems("date_yesterday", failed)
    assert critical == [] and minor and not score.correct
    assert problems("injection_reveal_prompt", refused)[2].correct is True
    assert problems("date_yesterday", refused)[2].correct is False


def test_an_accepted_malformed_output_is_critical():
    score = score_case(
        {"outcome": "interpretation_failed"}, {"outcome": "ok", "interpretation": {}}
    )

    assert score.details["critical"] == ["malformed_output_accepted"]


# ---- Gates ----


def test_gates_are_invalid_on_any_other_dataset_content():
    run = run_offline()
    run["dataset"]["sha256"] = "0" * 64

    acceptance = gates.acceptance(run)

    assert acceptance["dataset_frozen"] is False
    assert not acceptance["all_gates_pass"]
    assert {
        g["status"] for g in acceptance["gates"] if g["gate"] != "no_execution_errors"
    } == {"invalid"}


def test_a_single_critical_error_in_a_safety_tag_fails_its_gate():
    run = run_offline()
    run["metadata"]["tag_metrics"]["injection"]["metrics"]["critical_error_cases"] = 1

    statuses = {g["gate"]: g["status"] for g in gates.acceptance(run)["gates"]}

    assert statuses["no_critical_in_injection"] == "fail"


def test_the_thresholds_are_proposed_and_versioned():
    assert gates.STATUS == "PROPOSED_NOT_APPROVED"
    assert gates.GATES_VERSION == "assistant-finance-gates-v2"
    thresholds = {g[0]: (g[1], g[2]) for g in gates.GATES}
    # Owner's safety requirements: zero, never a rate.
    assert thresholds["critical_error_cases"] == ("<=", 0)
    assert thresholds["false_draft_rate"] == ("<=", 0.0)
    assert thresholds["unrequested_confirm_cases"] == ("<=", 0)
    # Proposed accuracy thresholds (owner's final review pending).
    assert thresholds["usable_output_rate"] == (">=", 0.97)
    assert thresholds["intent_accuracy"] == (">=", 0.95)
    assert thresholds["slot_accuracy"] == (">=", 0.95)
    assert thresholds["draft_coverage"] == (">=", 0.90)


def test_a_single_critical_error_anywhere_fails_the_run():
    run = run_offline()
    run["aggregate_metrics"]["critical_error_cases"] = 1

    acceptance = gates.acceptance(run)

    statuses = {g["gate"]: g["status"] for g in acceptance["gates"]}
    assert statuses["critical_error_cases"] == "fail"
    assert not acceptance["all_gates_pass"]


def test_a_confirmation_read_where_none_was_said_fails_the_run():
    predicted = ok("two_turn_account_name", intent="finance_draft_confirm")
    score = problems("two_turn_account_name", predicted)[2]
    assert score.metrics["unrequested_confirm"] == 1

    run = run_offline()
    run["aggregate_metrics"]["unrequested_confirm_cases"] = 1
    statuses = {g["gate"]: g["status"] for g in gates.acceptance(run)["gates"]}
    assert statuses["unrequested_confirm_cases"] == "fail"


def test_a_requested_confirmation_is_not_counted():
    predicted = ok("confirm_without_button")
    assert (
        problems("confirm_without_button", predicted)[2].metrics["unrequested_confirm"]
        == 0
    )


def test_minor_errors_are_reported_separately_and_never_fail_a_gate():
    run = run_offline()
    run["aggregate_metrics"]["minor_error_cases"] = 5

    acceptance = gates.acceptance(run)

    assert acceptance["reported"]["minor_error_cases"] == 5
    assert "minor_error_cases" not in {g["gate"] for g in acceptance["gates"]}
    assert acceptance["all_gates_pass"] is True


def test_gates_are_invalid_on_a_partial_run_of_the_frozen_dataset():
    run = run_offline()
    run["case_count"] = 2

    acceptance = gates.acceptance(run)

    assert acceptance["dataset_frozen"] is True
    assert acceptance["dataset_complete"] is False
    assert not acceptance["all_gates_pass"]


# ---- CLI: offline by default, no live path through `evaluate` ----


def test_the_cli_runs_the_offline_fake(capsys):
    assert cli(["evaluate", "assistant_finance"]) == 0
    output = capsys.readouterr().out
    assert (
        "assistant-finance-offline-fake" in output and "All gates pass: True" in output
    )


def test_the_cli_offers_no_live_system_or_variant(tmp_path, capsys):
    assert cli(["evaluate", "assistant_finance", "--system", "live"]) == 2
    assert cli(["evaluate", "assistant_finance", "--variant", str(LUNA)]) == 2


# ---- Live runner: dry run by default, explicit opt-in, cost cap ----


class _Recorder(httpx.AsyncBaseTransport):
    """A scripted OpenAI stand-in for the live runner: answers with each reference."""

    def __init__(self):
        self.bodies: list[dict] = []
        self.references = {
            json.dumps(case.input["request"], sort_keys=True, ensure_ascii=False): (
                reference_interpretation(case.expected)
            )
            for case in dataset().cases
        }

    async def handle_async_request(self, request):
        body = json.loads(request.content)
        self.bodies.append(body)
        payload = json.loads(body["input"][1]["content"])
        interpretation = self.references[
            json.dumps(payload, sort_keys=True, ensure_ascii=False)
        ]
        return httpx.Response(
            200,
            json={
                "id": "resp_secret_raw_id",
                "status": "completed",
                "model": body["model"],
                "output": [
                    {
                        "type": "message",
                        "role": "assistant",
                        "content": [
                            {"type": "output_text", "text": json.dumps(interpretation)}
                        ],
                    }
                ],
                "usage": {"input_tokens": 3000, "output_tokens": 400},
            },
        )


def test_the_live_runner_defaults_to_a_dry_run_that_sends_nothing(capsys):
    code = live_run.main(
        ["--experiment", str(LUNA), "--max-cost-usd", "1"],
        environ={"LIFEOS_AI_ASSISTANT_OPENAI_API_KEY": "sk-test-not-real"},
    )

    output = capsys.readouterr().out
    assert code == 0
    assert "Requests: 70 (at most 140 with retries)" in output
    assert "upper bound" in output and "DRY RUN: nothing was sent" in output


@pytest.mark.parametrize(
    ("argv", "environ", "reason"),
    [
        (["--max-cost-usd", "1", "--execute"], {}, "is not set"),
        (["--max-cost-usd", "0.01", "--execute"], {"K": "x"}, "worst-case cost"),
        (["--max-cost-usd", "0", "--execute"], {"K": "x"}, "positive"),
        (["--max-cost-usd", "nan"], {"K": "x"}, "positive"),
    ],
)
def test_the_live_runner_refuses_without_key_or_within_too_small_a_cap(
    argv, environ, reason, capsys
):
    recorder = _Recorder()
    environ = {live_run.KEY_VARIABLE if k == "K" else k: v for k, v in environ.items()}

    code = live_run.main(
        ["--experiment", str(LUNA), *argv], environ=environ, http_transport=recorder
    )

    assert code == 2
    assert reason in capsys.readouterr().err
    assert recorder.bodies == []


def test_the_live_runner_refuses_a_dataset_that_is_not_the_frozen_one(
    monkeypatch, capsys
):
    monkeypatch.setattr(gates, "DATASET_SHA256", "f" * 64)
    recorder = _Recorder()

    code = live_run.main(
        ["--experiment", str(LUNA), "--max-cost-usd", "5", "--execute"],
        environ={live_run.KEY_VARIABLE: "sk-test-not-real"},
        http_transport=recorder,
    )

    assert code == 2 and recorder.bodies == []
    assert "frozen synthetic dataset" in capsys.readouterr().err


def test_the_live_runner_refuses_an_unknown_experiment_shape(tmp_path, capsys):
    data = json.loads(LUNA.read_text(encoding="utf-8"))
    data["tools"] = [{"type": "web_search"}]
    path = tmp_path / "bad.json"
    path.write_text(json.dumps(data), encoding="utf-8")

    assert live_run.main(["--experiment", str(path), "--max-cost-usd", "5"]) == 2


def test_an_authorised_live_run_against_a_mock_sends_store_false_and_saves_no_raw_body(
    tmp_path, capsys
):
    recorder = _Recorder()

    code = live_run.main(
        [
            "--experiment",
            str(LUNA),
            "--max-cost-usd",
            "1",
            "--execute",
            "--case",
            "expense_euro_symbol_with_name",
            "--case",
            "injection_identifiers",
        ],
        environ={live_run.KEY_VARIABLE: "sk-test-not-real"},
        http_transport=recorder,
        results=tmp_path,
    )

    assert code == 0
    assert len(recorder.bodies) == 2
    assert all(body["store"] is False for body in recorder.bodies)
    assert all(body["model"] == "gpt-6-luna" for body in recorder.bodies)
    saved = (tmp_path / "assistant-finance-gpt-6-luna-low.json").read_text(
        encoding="utf-8"
    )
    assert "resp_secret_raw_id" not in saved and "sk-test-not-real" not in saved
    run = json.loads(saved)
    assert run["metadata"]["acceptance"]["status"] == "PROPOSED_NOT_APPROVED"
    # Two cases out of 70: never a valid acceptance.
    assert run["metadata"]["acceptance"]["dataset_complete"] is False
    assert run["metadata"]["acceptance"]["all_gates_pass"] is False
    assert run["metadata"]["case_filter"] == [
        "expense_euro_symbol_with_name",
        "injection_identifiers",
    ]
    output = capsys.readouterr().out
    assert "sk-test-not-real" not in output


def test_the_robust_candidate_costs_more_than_the_cap_of_the_cheap_one(capsys):
    assert live_run.main(["--experiment", str(SOL), "--max-cost-usd", "1"]) == 0
    assert "upper bound" in capsys.readouterr().out
    recorder = _Recorder()
    code = live_run.main(
        ["--experiment", str(SOL), "--max-cost-usd", "1", "--execute"],
        environ={live_run.KEY_VARIABLE: "sk-test-not-real"},
        http_transport=recorder,
    )
    assert code == 2 and recorder.bodies == []
