import json
from dataclasses import asdict, replace
from pathlib import Path

import pytest
from test_groq_detector import FakeModel, RateLimitError, baseline_result, response

from lifeos_ai_evals.__main__ import main
from lifeos_ai_evals.core.comparison import compare_runs
from lifeos_ai_evals.core.engine import evaluate
from lifeos_ai_evals.core.experiments import ExperimentVariant
from lifeos_ai_evals.evaluators.trip_detection import plugin
from lifeos_ai_evals.evaluators.trip_detection.variants import VariantTripDetector

ROOT = Path(__file__).resolve().parents[1]


def variant():
    return ExperimentVariant.load(ROOT / "experiments/trip_detection/candidate-v2.json")


def test_v2_composition_and_repeatability():
    dataset = plugin.load(ROOT / "datasets/trip_detection/v2.json")
    assert len(dataset.cases) == 60
    assert sum(bool(case.expected) for case in dataset.cases) == 30
    assert all(case.description and case.tags for case in dataset.cases)
    tags = {tag for case in dataset.cases for tag in case.tags}
    assert {
        "weekend",
        "long-leisure",
        "business",
        "mixed-business-leisure",
        "multiple-trips",
        "close-trips",
        "cross-month",
        "sparse",
        "accommodation-only",
        "private-car",
        "transport-only",
        "foreign-currency",
        "foreign-currency-nontravel",
        "advance-booking",
        "expensive-purchase",
        "restaurant-heavy",
        "local-ambiguity",
        "uncertain-boundaries",
        "no-spend",
        "multiple-currencies",
        "interleaved-ordinary",
        "false-positive-trap",
    } <= tags
    first = evaluate("trip_detection", dataset, plugin.system(), plugin.scorer())
    assert first == evaluate(
        "trip_detection", dataset, plugin.system(), plugin.scorer()
    )
    assert first.metadata["tag_metrics"]["positive"]["cases"] == 30
    assert first.metadata["tag_metrics"]["negative"]["cases"] == 30
    assert first.errors == 0


def test_frozen_controls():
    import hashlib

    frozen = [
        (
            "datasets/trip_detection/v1.json",
            "00388b535322fcf2ca72b443af8ef23f03323afaf24696075361d284caeed164",
        ),
        (
            "src/lifeos_ai_evals/evaluators/trip_detection/baseline.py",
            "d34f61c4cb4cbb11c64e47ea79b4e0d86593793b34735be228b9f3e1421d1425",
        ),
        (
            "src/lifeos_ai_evals/evaluators/trip_detection/groq_detector.py",
            "89c32132406a1d51b14f2d2cd550e27d4d383a560658fb0babc74bec7b4d6511",
        ),
        (
            "src/lifeos_ai_evals/evaluators/trip_detection/scoring.py",
            "92b07606ebba524031c058e60a77a60c3d3751c4604ef53a1b10aaedc2d7f54e",
        ),
        (
            "datasets/trip_detection/v2.json",
            "4644e129ad5dd5c939e2003ac59f812f6550f52a254b6600dda5ad04754964c1",
        ),
    ]
    for relative, digest in frozen:
        actual = (ROOT / relative).read_bytes().replace(b"\r\n", b"\n")
        assert hashlib.sha256(actual).hexdigest() == digest
    metrics = baseline_result()["aggregate_metrics"]
    assert [
        metrics[key] for key in ("true_positives", "false_positives", "false_negatives")
    ] == [8, 1, 2]
    assert metrics["f1"] == pytest.approx(16 / 19)
    assert metrics["mean_absolute_boundary_error_days"] == 0.125


def test_variant_payload_and_telemetry():
    model = FakeModel(
        [RateLimitError("0"), response(usage={"input_tokens": 12}), response()]
    )
    times = iter([0, 1, 2, 5, 6, 8])
    detector = VariantTripDetector(
        variant(), model=model, sleep=lambda _: None, clock=lambda: next(times)
    )
    dataset = plugin.load(plugin.default_dataset())
    for case in dataset.cases[:2]:
        detector.predict(case.input)
    telemetry = detector.experiment_metadata()["telemetry"]
    assert telemetry["request_count"] == 3
    assert telemetry["latency_seconds"]["mean"] == 2
    assert telemetry["token_usage"]["input_tokens"] == {"responses": 1, "total": 12}
    assert telemetry["token_usage"]["output_tokens"] == {"responses": 0, "total": None}
    assert telemetry["cost"] is None
    for prompt in model.prompts:
        payload = prompt.to_messages()[1].content
        assert "expected" not in payload and "tags" not in payload
        assert all(
            case.id not in payload and case.description not in payload
            for case in dataset.cases[:2]
        )
    assert detector.identity == asdict(variant())


def test_model_variant_wiring(monkeypatch):
    captured = {}
    monkeypatch.setenv("GROQ_API_KEY", "fake-secret")

    def factory(**kwargs):
        captured.update(kwargs)
        return FakeModel([response()])

    monkeypatch.setattr(
        "lifeos_ai_evals.evaluators.trip_detection.variants.ChatGroq", factory
    )
    detector = VariantTripDetector(replace(variant(), model="another-configured-model"))
    assert captured["model"] == "another-configured-model"
    assert "fake-secret" not in json.dumps(detector.identity)


