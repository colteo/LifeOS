import copy
import hashlib
import json

import pytest
from test_core import data, number

from lifeos_ai_evals.core.comparison import compare_runs
from lifeos_ai_evals.core.engine import evaluate, load_dataset
from lifeos_ai_evals.evaluators.trip_detection import plugin


@pytest.mark.parametrize("version", ["v1", "v2"])
def test_checkout_line_endings_have_identical_comparison_identity(tmp_path, version):
    source = plugin.default_dataset().with_name(f"{version}.json")
    lf = source.read_bytes().replace(b"\r\n", b"\n")
    crlf = lf.replace(b"\n", b"\r\n")
    assert hashlib.sha256(lf).hexdigest() != hashlib.sha256(crlf).hexdigest()
    runs = []
    datasets = []
    for index, raw in enumerate((lf, crlf)):
        path = tmp_path / f"dataset{index}.json"
        path.write_bytes(raw)
        dataset = plugin.load(path)
        datasets.append(dataset)
        runs.append(
            json.loads(
                evaluate(
                    "trip_detection", dataset, plugin.system(), plugin.scorer()
                ).to_json()
            )
        )
    assert datasets[0] == datasets[1]
    assert datasets[0].sha256 == hashlib.sha256(lf).hexdigest()
    assert [case.id for case in datasets[0].cases] == [
        case["id"] for case in json.loads(lf)["cases"]
    ]
    assert runs[0] == runs[1]
    assert compare_runs(runs)["comparisons"][0]["deltas"]["f1"] == 0
    # A legacy export lacks the hash strategy metadata and uses raw file bytes.
    for raw in (lf, crlf):
        legacy = copy.deepcopy(runs[0])
        legacy["metadata"].pop("dataset_hash")
        legacy["dataset"]["sha256"] = hashlib.sha256(raw).hexdigest()
        assert compare_runs([legacy, runs[1]])["comparisons"][0]["deltas"]["f1"] == 0
        assert compare_runs([runs[1], legacy])["comparisons"][0]["deltas"]["f1"] == 0


@pytest.mark.parametrize(
    "mutation",
    [
        lambda value: value["cases"].reverse(),
        lambda value: value["cases"][0].update(input=3),
        lambda value: value["cases"][0].update(expected=5),
        lambda value: value["cases"][0].update(description="changed"),
        lambda value: value["cases"][0].update(tags=["changed"]),
        lambda value: value.update(
            extra={"note": "semantic content beyond parsed fields"}
        ),
    ],
)
def test_normalization_preserves_all_content_and_case_order(tmp_path, mutation):
    value = data()
    first = tmp_path / "first.json"
    first.write_bytes(json.dumps(value, indent=2).encode())
    mutation(value)
    second = tmp_path / "second.json"
    second.write_bytes(json.dumps(value, indent=2).encode())
    assert (
        load_dataset(first, number, number).sha256
        != load_dataset(second, number, number).sha256
    )


@pytest.mark.parametrize(
    "raw",
    [
        '{\n"name":"a", "name":"b"\n}',
        '{\n"name":"a", "cases":[{"input":{"a":1,"a":2}}]\n}',
        '{\n"name": NaN\n}',
        '{\n"name": Infinity\n}',
        '{\n"name": "literal\nnewline"\n}',
        '{\n"name":\n}',
    ],
)
@pytest.mark.parametrize("newline", [b"\n", b"\r\n"])
def test_invalid_original_json_is_still_rejected(tmp_path, raw, newline):
    path = tmp_path / "invalid.json"
    path.write_bytes(raw.encode().replace(b"\n", newline))
    with pytest.raises(ValueError, match="Invalid dataset JSON"):
        load_dataset(path, number, number)


def test_legacy_compatibility_does_not_accept_unrelated_hashes():
    run = json.loads(
        evaluate(
            "trip_detection",
            plugin.load(plugin.default_dataset()),
            plugin.system(),
            plugin.scorer(),
        ).to_json()
    )
    legacy = copy.deepcopy(run)
    legacy["metadata"].pop("dataset_hash")
    legacy["dataset"]["sha256"] = "unrelated"
    with pytest.raises(ValueError, match="identical dataset"):
        compare_runs([run, legacy])
    # Alias matching never overrides a mismatch between two normalized runs.
    changed = copy.deepcopy(run)
    changed["dataset"]["sha256"] = "changed"
    with pytest.raises(ValueError, match="identical dataset"):
        compare_runs([run, changed])
    # Old-old comparisons keep their exact raw-hash behavior.
    other_legacy = copy.deepcopy(legacy)
    other_legacy["dataset"]["sha256"] = "different"
    with pytest.raises(ValueError, match="identical dataset"):
        compare_runs([legacy, other_legacy])
