import json
import subprocess
import sys
from pathlib import Path

import pytest

from lifeos_ai_evals.core.engine import Prediction, Score, evaluate, load_dataset


class Double:
    name = "double"
    version = "1"
    configuration = {"factor": 2}

    def predict(self, value):
        if value < 0:
            raise RuntimeError("secret must not be exported")
        return Prediction(value * 2, ("doubled",))


class Equality:
    configuration = {"comparison": "equality"}

    def score(self, expected, predicted):
        return Score(expected == predicted, {"exact": int(expected == predicted)})

    def aggregate(self, scores):
        return {"exact": sum(s.metrics["exact"] for s in scores)}


def number(value):
    if type(value) is not int:
        raise ValueError("requires integer")
    return value


def data():
    return {
        "name": "numbers",
        "version": "1",
        "cases": [
            {"id": "a", "input": 2, "expected": 4},
            {
                "id": "b",
                "input": 3,
                "expected": 7,
                "tags": ["negative"],
                "description": "incorrect prediction",
            },
        ],
    }


def load(tmp_path, value):
    path = tmp_path / "cases.json"
    path.write_text(json.dumps(value), encoding="utf-8")
    return load_dataset(path, number, number)


def test_loading_stable_ids_and_metadata(tmp_path):
    dataset = load(tmp_path, data())
    assert dataset.name == "numbers"
    assert dataset.version == "1"
    assert [c.id for c in dataset.cases] == ["a", "b"]
    assert dataset.cases[1].tags == ("negative",)
    assert len(dataset.sha256) == 64
    assert dataset == load(tmp_path, data())


@pytest.mark.parametrize(
    "mutation",
    [
        lambda d: d.update(name=""),
        lambda d: d.update(version=2),
        lambda d: d.update(cases=[]),
        lambda d: d.update(cases={}),
        lambda d: d["cases"][0].update(id=""),
        lambda d: d["cases"][1].update(id="a"),
        lambda d: d["cases"][0].pop("input"),
        lambda d: d["cases"][0].pop("expected"),
        lambda d: d["cases"][0].update(tags="easy"),
        lambda d: d["cases"][0].update(tags=[""]),
        lambda d: d["cases"][0].update(description=3),
        lambda d: d["cases"].append(None),
        lambda d: d["cases"][0].update(input=True),
    ],
)
def test_malformed_dataset(tmp_path, mutation):
    value = data()
    mutation(value)
    with pytest.raises(ValueError):
        load(tmp_path, value)


@pytest.mark.parametrize(
    "raw", ["{", "[]", '{"name":"a","name":"b"}', '{"name": NaN}', '{"name": Infinity}']
)
def test_malformed_json(tmp_path, raw):
    path = tmp_path / "bad.json"
    path.write_text(raw, encoding="utf-8")
    with pytest.raises(ValueError):
        load_dataset(path, number, number)


def test_generic_repeatability_and_serialization(tmp_path):
    dataset = load(tmp_path, data())
    first = evaluate("arithmetic", dataset, Double(), Equality())
    assert first == evaluate("arithmetic", dataset, Double(), Equality())
    assert first.aggregate_metrics == {"exact": 1}
    assert [r.status for r in first.cases] == ["correct", "incorrect"]
    assert first.cases[0].prediction.value == 4
    assert first.failures == 1 and first.errors == 0
    output = json.loads(first.to_json())
    assert output["system"]["version"] == "1"
    assert output["dataset"]["sha256"] == dataset.sha256
    assert output["cases"][0]["prediction"]["explanations"] == ["doubled"]
    assert output["case_count"] == 2
    assert output["metadata"]["scored_cases"] == 2
    assert str(tmp_path) not in first.to_json()


def test_predictor_error_continues_without_leaking_message(tmp_path):
    value = data()
    value["cases"][0]["input"] = -1
    result = evaluate("arithmetic", load(tmp_path, value), Double(), Equality())
    assert result.errors == 1 and result.failures == 1
    assert result.metadata["scored_cases"] == 1
    assert result.cases[0].error == {"stage": "prediction", "type": "RuntimeError"}
    assert result.cases[1].score is not None
    assert "secret" not in result.to_json()


def test_scoring_error_continues(tmp_path):
    class BrokenScorer(Equality):
        def score(self, expected, predicted):
            if expected == 4:
                raise ValueError("scorer failed")
            return super().score(expected, predicted)

    result = evaluate("arithmetic", load(tmp_path, data()), Double(), BrokenScorer())
    assert result.cases[0].prediction.value == 4
    assert result.cases[0].error["stage"] == "scoring"
    assert result.errors == 1
    assert result.cases[1].score is not None


def test_cli_success_and_json(tmp_path):
    output = tmp_path / "nested/result.json"
    result = subprocess.run(
        [
            sys.executable,
            "-m",
            "lifeos_ai_evals",
            "evaluate",
            "trip_detection",
            "--output",
            str(output),
            "--verbose",
        ],
        capture_output=True,
        text=True,
        check=False,
    )
    assert result.returncode == 0, result.stderr
    assert "precision:" in result.stdout
    assert "Detected" in result.stdout
    value = json.loads(output.read_text(encoding="utf-8"))
    assert value["case_count"] == 16
    assert value["errors"] == 0
    assert value["aggregate_metrics"]["true_positives"] == 8


@pytest.mark.parametrize(
    "args",
    [["missing"], ["../invalid"], ["trip_detection", "--dataset", "nonexistent.json"]],
)
def test_cli_configuration_error(args):
    result = subprocess.run(
        [sys.executable, "-m", "lifeos_ai_evals", "evaluate", *args],
        capture_output=True,
        text=True,
        check=False,
    )
    assert result.returncode == 2
    assert "configuration error" in result.stderr
    assert "Traceback" not in result.stderr


def test_cli_new_evaluator_without_engine_changes(tmp_path, monkeypatch, capsys):
    # A second non-trip plugin proves that neither engine nor CLI require edits.
    from types import SimpleNamespace

    from lifeos_ai_evals.__main__ import main

    path = tmp_path / "numbers.json"
    path.write_text(json.dumps(data()), encoding="utf-8")
    plugin = SimpleNamespace(
        default_dataset=lambda: path,
        load=lambda p: load_dataset(p, number, number),
        system=Double,
        scorer=Equality,
    )
    monkeypatch.setitem(
        sys.modules, "lifeos_ai_evals.evaluators.arithmetic.plugin", plugin
    )
    assert main(["evaluate", "arithmetic"]) == 0
    assert "exact: 1" in capsys.readouterr().out


def test_cli_error_exit(tmp_path, monkeypatch):
    from types import SimpleNamespace

    from lifeos_ai_evals.__main__ import main

    value = data()
    value["cases"][0]["input"] = -1
    dataset = load(tmp_path, value)
    plugin = SimpleNamespace(
        default_dataset=lambda: Path("unused"),
        load=lambda p: dataset,
        system=Double,
        scorer=Equality,
    )
    monkeypatch.setitem(sys.modules, "lifeos_ai_evals.evaluators.broken.plugin", plugin)
    assert main(["evaluate", "broken"]) == 1
