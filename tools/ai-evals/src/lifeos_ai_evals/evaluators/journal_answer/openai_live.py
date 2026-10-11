"""AI-006: live answers from the PRODUCTION `OpenAIJournalAnswerer` (final candidate).

The production adapter builds the Responses request (prompt journal-rag-answer-v1,
strict structured output, reasoning medium, store false), sends it with the production
transport policy and validates the answer. This session reuses the AI-005
`LiveAnswerSession` call path unchanged (outcome classification from the adapter's
own log line, scoring output, pacing); only the adapter it builds differs. Telemetry
adds the cached-input and reasoning token totals the Responses API reports.
"""

import time

import httpx

from lifeos_ai_evals.core.engine import Prediction
from lifeos_ai_evals.evaluators.journal_answer.live import LiveAnswerSession
from lifeos_ai_evals.journal_memory import openai_final
from lifeos_ai_evals.production import runtime


class OpenAIAnswerSession(LiveAnswerSession):
    def __init__(
        self,
        *,
        identity: dict = openai_final.ANSWER_FINAL,
        api_key: str | None = None,
        transport: httpx.AsyncBaseTransport | None = None,
        sleep=None,
        case_sleep=time.sleep,
    ):
        if identity != openai_final.ANSWER_FINAL:
            raise ValueError(
                "AI-006 runs only the frozen OpenAI final answer candidate"
            )
        self.identity = identity
        self._api_key = api_key or (
            "offline-test-key" if transport else openai_final.key_from_environment()
        )
        self._transport = transport
        self._sleep = sleep
        self._case_sleep = case_sleep
        self._calls = 0
        self.telemetry = runtime.Telemetry()

    def answerer(self):
        return openai_final.live_answerer(
            self._api_key, self.telemetry, self._transport, self._sleep
        )

    def token_details(self) -> dict:
        def total(key):
            counts = [
                attempt.usage[key]
                for attempt in self.telemetry.attempts
                if attempt.usage and attempt.usage.get(key) is not None
            ]
            return sum(counts) if counts else None

        return {
            "cached_input_tokens": total("cached_input_tokens"),
            "reasoning_tokens": total("reasoning_tokens"),
        }

    def metadata(self) -> dict:
        return {
            "runtime": dict(openai_final.ANSWER_RUNTIME),
            "adapter": openai_final.ANSWER_ADAPTER,
            "telemetry": self.telemetry.summary(),
            "token_details": self.token_details(),
        }


class OpenAIAnswerPredictor:
    version = "1.0.0"
    name = "journal-answer-openai-final-candidate"

    def __init__(self, identity: dict = openai_final.ANSWER_FINAL, **kwargs):
        self.identity = identity
        self.session = OpenAIAnswerSession(identity=identity, **kwargs)
        self.configuration = {
            **identity,
            "runtime": dict(openai_final.ANSWER_RUNTIME),
            "adapter": openai_final.ANSWER_ADAPTER,
        }

    def predict(self, value) -> Prediction:
        return Prediction(self.session.answer(value))

    def experiment_metadata(self) -> dict:
        return {
            "reproducibility": "experiment identity; probabilistic outputs",
            "telemetry": self.session.telemetry.summary(),
            "token_details": self.session.token_details(),
        }
