import json
from dataclasses import asdict

import pytest

from lifeos_ai_evals.core.engine import evaluate
from lifeos_ai_evals.evaluators.trip_detection.baseline import TripDetector
from lifeos_ai_evals.evaluators.trip_detection.model import (
    Event,
    Trip,
    parse_expected,
    parse_input,
)
from lifeos_ai_evals.evaluators.trip_detection.plugin import default_dataset, load
from lifeos_ai_evals.evaluators.trip_detection.scoring import (
    TripScorer,
    iou,
)


def t(start=10, end=14):
    return Trip(f"2026-09-{start:02d}", f"2026-09-{end:02d}")


def test_exact_interval():
    score = TripScorer().score((t(),), (t(),))
    assert score.correct
    assert score.metrics["true_positives"] == 1
    assert (
        score.metrics["precision"]
        == score.metrics["recall"]
        == score.metrics["f1"]
        == 1
    )
    assert score.metrics["mean_absolute_boundary_error_days"] == 0
    assert score.details["matches"][0]["iou"] == 1


def test_overlapping_interval_and_boundaries():
    score = TripScorer().score((t(10, 15),), (t(11, 14),))
    assert not score.correct  # Detection success does not mean exact boundaries.
    assert score.metrics["true_positives"] == 1
    assert score.metrics["mean_start_error_days"] == 1
    assert score.metrics["mean_end_error_days"] == 1
    assert score.metrics["mean_absolute_boundary_error_days"] == 1
    assert score.details["matches"][0]["iou"] == pytest.approx(4 / 6)


def test_threshold_is_inclusive():
    assert TripScorer().score((t(10, 13),), (t(10, 11),)).metrics["true_positives"] == 1
    assert TripScorer().score((t(10, 14),), (t(10, 11),)).metrics["true_positives"] == 0


def test_insufficient_overlap_and_disjoint():
    score = TripScorer().score((t(10, 14),), (t(14, 18),))
    assert score.metrics["true_positives"] == 0
    assert score.metrics["false_positives"] == score.metrics["false_negatives"] == 1
    assert iou(t(1, 2), t(5, 6)) == 0
    assert score.metrics["mean_start_error_days"] is None


def test_one_prediction_cannot_match_two_expected():
    score = TripScorer().score((t(1, 2), t(3, 4)), (t(1, 4),))
    assert score.metrics["true_positives"] == 1
    assert score.metrics["false_negatives"] == 1


def test_two_predictions_cannot_match_one_expected():
    score = TripScorer().score((t(),), (t(), t()))
    assert score.metrics["true_positives"] == score.metrics["false_positives"] == 1


def test_maximum_cardinality_avoids_greedy_miss():
    # First prediction prefers first expected but can match either; second only first.
    score = TripScorer(0.25).score((t(1, 4), t(5, 8)), (t(1, 7), t(1, 3)))
    assert score.metrics["true_positives"] == 2
    assert score == TripScorer(0.25).score((t(1, 4), t(5, 8)), (t(1, 7), t(1, 3)))


@pytest.mark.parametrize(
    "expected,predicted,tp,fp,fn,precision,recall,f1",
    [
        ((), (), 0, 0, 0, 1, 1, 1),
        ((), (t(),), 0, 1, 0, 0, 0, 0),
        ((t(),), (), 0, 0, 1, 0, 0, 0),
        ((t(), t(20, 22)), (t(), t(25, 27)), 1, 1, 1, 0.5, 0.5, 0.5),
        ((t(),), (t(), t(20, 22)), 1, 1, 0, 0.5, 1, 2 / 3),
        ((t(), t(20, 22)), (t(),), 1, 0, 1, 1, 0.5, 2 / 3),
    ],
)
def test_detection_metrics(expected, predicted, tp, fp, fn, precision, recall, f1):
    metrics = TripScorer().score(expected, predicted).metrics
    assert metrics["true_positives"] == tp
    assert metrics["false_positives"] == fp
    assert metrics["false_negatives"] == fn
    assert metrics["precision"] == precision
    assert metrics["recall"] == recall
    assert metrics["f1"] == pytest.approx(f1)


def test_multiple_and_cross_month():
    trips = (Trip("2026-09-29", "2026-10-03"), Trip("2026-10-20", "2026-10-23"))
    assert TripScorer().score(trips, trips).metrics["true_positives"] == 2
    assert iou(trips[0], Trip("2026-09-30", "2026-10-03")) == 0.8


def test_aggregate_is_micro_and_boundaries_are_match_weighted():
    scorer = TripScorer()
    scores = [
        scorer.score((t(10, 15),), (t(11, 14),)),
        scorer.score((t(20, 22),), (t(20, 22),)),
        scorer.score((t(1, 3),), ()),
    ]
    metrics = scorer.aggregate(scores)
    assert metrics["precision"] == 1
    assert metrics["recall"] == pytest.approx(2 / 3)
    assert metrics["f1"] == 0.8
    assert metrics["mean_start_error_days"] == 0.5
    assert metrics["mean_end_error_days"] == 0.5
    assert metrics["mean_absolute_boundary_error_days"] == 0.5


@pytest.mark.parametrize("threshold", [0, -0.1, 1.1, float("nan")])
def test_invalid_threshold(threshold):
    with pytest.raises(ValueError):
        TripScorer(threshold)


