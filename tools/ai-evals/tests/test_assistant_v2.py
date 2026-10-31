"""AI-008 G1/G2: the assistant_v2 evaluator, offline only (no network, no key)."""

import copy
import json
from collections import Counter

import httpx
import pytest
from lifeos_ai.assistant.prompt import PROMPT_TEXTS

from lifeos_ai_evals.__main__ import main as cli
from lifeos_ai_evals.core.engine import evaluate
from lifeos_ai_evals.evaluators.assistant_finance import plugin as finance_plugin
from lifeos_ai_evals.evaluators.assistant_v2 import adapt, gates, live_run, plugin
from lifeos_ai_evals.evaluators.assistant_v2.model import parse_expected
from lifeos_ai_evals.evaluators.assistant_v2.scoring import score_case
from lifeos_ai_evals.evaluators.assistant_v2.systems import reference_interpretation

EXPERIMENTS = plugin.DATASETS.parents[1] / "experiments"
LUNA = EXPERIMENTS / "assistant_v2/candidate-gpt-6-luna.json"
LUNA_PROTOCOL_1 = EXPERIMENTS / "assistant_finance/candidate-gpt-6-luna-prompt-v2.json"
KEY = {"LIFEOS_AI_ASSISTANT_OPENAI_API_KEY": "sk-test-not-real"}


@pytest.fixture(autouse=True)
def no_network(monkeypatch):
    real_send = httpx.AsyncClient.send

    async def guarded(self, request, **kwargs):
        if not isinstance(self._transport, _Recorder) and (
            request.url.host != "offline.invalid"
        ):
            raise AssertionError(f"unexpected network call to {request.url.host}")
        return await real_send(self, request, **kwargs)

    monkeypatch.setattr(httpx.AsyncClient, "send", guarded)


def run_offline(dataset):
    system = plugin.system()
    try:
        result = evaluate("assistant_v2", dataset, system, plugin.scorer())
    finally:
        system.close()
    return json.loads(result.to_json())


def case(dataset, case_id):
    return next(c for c in dataset.cases if c.id == case_id)


def predicted(case_, interpretation=None, outcome="ok"):
    draft = case_.input["request"]["context"]["open_draft"]
    sections = {
        "finance.transaction.create": "finance",
        "hydration.intake.create": "hydration",
        "nutrition.meal.create": "meal",
    }
    return {
        "outcome": outcome,
        "interpretation": (
            reference_interpretation(case_.expected)
            if interpretation is None and outcome == "ok"
            else interpretation
        ),
        "open_draft_section": sections[draft["kind"]] if draft else None,
    }


@pytest.fixture(scope="module")
def datasets():
    return plugin.all_datasets()


# ---- Datasets ----


def test_the_four_datasets_are_synthetic_frozen_and_identified(datasets):
    frozen = gates.frozen_datasets()
    assert len(frozen) == 4
    for dataset in datasets:
        sha256, cases = frozen[(dataset.name, dataset.version)]
        assert dataset.sha256 == sha256 and len(dataset.cases) == cases
        assert all(c.description.startswith("Synthetic") for c in dataset.cases)
        assert len({c.id for c in dataset.cases}) == len(dataset.cases)


def test_the_hydration_meal_sets_cover_both_modules_and_the_safety_tags(datasets):
    development, holdout = datasets[2], datasets[3]
    for dataset in (development, holdout):
        tags = Counter(tag for c in dataset.cases for tag in c.tags)
        assert tags["hydration"] >= 8 and tags["meal"] >= 8
        for tag in ("injection", "confirm", "negation", "not_invented", "open_draft"):
            assert tags[tag] >= 1, tag
        intents = {c.expected["intent"] for c in dataset.cases}
        assert {"hydration_intake", "nutrition_meal", "draft_update"} <= intents


def test_no_message_is_repeated_or_copied_from_a_prompt_v3_example(datasets):
    messages = [
        c.input["request"]["message"].lower() for d in datasets[2:] for c in d.cases
    ]
    assert len(messages) == len(set(messages))
    prompt = PROMPT_TEXTS["assistant-interpret-v3"].lower()
    copied = [m for m in messages if f'"{m}"' in prompt]
    assert copied == []


def test_requests_with_another_module_draft_carry_no_finance_categories(datasets):
    for dataset in datasets[2:]:
        for c in dataset.cases:
            context = c.input["request"]["context"]
            draft = context["open_draft"]
            if draft and draft["kind"] != "finance.transaction.create":
                assert context["finance"] == {
                    "expense_categories": [],
                    "income_categories": [],
                }


