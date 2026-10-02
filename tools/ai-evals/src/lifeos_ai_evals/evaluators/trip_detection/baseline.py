from datetime import date

from lifeos_ai_evals.core.engine import Prediction
from lifeos_ai_evals.evaluators.trip_detection.model import Event, Trip


class TripDetector:
    name = "travel-cluster-baseline"
    version = "1.0.0"
    configuration = {
        "max_gap_days": 2,
        "min_evidence_days": 2,
        "categories": ["accommodation", "transport", "travel"],
    }

    def predict(self, value: tuple[Event, ...]) -> Prediction[tuple[Trip, ...]]:
        evidence = sorted(
            (
                e
                for e in value
                if e.transaction_type == "expense"
                and e.category in self.configuration["categories"]
            ),
            key=lambda e: e.date,
        )
        clusters: list[list[Event]] = []
        for event in evidence:
            if (
                not clusters
                or (
                    date.fromisoformat(event.date)
                    - date.fromisoformat(clusters[-1][-1].date)
                ).days
                > self.configuration["max_gap_days"]
            ):
                clusters.append([])
            clusters[-1].append(event)
        trips, explanations = [], []
        for cluster in clusters:
            categories = {e.category for e in cluster}
            days = {e.date for e in cluster}
            if (
                "accommodation" in categories
                and categories & {"transport", "travel"}
                and len(days) >= self.configuration["min_evidence_days"]
            ):
                trip = Trip(cluster[0].date, cluster[-1].date)
                trips.append(trip)
                span = (
                    date.fromisoformat(trip.end_date)
                    - date.fromisoformat(trip.start_date)
                ).days + 1
                explanations.append(
                    f"Detected {span}-day cluster with accommodation + "
                    f"transport/travel on {len(days)} evidence days."
                )
        return Prediction(tuple(trips), tuple(explanations))
