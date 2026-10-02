from datetime import date

from lifeos_ai_evals.core.engine import Metrics, Score
from lifeos_ai_evals.evaluators.trip_detection.model import Trip


def iou(a: Trip, b: Trip) -> float:
    a_start, a_end = date.fromisoformat(a.start_date), date.fromisoformat(a.end_date)
    b_start, b_end = date.fromisoformat(b.start_date), date.fromisoformat(b.end_date)
    intersection = max(0, (min(a_end, b_end) - max(a_start, b_start)).days + 1)
    union = (a_end - a_start).days + (b_end - b_start).days + 2 - intersection
    return intersection / union


def detection_metrics(tp: int, fp: int, fn: int) -> Metrics:
    precision = tp / (tp + fp) if tp + fp else (1.0 if fn == 0 else 0.0)
    recall = tp / (tp + fn) if tp + fn else (1.0 if fp == 0 else 0.0)
    return {
        "true_positives": tp,
        "false_positives": fp,
        "false_negatives": fn,
        "precision": precision,
        "recall": recall,
        "f1": 2 * tp / (2 * tp + fp + fn) if 2 * tp + fp + fn else 1.0,
    }


class TripScorer:
    def __init__(self, threshold: float = 0.5):
        if not 0 < threshold <= 1:
            raise ValueError("IoU threshold must be in (0, 1]")
        self.configuration = {
            "iou_threshold": threshold,
            "matching": "maximum-cardinality; stable index ties",
        }

    def score(self, expected: tuple[Trip, ...], predicted: tuple[Trip, ...]) -> Score:
        # Augmenting-path bipartite matching maximizes TP, unlike greedy IoU.
        candidates = {
            p: sorted(
                (
                    e
                    for e in range(len(expected))
                    if iou(predicted[p], expected[e])
                    >= self.configuration["iou_threshold"]
                ),
                key=lambda e: (-iou(predicted[p], expected[e]), e),
            )
            for p in range(len(predicted))
        }
        owners: dict[int, int] = {}

        def assign(p: int, visited: set[int]) -> bool:
            for e in candidates[p]:
                if e in visited:
                    continue
                visited.add(e)
                if e not in owners or assign(owners[e], visited):
                    owners[e] = p
                    return True
            return False

        for p in range(len(predicted)):
            assign(p, set())
        matches = []
        for e, p in sorted(owners.items()):
            start = abs(
                (
                    date.fromisoformat(predicted[p].start_date)
                    - date.fromisoformat(expected[e].start_date)
                ).days
            )
            end = abs(
                (
                    date.fromisoformat(predicted[p].end_date)
                    - date.fromisoformat(expected[e].end_date)
                ).days
            )
            matches.append(
                {
                    "expected_index": e,
                    "predicted_index": p,
                    "iou": iou(expected[e], predicted[p]),
                    "start_error_days": start,
                    "end_error_days": end,
                }
            )
        tp = len(matches)
        metrics = detection_metrics(tp, len(predicted) - tp, len(expected) - tp)
        metrics.update(self.boundaries(matches))
        return Score(
            sorted(expected) == sorted(predicted), metrics, {"matches": matches}
        )

    @staticmethod
    def boundaries(matches: list[dict]) -> Metrics:
        count = len(matches)
        start = sum(m["start_error_days"] for m in matches)
        end = sum(m["end_error_days"] for m in matches)
        return {
            "mean_start_error_days": start / count if count else None,
            "mean_end_error_days": end / count if count else None,
            "mean_absolute_boundary_error_days": (start + end) / (2 * count)
            if count
            else None,
        }

    def aggregate(self, scores: list[Score]) -> Metrics:
        counts = [
            sum(int(s.metrics[key]) for s in scores)
            for key in ("true_positives", "false_positives", "false_negatives")
        ]
        metrics = detection_metrics(*counts)
        if not scores:
            metrics.update(precision=None, recall=None, f1=None)
        metrics.update(
            self.boundaries([m for s in scores for m in s.details["matches"]])
        )
        return metrics