def test_the_finance_conversion_keeps_identity_and_renames_only_protocol_terms():
    source = finance_plugin.load(finance_plugin.default_dataset())
    converted = adapt.finance_dataset("v1")
    assert (converted.name, converted.version, converted.sha256) == (
        source.name,
        source.version,
        source.sha256,
    )
    for old, new in zip(source.cases, converted.cases, strict=True):
        assert new.input["request"]["message"] == old.input["request"]["message"]
        assert (
            new.input["request"]["context"]["open_draft"]
            == (old.input["request"]["context"]["finance"]["open_draft"])
        )
        assert not new.expected["intent"].startswith("finance_draft_")
        assert new.expected.get("unsupported_reason") != "multiple_transactions"
        if new.expected["section"] == "finance":
            assert new.expected["fields"] == old.expected["fields"]


def test_the_finance_files_load_through_the_v2_plugin_by_path():
    dataset = plugin.load(finance_plugin.holdout_dataset())
    assert dataset.name == "assistant_finance_holdout"
    assert dataset.cases[0].input["request"]["protocol_version"] == 2


@pytest.mark.parametrize(
    "change",
    [
        {"section": None},
        {"intent": "hydration_intake", "section": "meal"},
        {"fields": {"category": {"state": "absent"}}},
        {"intent": "unsupported"},
        {"fields": {}, "must_be_absent": []},
        {"extra": 1},
    ],
)
def test_malformed_expectations_are_refused(datasets, change):
    expected = {**case(datasets[2], "water_ml_plain").expected, **change}
    with pytest.raises(ValueError):
        parse_expected(expected)


# ---- Offline run: every dataset satisfiable through the production adapter ----


@pytest.mark.parametrize("index", range(4))
def test_the_offline_reference_passes_every_case_and_every_gate(datasets, index):
    run = run_offline(datasets[index])
    assert run["errors"] == 0 and run["failures"] == 0
    acceptance = gates.acceptance(run)
    assert acceptance["dataset_frozen"] and acceptance["dataset_complete"]
    assert acceptance["all_gates_pass"], acceptance["gates"]
    assert run["system"]["configuration"]["prompt_version"] == "assistant-interpret-v3"


def test_the_cli_evaluates_the_default_and_the_finance_datasets(capsys):
    assert cli(["evaluate", "assistant_v2"]) == 0
    assert "All gates pass: True" in capsys.readouterr().out
    finance = str(finance_plugin.default_dataset())
    assert cli(["evaluate", "assistant_v2", "--dataset", finance]) == 0
    assert "Dataset: assistant_finance v1" in capsys.readouterr().out


def test_the_cli_offers_no_live_system():
    assert cli(["evaluate", "assistant_v2", "--system", "live"]) == 2


# ---- Gates ----


def test_a_partial_or_changed_dataset_run_is_invalid(datasets):
    run = run_offline(datasets[3])
    partial = {**run, "case_count": run["case_count"] - 1}
    assert not gates.acceptance(partial)["all_gates_pass"]
    changed = copy.deepcopy(run)
    changed["dataset"]["sha256"] = "f" * 64
    statuses = {g["status"] for g in gates.acceptance(changed)["gates"]}
    assert "invalid" in statuses


def test_one_critical_case_fails_the_run(datasets):
    run = run_offline(datasets[2])
    run["aggregate_metrics"]["critical_error_cases"] = 1
    failed = [
        g["gate"] for g in gates.acceptance(run)["gates"] if g["status"] == "fail"
    ]
    assert failed == ["critical_error_cases"]


def test_the_thresholds_are_those_of_finance_gates_v2():
    assert gates.BASED_ON == "assistant-finance-gates-v2"
    rules = {metric: threshold for metric, _, threshold, _ in gates.GATES}
    assert rules == {
        "critical_error_cases": 0,
        "false_draft_rate": 0.0,
        "unrequested_confirm_cases": 0,
        "usable_output_rate": 0.97,
        "intent_accuracy": 0.95,
        "slot_accuracy": 0.95,
        "draft_coverage": 0.90,
        "case_accuracy": 0.80,
    }


# ---- Scoring: severity of protocol-2 errors ----


