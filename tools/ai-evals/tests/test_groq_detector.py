import json
from dataclasses import asdict
from types import SimpleNamespace

import pytest

from lifeos_ai_evals.__main__ import main
from lifeos_ai_evals.core.comparison import compare_results, format_comparison
from lifeos_ai_evals.core.engine import Predictor, evaluate
from lifeos_ai_evals.evaluators.trip_detection import plugin
from lifeos_ai_evals.evaluators.trip_detection.groq_detector import (
    PROMPT_VERSION,
    SCHEMA,
    GroqTripDetector,
    convert_output,
    serialize_events,
)
from lifeos_ai_evals.evaluators.trip_detection.model import Trip


class FakeModel:
    def __init__(self, responses):
        self.responses = iter(responses)
        self.calls = 0
        self.prompts = []

    def with_structured_output(self, schema, **kwargs):
        assert schema == SCHEMA
        assert kwargs == dict(method="json_schema", strict=True, include_raw=True)
        return self

    def invoke(self, prompt):
        self.calls += 1
        self.prompts.append(prompt)
        value = next(self.responses)
        if isinstance(value, Exception):
            raise value
        return value


def response(trips=None, explanations=None, usage=None):
    return {
        "parsed": {"trips": trips or [], "explanations": explanations or []},
        "parsing_error": None,
        "raw": SimpleNamespace(usage_metadata=usage),
    }


def interval(start="2026-09-10", end="2026-09-12"):
    return {"start_date": start, "end_date": end}


def test_predictor_contract_and_prompt_identity():
    fake = FakeModel([response([interval()], ["Overnight evidence."])])
    detector: Predictor = GroqTripDetector(model=fake, sleep=lambda _: None)
    prediction = detector.predict(())
    assert prediction.value == (Trip("2026-09-10", "2026-09-12"),)
    assert prediction.explanations == ("Overnight evidence.",)
    assert detector.name == "groq-langchain-trip-detector"
    assert detector.version == "1.0.0"
    assert detector.configuration["prompt_version"] == PROMPT_VERSION
    assert detector.configuration["provider"] == "groq"
    assert detector.configuration["model"] == "openai/gpt-oss-20b"
    assert detector.configuration["temperature"] == 0
    assert len(fake.prompts[0].to_messages()) == 2


def test_events_exclude_labels_and_are_stable():
    dataset = plugin.load(plugin.default_dataset())
    case = dataset.cases[1]
    payload = serialize_events(case.input)
    assert payload == serialize_events(tuple(reversed(case.input)))
    events = json.loads(payload)
    assert all(set(event) == set(asdict(case.input[0])) for event in events)
    assert "expected" not in payload and "tags" not in payload
    assert case.id not in payload and case.description not in payload
    assert len(events) == len(case.input)


@pytest.mark.parametrize(
    "trips",
    [[], [interval()], [interval(), interval("2026-10-01", "2026-10-03")]],
)
def test_empty_single_multiple(trips):
    prediction = convert_output({"trips": trips, "explanations": []})
    assert prediction.value == tuple(Trip(**item) for item in trips)


@pytest.mark.parametrize(
    "value",
    [
        None,
        {},
        {"trips": [], "explanations": "wrong"},
        {"trips": [], "explanations": [1]},
        {"trips": [], "explanations": [], "extra": True},
        {"trips": [interval("2026-09-12", "2026-09-10")], "explanations": []},
        {"trips": [interval("2026-02-30")], "explanations": []},
        {"trips": [interval("20260910")], "explanations": []},
        {"trips": [interval(), interval()], "explanations": []},
        {"trips": [{"start_date": "2026-09-10"}], "explanations": []},
    ],
)
def test_invalid_output(value):
    with pytest.raises(ValueError):
        convert_output(value)


def test_strict_schema_objects():
    for schema in (SCHEMA, SCHEMA["properties"]["trips"]["items"]):
        assert schema["additionalProperties"] is False
        assert set(schema["required"]) == set(schema["properties"])


def test_missing_key_before_evaluation(monkeypatch, capsys):
    monkeypatch.delenv("GROQ_API_KEY", raising=False)
    monkeypatch.setattr(
        "lifeos_ai_evals.__main__.evaluate",
        lambda *_: pytest.fail("evaluation must not begin"),
    )
    assert main(["evaluate", "trip_detection", "--system", "groq"]) == 2
    assert "GROQ_API_KEY is required" in capsys.readouterr().err


