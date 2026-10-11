import json
from dataclasses import replace
from pathlib import Path

from lifeos_ai_evals.core.engine import load_dataset
from lifeos_ai_evals.evaluators.journal_answer import acceptance as gates
from lifeos_ai_evals.evaluators.journal_answer import promotion as promotion_gates
from lifeos_ai_evals.evaluators.journal_answer.baseline import ExtractiveBaseline
from lifeos_ai_evals.evaluators.journal_answer.model import (
    parse_expected_with,
    parse_input,
)
from lifeos_ai_evals.evaluators.journal_answer.scoring import JournalAnswerScorer
from lifeos_ai_evals.journal_memory import final_acceptance


def default_dataset() -> Path:
    return Path(__file__).resolve().parents[4] / "datasets/journal_answer/v1.json"


def load(path: Path):
    dataset = load_dataset(path, parse_input, lambda raw: raw)
    cases = []
    for case in dataset.cases:
        if not case.description.startswith("Synthetic"):
            raise ValueError(f"{case.id}: description must start with 'Synthetic'")
        try:
            expected = parse_expected_with(case.input, case.expected)
        except (ValueError, TypeError, KeyError) as exc:
            raise ValueError(f"Invalid case {case.id}: {exc}") from exc
        cases.append(replace(case, expected=expected))
    return replace(dataset, cases=tuple(cases))


def system(name="baseline"):
    if name == "baseline":
        return ExtractiveBaseline()
    raise ValueError(
        f"unknown journal_answer system: {name}; use --variant for the live control"
    )


def scorer():
    return JournalAnswerScorer()


def experiment(path: Path):
    from lifeos_ai_evals.evaluators.journal_answer.live import LiveAnswerPredictor
    from lifeos_ai_evals.evaluators.journal_answer.openai_live import (
        OpenAIAnswerPredictor,
    )

    # AI-006: exactly the frozen OpenAI final candidate.
    if _read(path) == final_acceptance.ANSWER_FINAL:
        return OpenAIAnswerPredictor(final_acceptance.ANSWER_FINAL)
    # The production control, or the AI-005.2 candidate (prompt version only).
    return LiveAnswerPredictor(promotion_gates.load_experiment(path))


def _read(path: Path):
    try:
        return json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise ValueError("invalid journal_answer experiment JSON") from exc


def run_acceptance(run: dict) -> dict:
    # AI-006: the OpenAI final candidate is judged by its own frozen absolute gates.
    if final_acceptance.is_candidate(run, final_acceptance.ANSWER_FINAL):
        return final_acceptance.answer(run)
    return gates.acceptance(run)


def promotion(reference: dict, candidate: dict) -> dict:
    # AI-005.2: control vs candidate on one frozen dataset (attached by `compare`).
    return promotion_gates.promotion(reference, candidate)
