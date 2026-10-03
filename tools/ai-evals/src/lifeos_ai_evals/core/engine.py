from __future__ import annotations

import hashlib
import json
import platform
from collections.abc import Callable
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Protocol

Metrics = dict[str, int | float | None]


@dataclass(frozen=True)
class Case[Input, Output]:
    id: str
    input: Input
    expected: Output
    tags: tuple[str, ...] = ()
    description: str = ""


@dataclass(frozen=True)
class Dataset[Input, Output]:
    name: str
    version: str
    cases: tuple[Case[Input, Output], ...]
    sha256: str


@dataclass(frozen=True)
class Prediction[Output]:
    value: Output
    explanations: tuple[str, ...] = ()


@dataclass(frozen=True)
class Score:
    correct: bool
    metrics: Metrics
    details: dict[str, Any] = field(default_factory=dict)


class Predictor[Input, Output](Protocol):
    name: str
    version: str
    configuration: dict[str, Any]

    def predict(self, value: Input) -> Prediction[Output]: ...


class Scorer[Output](Protocol):
    configuration: dict[str, Any]

    def score(self, expected: Output, predicted: Output) -> Score: ...

    def aggregate(self, scores: list[Score]) -> Metrics: ...


@dataclass(frozen=True)
class CaseResult[Output]:
    id: str
    status: str
    expected: Output
    prediction: Prediction[Output] | None
    score: Score | None
    error: dict[str, str] | None
    tags: tuple[str, ...]
    description: str


@dataclass(frozen=True)
class EvaluationResult[Output]:
    evaluator: str
    system: dict[str, Any]
    dataset: dict[str, str]
    case_count: int
    aggregate_metrics: Metrics
    cases: tuple[CaseResult[Output], ...]
    failures: int
    errors: int
    metadata: dict[str, Any]

    def to_json(self) -> str:
        return json.dumps(asdict(self), indent=2, allow_nan=False)


def nonempty_string(value: Any, label: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"{label} must be a nonempty string")
    return value


def load_dataset[Input, Output](
    path: Path,
    parse_input: Callable[[Any], Input],
    parse_expected: Callable[[Any], Output],
) -> Dataset[Input, Output]:
    raw = path.read_bytes()

    def unique_object(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f"duplicate JSON key: {key}")
            result[key] = value
        return result

    def reject_constant(value):
        raise ValueError(f"invalid JSON constant: {value}")

    try:
        data = json.loads(
            raw, object_pairs_hook=unique_object, parse_constant=reject_constant
        )
    except (ValueError, UnicodeError) as exc:
        raise ValueError(f"Invalid dataset JSON: {exc}") from exc
    if not isinstance(data, dict):
        raise ValueError("dataset must be an object")
    name = nonempty_string(data.get("name"), "dataset name")
    version = nonempty_string(data.get("version"), "dataset version")
    if not isinstance(data.get("cases"), list) or not data["cases"]:
        raise ValueError("cases must be a nonempty list")
    cases = []
    ids = set()
    for index, item in enumerate(data["cases"]):
        try:
            if not isinstance(item, dict):
                raise ValueError("case must be an object")
            case_id = nonempty_string(item.get("id"), "case id")
            if case_id in ids:
                raise ValueError(f"duplicate case id: {case_id}")
            ids.add(case_id)
            tags = item.get("tags", [])
            if not isinstance(tags, list) or any(
                not isinstance(tag, str) or not tag.strip() for tag in tags
            ):
                raise ValueError("tags must be a list of nonempty strings")
            description = item.get("description", "")
            if not isinstance(description, str):
                raise ValueError("description must be a string")
            cases.append(
                Case(
                    case_id,
                    parse_input(item["input"]),
                    parse_expected(item["expected"]),
                    tuple(tags),
                    description,
                )
            )
        except (ValueError, TypeError, KeyError) as exc:
            raise ValueError(f"Invalid case at index {index}: {exc}") from exc
    return Dataset(name, version, tuple(cases), hashlib.sha256(raw).hexdigest())


def evaluate[Input, Output](
    evaluator: str,
    dataset: Dataset[Input, Output],
    system: Predictor[Input, Output],
    scorer: Scorer[Output],
) -> EvaluationResult[Output]:
    results = []
    scores = []
    for case in dataset.cases:
        prediction = None
        stage = "prediction"
        try:
            prediction = system.predict(case.input)
            stage = "scoring"
            score = scorer.score(case.expected, prediction.value)
            scores.append(score)
            result = CaseResult(
                case.id,
                "correct" if score.correct else "incorrect",
                case.expected,
                prediction,
                score,
                None,
                case.tags,
                case.description,
            )
        except Exception as exc:
            # Never serialize exception messages: future adapters may include secrets.
            result = CaseResult(
                case.id,
                "error",
                case.expected,
                prediction,
                None,
                {"stage": stage, "type": type(exc).__name__},
                case.tags,
                case.description,
            )
        results.append(result)
    return EvaluationResult(
        evaluator,
        {
            "name": system.name,
            "version": system.version,
            "configuration": system.configuration,
        },
        {"name": dataset.name, "version": dataset.version, "sha256": dataset.sha256},
        len(results),
        scorer.aggregate(scores),
        tuple(results),
        sum(r.status == "incorrect" for r in results),
        sum(r.status == "error" for r in results),
        {
            "schema_version": "1",
            "harness_version": "0.1.0",
            "python": platform.python_version(),
            "scorer": scorer.configuration,
            "scored_cases": len(scores),
            "experiment": getattr(
                system,
                "identity",
                {
                    "provider": system.configuration.get("provider", "deterministic"),
                    "model": system.configuration.get("model"),
                    "prompt_version": system.configuration.get("prompt_version"),
                    "context_version": system.configuration.get("context_version"),
                    "structured_output": system.configuration.get("structured_output"),
                    "generation_settings": {
                        key: system.configuration[key]
                        for key in (
                            "temperature",
                            "max_tokens",
                            "reasoning_effort",
                            "include_reasoning",
                        )
                        if key in system.configuration
                    },
                },
            ),
            "tag_metrics": {
                tag: {
                    "cases": sum(tag in r.tags for r in results),
                    "scored_cases": sum(
                        tag in r.tags and r.score is not None for r in results
                    ),
                    "errors": sum(
                        tag in r.tags and r.status == "error" for r in results
                    ),
                    "metrics": scorer.aggregate(
                        [
                            r.score
                            for r in results
                            if tag in r.tags and r.score is not None
                        ]
                    ),
                }
                for tag in sorted({tag for r in results for tag in r.tags})
            },
            "error_policy": "continue; exclude from metrics",
        },
    )