def test_baseline_repeatability_and_order_independence():
    dataset = load(default_dataset())
    detector = TripDetector()
    for case in dataset.cases:
        first = detector.predict(case.input)
        assert first == detector.predict(case.input)
        assert first == detector.predict(tuple(reversed(case.input)))
        assert len(first.explanations) == len(first.value)
    assert detector.predict(dataset.cases[0].input).value == ()
    assert detector.predict(dataset.cases[3].input).value == dataset.cases[3].expected
    assert detector.predict(dataset.cases[4].input).value == dataset.cases[4].expected


def test_baseline_does_not_use_names_amounts_currency_or_ground_truth():
    events = (
        Event(
            "2026-09-10", "expense", "1.00", "EUR", "transport", "Synthetic: unknown"
        ),
        Event(
            "2026-09-11", "expense", "2.00", "USD", "accommodation", "Synthetic: other"
        ),
    )
    assert TripDetector().predict(events).value == (t(10, 11),)
    assert (
        TripDetector()
        .predict(
            tuple(
                Event(e.date, "income", e.amount, e.currency, e.category, e.description)
                for e in events
            )
        )
        .value
        == ()
    )
    assert TripDetector().predict((events[0],)).value == ()
    assert TripDetector().predict((events[1],)).value == ()


def test_same_day_evidence_is_not_enough():
    events = tuple(
        Event("2026-09-10", "expense", "10.00", "EUR", c, "Synthetic: event")
        for c in ("transport", "accommodation")
    )
    assert TripDetector().predict(events).value == ()


def test_dataset_evaluation_is_repeatable_and_json_compatible():
    dataset = load(default_dataset())
    result = evaluate("trip_detection", dataset, TripDetector(), TripScorer())
    assert result == evaluate("trip_detection", dataset, TripDetector(), TripScorer())
    assert result.case_count == 16 and result.errors == 0
    assert result.aggregate_metrics["true_positives"] == 8
    assert result.aggregate_metrics["false_positives"] == 1
    assert result.aggregate_metrics["false_negatives"] == 2
    assert result.aggregate_metrics["precision"] == pytest.approx(8 / 9)
    assert result.aggregate_metrics["recall"] == 0.8
    assert result.aggregate_metrics["f1"] == pytest.approx(16 / 19)
    assert result.aggregate_metrics["mean_absolute_boundary_error_days"] == 0.125
    assert (
        json.loads(result.to_json())["cases"][1]["prediction"]["value"][0]["start_date"]
        == "2026-09-12"
    )


def valid_event():
    return asdict(
        Event("2026-09-10", "expense", "10.00", "EUR", "transport", "Synthetic: event")
    )


@pytest.mark.parametrize(
    "updates",
    [
        {"date": "2026-02-30"},
        {"date": "20260910"},
        {"date": 1},
        {"amount": "NaN"},
        {"amount": "Infinity"},
        {"amount": "-1"},
        {"amount": "bad"},
        {"amount": 10},
        {"currency": "eur"},
        {"currency": "123"},
        {"currency": "\u00c9UR"},
        {"transaction_type": "unknown"},
        {"category": ""},
        {"description": None},
    ],
)
def test_invalid_event(updates):
    value = valid_event()
    value.update(updates)
    with pytest.raises(ValueError):
        parse_input({"events": [value]})


@pytest.mark.parametrize(
    "value", [None, [], {"events": {}}, {"events": [{}]}, {"events": [], "extra": 1}]
)
def test_invalid_trip_input_shape(value):
    with pytest.raises(ValueError):
        parse_input(value)


@pytest.mark.parametrize(
    "value",
    [
        None,
        {},
        [{}],
        [{"start_date": "2026-09-15", "end_date": "2026-09-10"}],
        [
            dict(start_date="2026-09-10", end_date="2026-09-13"),
            dict(start_date="2026-09-13", end_date="2026-09-15"),
        ],
    ],
)
def test_invalid_ground_truth(value):
    with pytest.raises(ValueError):
        parse_expected(value)


def test_no_scored_cases_are_not_reported_as_perfect():
    metrics = TripScorer().aggregate([])
    assert metrics["precision"] is None
    assert metrics["recall"] is None
    assert metrics["f1"] is None
    assert metrics["mean_absolute_boundary_error_days"] is None


def test_exact_trip_set_is_order_independent():
    trips = (t(1, 3), t(10, 14))
    assert TripScorer().score(trips, tuple(reversed(trips))).correct


def test_execution_errors_do_not_become_false_negatives():
    class BrokenDetector(TripDetector):
        def predict(self, value):
            raise RuntimeError("do not leak")

    result = evaluate(
        "trip_detection", load(default_dataset()), BrokenDetector(), TripScorer()
    )
    assert result.errors == result.case_count
    assert result.failures == 0
    assert result.metadata["scored_cases"] == 0
    assert result.aggregate_metrics["false_negatives"] == 0
    assert result.aggregate_metrics["f1"] is None


def test_asymmetric_boundary_errors_remain_separate():
    metrics = TripScorer().score((t(10, 15),), (t(10, 14),)).metrics
    assert metrics["mean_start_error_days"] == 0
    assert metrics["mean_end_error_days"] == 1
    assert metrics["mean_absolute_boundary_error_days"] == 0.5