@pytest.mark.parametrize(
    "field,value",
    [
        ("provider", "unavailable"),
        ("prompt_version", "unknown"),
        ("context_version", "unknown"),
        ("structured_output", "unknown"),
    ],
)
def test_unsupported_variant_fails_before_calls(field, value):
    with pytest.raises(ValueError):
        VariantTripDetector(replace(variant(), **{field: value}), model=FakeModel([]))


def test_multi_run_comparison_cli(tmp_path):
    paths = []
    for index in range(3):
        path = tmp_path / f"run{index}.json"
        path.write_text(json.dumps(baseline_result()))
        paths.append(str(path))
    output = tmp_path / "comparison.json"
    assert main(["compare", *paths, "--output", str(output)]) == 0
    comparison = json.loads(output.read_text())
    assert len(comparison["runs"]) == 3
    assert len(comparison["comparisons"]) == 2
    assert comparison["comparisons"][1]["deltas"]["precision"] == 0
    assert comparison["comparisons"][1]["tag_deltas"]
    assert comparison["comparisons"][1]["execution_error_differences"] == []


@pytest.mark.parametrize(
    "mutation",
    [
        lambda run: run["dataset"].update(name="other"),
        lambda run: run["dataset"].update(version="other"),
        lambda run: run["dataset"].update(sha256="other"),
        lambda run: run["cases"][0].update(id="other"),
        lambda run: run["metadata"]["scorer"].update(iou_threshold=0.9),
        lambda run: run["metadata"]["scorer"].update(matching="other"),
    ],
)
def test_multi_run_rejects_incompatible_third_run(mutation):
    runs = [baseline_result() for _ in range(3)]
    mutation(runs[2])
    with pytest.raises(ValueError):
        compare_runs(runs)


def test_tag_metrics_exclude_execution_errors():
    dataset = plugin.load(plugin.default_dataset())
    dataset = replace(dataset, cases=dataset.cases[:1])

    class Broken:
        name = "broken"
        version = "1"
        configuration = {}

        def predict(self, value):
            raise TimeoutError("secret")

    result = evaluate("trip_detection", dataset, Broken(), plugin.scorer())
    for tag in dataset.cases[0].tags:
        group = result.metadata["tag_metrics"][tag]
        assert group["errors"] == 1 and group["scored_cases"] == 0
        assert group["metrics"]["f1"] is None


def test_variant_cli_offline(monkeypatch, tmp_path):
    detector = VariantTripDetector(
        variant(), model=FakeModel([response()] * 16), sleep=lambda _: None
    )
    monkeypatch.setattr(plugin, "experiment", lambda path: detector)
    output = tmp_path / "variant.json"
    assert (
        main(
            [
                "evaluate",
                "trip_detection",
                "--variant",
                "fake.json",
                "--output",
                str(output),
            ]
        )
        == 0
    )
    run = json.loads(output.read_text())
    assert run["metadata"]["experiment"]["prompt_version"] == "trip-detection-v2"
    assert run["metadata"]["telemetry"]["request_count"] == 16


def test_provider_neutral_registration(tmp_path):
    from lifeos_ai_evals.core.experiments import create_experiment

    data = asdict(variant())
    data.update(
        provider="offline-example", model="fake", generation_settings={"seed": 7}
    )
    path = tmp_path / "variant.json"
    path.write_text(json.dumps(data))
    configured = create_experiment(path, {"offline-example": lambda value: value})
    assert configured.provider == "offline-example"
    assert configured.generation_settings == {"seed": 7}
    with pytest.raises(ValueError, match="adapter unavailable"):
        create_experiment(path, {})


@pytest.mark.parametrize(
    "mutation",
    [
        lambda data: data.update(model=""),
        lambda data: data.update(extra="unexpected"),
        lambda data: data.update(generation_settings=[]),
        lambda data: data["generation_settings"].update(temperature=float("nan")),
    ],
)
def test_invalid_variant_configuration(tmp_path, mutation):
    data = asdict(variant())
    mutation(data)
    path = tmp_path / "invalid.json"
    path.write_text(json.dumps(data))
    with pytest.raises(ValueError):
        ExperimentVariant.load(path)


def test_execution_error_difference_and_null_tag_deltas():
    reference = baseline_result()
    candidate = baseline_result()
    candidate["cases"][0].update(
        status="error", error={"stage": "prediction", "type": "TimeoutError"}
    )
    candidate["errors"] = 1
    candidate["metadata"]["scored_cases"] -= 1
    result = compare_runs([reference, candidate, reference])
    pair = result["comparisons"][0]
    assert pair["coverage_deltas"]["execution_errors"] == 1
    assert pair["execution_error_differences"][0]["id"] == "normal-month"
    assert pair["execution_error_differences"][0]["candidate"]["type"] == "TimeoutError"