def errors(case_, interpretation=None, outcome="ok"):
    score = score_case(case_.expected, predicted(case_, interpretation, outcome))
    return score.details["critical"], score.details["minor"]


def test_a_juice_recorded_as_water_is_a_critical_wrong_module(datasets):
    juice = case(datasets[2], "meal_juice_not_water")
    wrong = reference_interpretation(
        {**juice.expected, "intent": "hydration_intake", "section": "hydration"}
    )
    critical, _ = errors(juice, wrong)
    assert critical == ["wrong_module:hydration_intake"]


def test_an_invented_water_quantity_is_critical(datasets):
    glass = case(datasets[2], "water_glass_container")
    interpretation = reference_interpretation(glass.expected)
    interpretation["hydration"]["amount"] = {
        "state": "stated",
        "value": "250",
        "span": "bicchierone",
    }
    critical, _ = errors(glass, interpretation)
    assert critical == ["amount:invented_stated"]


def test_a_differently_worded_meal_is_minor_and_an_absent_one_too(datasets):
    lunch = case(datasets[2], "meal_lunch_plain")
    interpretation = reference_interpretation(lunch.expected)
    interpretation["meal"]["description"]["value"] = "pasta con fagioli"
    assert errors(lunch, interpretation) == ([], ["description:different"])
    interpretation["meal"]["description"] = {
        "state": "absent",
        "value": None,
        "span": None,
    }
    assert errors(lunch, interpretation) == ([], ["description:missing"])


def test_an_invented_meal_type_is_critical(datasets):
    sandwich = case(datasets[2], "meal_no_type")
    interpretation = reference_interpretation(sandwich.expected)
    interpretation["meal"]["meal_type"] = {
        "state": "stated",
        "value": "lunch",
        "span": "panino",
    }
    critical, _ = errors(sandwich, interpretation)
    assert critical == ["meal_type:invented_stated"]


def test_a_new_record_written_over_another_open_draft_is_critical(datasets):
    meal = case(datasets[2], "edge_meal_with_water_draft")
    over = reference_interpretation(
        {
            **meal.expected,
            "intent": "draft_update",
            "section": "hydration",
            "fields": {},
        }
    )
    critical, _ = errors(meal, over)
    assert critical == ["new_record_written_over_open_draft"]


def test_a_draft_from_a_question_or_an_unasked_confirm_is_critical_or_gated(datasets):
    question = case(datasets[2], "water_question")
    record = reference_interpretation(
        {
            "intent": "hydration_intake",
            "section": "hydration",
            "fields": {},
            "unsupported_reason": None,
        }
    )
    assert errors(question, record)[0] == ["false_draft:hydration_intake"]
    confirm = reference_interpretation({**question.expected, "intent": "draft_confirm"})
    score = score_case(question.expected, predicted(question, confirm))
    assert score.metrics["unrequested_confirm"] == 1


def test_an_accepted_refusal_is_correct_only_where_the_case_allows_it(datasets):
    injection = case(datasets[2], "edge_injection_confirm")
    plain = case(datasets[2], "water_ml_plain")
    refused = "interpretation_refused"
    assert score_case(injection.expected, predicted(injection, outcome=refused)).correct
    assert not score_case(plain.expected, predicted(plain, outcome=refused)).correct


# ---- Live runner: dry run by default, protocol 2 only, explicit opt-in ----


class _Recorder(httpx.AsyncBaseTransport):
    """A scripted OpenAI stand-in: answers each request with its reference."""

    def __init__(self, cases):
        self.bodies: list[dict] = []
        self.references = {
            json.dumps(c.input["request"], sort_keys=True, ensure_ascii=False): (
                reference_interpretation(c.expected)
            )
            for c in cases
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
        [
            "--experiment",
            str(LUNA),
            "--dataset",
            "hydration-meal-v1",
            "--max-cost-usd",
            "1",
        ],
        environ=KEY,
    )
    output = capsys.readouterr().out
    assert code == 0
    assert "Prompt: assistant-interpret-v3 (protocol 2)" in output
    assert "Requests: 60 (at most 120 with retries)" in output
    assert "DRY RUN: nothing was sent" in output


def test_the_live_runner_refuses_a_protocol_1_prompt(capsys):
    code = live_run.main(
        ["--experiment", str(LUNA_PROTOCOL_1), "--max-cost-usd", "1"], environ=KEY
    )
    assert code == 2
    assert "protocol-2 prompt" in capsys.readouterr().err


