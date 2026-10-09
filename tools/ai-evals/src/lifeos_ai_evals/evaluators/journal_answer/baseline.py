"""Offline extractive baseline (`journal-answer-extractive-baseline`): validates the
scorer.

Not a candidate. Picks the passage sharing the most content words (>= 4 letters) with
the question; with at least two shared words it answers with that passage's first
sentence and cites it, otherwise it returns insufficient_evidence. The output goes
through the production `JournalAnswer` contract and citation-membership check, like a
model answer.
"""

import re

from lifeos_ai.journal_memory.schema import AnswerRequest, JournalAnswer
from pydantic import ValidationError

from lifeos_ai_evals.core.engine import Prediction
from lifeos_ai_evals.evaluators.journal_answer.model import AnswerOutput

_WORD = re.compile(r"\w{4,}", re.UNICODE)
_SENTENCE = re.compile(r"(.+?[.!?])(?:\s|$)", re.DOTALL)
_MARKUP = re.compile(r"\*\*|__|`|\]\(")


def words(text: str) -> set[str]:
    return {word.lower() for word in _WORD.findall(text)}


def extractive_answer(request: AnswerRequest) -> dict:
    question = words(request.question)
    best, overlap = None, 1
    for source in request.sources:
        shared = len(question & words(source.text))
        if shared > overlap:
            best, overlap = source, shared
    if best is None:
        return {"status": "insufficient_evidence", "answer": "", "citations": []}
    match = _SENTENCE.match(best.text.strip())
    sentence = (match.group(1) if match else best.text.strip())[:1000]
    sentence = _MARKUP.sub(" ", sentence).replace("\r", " ").replace("\t", " ")
    return {"status": "answered", "answer": sentence, "citations": [best.label]}


class ExtractiveBaseline:
    name = "journal-answer-extractive-baseline"
    version = "1.0.0"
    configuration = {
        "method": "most shared words (>= 4 letters), at least 2, first sentence",
        "provider": "deterministic",
    }

    def predict(self, value: AnswerRequest) -> Prediction[AnswerOutput]:
        raw = extractive_answer(value)
        try:
            answer = JournalAnswer.model_validate(raw)
        except ValidationError:
            return Prediction(AnswerOutput(False, "invalid_output", None, "", ()))
        if not answer.cites_only(value.labels()):
            return Prediction(AnswerOutput(False, "unknown_citation", None, "", ()))
        return Prediction(
            AnswerOutput(
                True,
                answer.status,
                answer.status,
                answer.answer,
                tuple(answer.citations),
                source_count=len(value.sources),
            )
        )