def test_baseline_without_key_and_cli_selection(monkeypatch, tmp_path):
    monkeypatch.delenv("GROQ_API_KEY", raising=False)
    baseline = tmp_path / "baseline.json"
    assert (
        main(
            [
                "evaluate",
                "trip_detection",
                "--system",
                "baseline",
                "--output",
                str(baseline),
            ]
        )
        == 0
    )
    run = json.loads(baseline.read_text())
    assert run["aggregate_metrics"]["true_positives"] == 8
    assert run["aggregate_metrics"]["false_positives"] == 1
    assert run["aggregate_metrics"]["false_negatives"] == 2
    assert main(["evaluate", "trip_detection", "--system", "unknown"]) == 2
    fake = FakeModel([response()] * 16)
    detector = GroqTripDetector(model=fake, sleep=lambda _: None)
    original = plugin.system
    monkeypatch.setattr(
        plugin, "system", lambda name: detector if name == "groq" else original(name)
    )
    output = tmp_path / "groq.json"
    assert (
        main(
            ["evaluate", "trip_detection", "--system", "groq", "--output", str(output)]
        )
        == 0
    )
    assert json.loads(output.read_text())["metadata"]["usage"] is None
    comparison = tmp_path / "comparison.json"
    assert (
        main(["compare", str(baseline), str(output), "--output", str(comparison)]) == 0
    )
    assert "deltas" in json.loads(comparison.read_text())


class RateLimitError(Exception):
    status_code = 429

    def __init__(self, retry_after=None):
        self.response = SimpleNamespace(
            headers={} if retry_after is None else {"retry-after": retry_after}
        )


def test_retry_after_and_bounded_attempts():
    delays = []
    fake = FakeModel([RateLimitError("7"), RateLimitError(), response()])
    detector = GroqTripDetector(model=fake, sleep=delays.append)
    assert detector.predict(()).value == ()
    assert delays == [7, 4]
    assert fake.calls == 3
    fake = FakeModel([RateLimitError()] * 4)
    detector = GroqTripDetector(model=fake, sleep=lambda _: None)
    with pytest.raises(RateLimitError):
        detector.predict(())
    assert fake.calls == 3


def test_long_retry_after_does_not_retry_early():
    fake = FakeModel([RateLimitError("120"), response()])
    detector = GroqTripDetector(model=fake, sleep=lambda _: pytest.fail("no sleep"))
    with pytest.raises(RateLimitError):
        detector.predict(())
    assert fake.calls == 1


def test_retry_http_date():
    from datetime import UTC, datetime, timedelta
    from email.utils import format_datetime

    header = format_datetime(datetime.now(UTC) + timedelta(seconds=20), usegmt=True)
    assert 18 <= GroqTripDetector.retry_delay(RateLimitError(header), 1) <= 20


@pytest.mark.parametrize(
    "failure",
    [
        TimeoutError("secret-token"),
        RuntimeError("secret-token"),
        RateLimitError(),
        {"parsed": None, "parsing_error": ValueError("secret-token")},
        response([interval("invalid")]),
    ],
)
def test_provider_errors_are_not_fp_fn_and_secrets_excluded(failure):
    # One failed negative case, then 15 valid empty predictions.
    prefix = [failure] * 3 if isinstance(failure, RateLimitError) else [failure]
    fake = FakeModel(prefix + [response()] * 15)
    detector = GroqTripDetector(model=fake, sleep=lambda _: None)
    dataset = plugin.load(plugin.default_dataset())
    run = evaluate("trip_detection", dataset, detector, plugin.scorer())
    assert run.errors == 1 and run.metadata["scored_cases"] == 15
    assert run.cases[0].status == "error"
    assert run.cases[0].score is None and run.cases[0].error["stage"] == "prediction"
    assert run.aggregate_metrics["false_positives"] == 0
    assert "secret-token" not in run.to_json()
    assert "raw" not in run.to_json()


def test_usage_optional_and_no_reasoning_persisted():
    fake = FakeModel(
        [
            response(usage=dict(input_tokens=12, output_tokens=4, total_tokens=16)),
            response(),
        ]
    )
    detector = GroqTripDetector(model=fake, sleep=lambda _: None)
    detector.predict(())
    detector.predict(())
    assert detector.experiment_metadata()["usage"] == dict(
        input_tokens=12, output_tokens=4, total_tokens=16
    )
    assert detector.experiment_metadata()["usage_responses"] == 1


def baseline_result():
    return json.loads(
        evaluate(
            "trip_detection",
            plugin.load(plugin.default_dataset()),
            plugin.system(),
            plugin.scorer(),
        ).to_json()
    )


