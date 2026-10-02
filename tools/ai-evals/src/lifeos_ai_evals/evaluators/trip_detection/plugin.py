from pathlib import Path

from lifeos_ai_evals.core.engine import load_dataset
from lifeos_ai_evals.evaluators.trip_detection.baseline import TripDetector
from lifeos_ai_evals.evaluators.trip_detection.model import parse_expected, parse_input
from lifeos_ai_evals.evaluators.trip_detection.scoring import TripScorer


def default_dataset() -> Path:
    # Editable uv installation keeps datasets versioned outside source code.
    return Path(__file__).resolve().parents[4] / "datasets/trip_detection/v1.json"


def load(path: Path):
    return load_dataset(path, parse_input, parse_expected)


def system(name="baseline"):
    if name == "baseline":
        return TripDetector()
    if name == "groq":
        from lifeos_ai_evals.evaluators.trip_detection.groq_detector import (
            GroqTripDetector,
        )

        return GroqTripDetector()
    raise ValueError(f"unknown trip_detection system: {name}")


def scorer():
    return TripScorer()