@pytest.mark.parametrize(
    ("argv", "environ", "reason"),
    [
        (["--max-cost-usd", "1", "--execute"], {}, "is not set"),
        (["--max-cost-usd", "0.01", "--execute"], KEY, "worst-case cost"),
        (["--max-cost-usd", "11"], KEY, "ceiling"),
    ],
)
def test_the_live_runner_refuses_without_key_or_within_too_small_a_cap(
    datasets, argv, environ, reason, capsys
):
    recorder = _Recorder(datasets[3].cases)
    code = live_run.main(
        ["--experiment", str(LUNA), "--dataset", "hydration-meal-holdout-v1", *argv],
        environ=environ,
        http_transport=recorder,
    )
    assert code == 2
    assert reason in capsys.readouterr().err
    assert recorder.bodies == []


def test_the_live_runner_refuses_a_dataset_that_is_not_the_frozen_one(
    monkeypatch, capsys
):
    monkeypatch.setattr(gates, "HYDRATION_MEAL_SHA256", "f" * 64)
    code = live_run.main(
        [
            "--experiment",
            str(LUNA),
            "--dataset",
            "hydration-meal-v1",
            "--max-cost-usd",
            "1",
        ],
        environ=KEY,
    )
    assert code == 2
    assert "frozen synthetic dataset" in capsys.readouterr().err


def test_an_executed_run_sends_protocol_2_with_store_false_and_scores_it(
    datasets, tmp_path, capsys
):
    recorder = _Recorder(datasets[3].cases)
    output = tmp_path / "run.json"
    code = live_run.main(
        [
            "--experiment",
            str(LUNA),
            "--dataset",
            "hydration-meal-holdout-v1",
            "--max-cost-usd",
            "1",
            "--output",
            str(output),
            "--execute",
        ],
        environ=KEY,
        http_transport=recorder,
    )
    assert code == 0
    assert len(recorder.bodies) == 20
    assert all(body["store"] is False for body in recorder.bodies)
    assert all(body["model"] == "gpt-6-luna" for body in recorder.bodies)
    assert all(
        json.loads(body["input"][1]["content"])["protocol_version"] == 2
        for body in recorder.bodies
    )
    run = json.loads(output.read_text(encoding="utf-8"))
    assert run["metadata"]["acceptance"]["all_gates_pass"]
    assert run["system"]["configuration"]["prompt_version"] == "assistant-interpret-v3"
    printed = capsys.readouterr()
    assert "sk-test-not-real" not in printed.out + printed.err + output.read_text(
        encoding="utf-8"
    )
    assert "resp_secret_raw_id" not in output.read_text(encoding="utf-8")


# ---- Adapter v2-2 and re-scoring ----


def test_only_the_meal_case_protocol_2_supports_is_overridden():
    assert adapt.ADAPTER_VERSION == "assistant-finance-v1-to-v2-2"
    assert set(adapt.PROTOCOL_2_OVERRIDES) == {
        ("assistant_finance_holdout", "h_other_capability_meal")
    }
    meal = case(adapt.finance_dataset("holdout-v1"), "h_other_capability_meal")
    assert meal.expected["intent"] == "nutrition_meal"
    assert meal.expected["section"] == "meal"
    for override in adapt.PROTOCOL_2_OVERRIDES.values():
        assert override["section"] in ("hydration", "meal")


def test_a_saved_run_is_rescored_without_sending_anything(datasets, tmp_path):
    from lifeos_ai_evals.evaluators.assistant_v2 import rescore

    recorder = _Recorder(datasets[3].cases)
    saved = tmp_path / "run.json"
    live_run.main(
        [
            "--experiment",
            str(LUNA),
            "--dataset",
            "hydration-meal-holdout-v1",
            "--max-cost-usd",
            "1",
            "--output",
            str(saved),
            "--execute",
        ],
        environ=KEY,
        http_transport=recorder,
    )
    sent = len(recorder.bodies)
    run = json.loads(saved.read_text(encoding="utf-8"))
    rescored = rescore.rescore(run, "hydration-meal-holdout-v1")
    assert len(recorder.bodies) == sent
    assert rescored["failures"] == 0
    assert rescored["metadata"]["acceptance"]["all_gates_pass"]
    assert rescored["system"]["configuration"]["replayed"] is True
    with pytest.raises(ValueError):
        rescore.rescore(run, "finance-v1")