@pytest.mark.parametrize("field", ["dataset", "scorer", "case_count", "labels"])
def test_comparison_rejects_drift(field):
    baseline, candidate = baseline_result(), baseline_result()
    if field == "dataset":
        candidate["dataset"]["sha256"] = "different"
    elif field == "scorer":
        candidate["metadata"]["scorer"]["iou_threshold"] = 0.6
    elif field == "labels":
        candidate["cases"][0]["expected"] = [interval()]
    else:
        candidate["case_count"] = 15
    with pytest.raises(ValueError, match="identical"):
        compare_results(baseline, candidate)


def test_comparison_deltas_and_undefined_boundaries():
    baseline, candidate = baseline_result(), baseline_result()
    candidate["aggregate_metrics"]["f1"] = 0.9
    candidate["aggregate_metrics"]["mean_absolute_boundary_error_days"] = 0.1
    comparison = compare_results(baseline, candidate)
    assert comparison["deltas"]["f1"] == pytest.approx(0.9 - 16 / 19)
    assert comparison["deltas"]["mean_absolute_boundary_error_days"] == pytest.approx(
        -0.025
    )
    assert len(comparison["incorrect_case_differences"]) == 4
    assert "Mean boundary error" in format_comparison(comparison)
    candidate["aggregate_metrics"]["mean_absolute_boundary_error_days"] = None
    assert (
        compare_results(baseline, candidate)["deltas"][
            "mean_absolute_boundary_error_days"
        ]
        is None
    )


def test_chatgroq_configuration_without_network(monkeypatch):
    monkeypatch.setenv("GROQ_API_KEY", "fake-contract-secret")
    captured = {}

    def factory(**kwargs):
        captured.update(kwargs)
        return FakeModel([response()])

    monkeypatch.setattr(
        "lifeos_ai_evals.evaluators.trip_detection.groq_detector.ChatGroq", factory
    )
    detector = GroqTripDetector()
    assert captured["timeout"] == 45 and captured["max_retries"] == 0
    assert captured["model_kwargs"] == {"include_reasoning": False}
    assert "fake-contract-secret" not in json.dumps(detector.configuration)


@pytest.mark.parametrize("timeout", [False, True])
def test_real_langchain_wiring_with_fake_http(monkeypatch, timeout):
    import httpx
    from langchain_groq import ChatGroq

    monkeypatch.setenv("GROQ_API_KEY", "fake-http-contract-secret")
    calls = []

    def transport(request):
        payload = json.loads(request.content)
        calls.append(payload)
        assert payload["model"] == "openai/gpt-oss-20b"
        assert payload["temperature"] == 0
        assert payload["include_reasoning"] is False
        assert payload["response_format"]["type"] == "json_schema"
        assert payload["response_format"]["json_schema"]["strict"] is True
        if timeout:
            raise httpx.ReadTimeout("fake-http-contract-secret", request=request)
        return httpx.Response(
            200,
            json={
                "id": "fake-id",
                "object": "chat.completion",
                "created": 0,
                "model": "openai/gpt-oss-20b",
                "choices": [
                    {
                        "index": 0,
                        "message": {
                            "role": "assistant",
                            "content": json.dumps({"trips": [], "explanations": []}),
                        },
                        "finish_reason": "stop",
                    }
                ],
                "usage": {
                    "prompt_tokens": 10,
                    "completion_tokens": 5,
                    "total_tokens": 15,
                },
            },
        )

    real_constructor = ChatGroq

    def factory(**kwargs):
        return real_constructor(
            **kwargs, http_client=httpx.Client(transport=httpx.MockTransport(transport))
        )

    monkeypatch.setattr(
        "lifeos_ai_evals.evaluators.trip_detection.groq_detector.ChatGroq", factory
    )
    detector = GroqTripDetector(sleep=lambda _: None)
    from dataclasses import replace

    dataset = plugin.load(plugin.default_dataset())
    dataset = replace(dataset, cases=dataset.cases[:1])
    result = evaluate("trip_detection", dataset, detector, plugin.scorer())
    assert len(calls) == 1
    assert result.errors == int(timeout), result.cases[0].error
    assert "fake-http-contract-secret" not in result.to_json()
    if not timeout:
        assert detector.experiment_metadata()["usage"]["total_tokens"] == 15


def test_compare_invalid_export_is_configuration_error(tmp_path, capsys):
    path = tmp_path / "invalid.json"
    path.write_text("{}")
    assert main(["compare", str(path), str(path)]) == 2
    assert "invalid evaluation result JSON" in capsys.readouterr().err
