"""Live answer predictor over the PRODUCTION `GroqJournalAnswerer`.

The production adapter builds the request (prompt `journal-rag-answer-v1`, strict
json_schema, settings), sends it with the production transport policy and validates the
answer (schema and citation membership). This wrapper only pins the experiment identity
(the control, or the AI-005.2 candidate whose only difference is a registered prompt
version) and observes:
- telemetry through the lab's observing transport (attempts, statuses, latency, tokens);
- the production adapter's own outcome classification, read from its log line (`journal
  answer result=... outcome=...`), never its content.

Outcomes: a valid answer -> scored; AnswerFailed (invalid output, unknown citation,
rejected request) -> scored as an invalid output; ProviderUnavailable -> execution error
(excluded from quality metrics, reported as coverage). For the control, the request body
is byte-for-byte the production body; for the candidate it differs only in the system
message (tested).
"""

import asyncio
import logging
import re
import time

import httpx
from lifeos_ai.journal_memory.answer import AnswerFailed
from lifeos_ai.journal_memory.groq import GroqJournalAnswerer
from lifeos_ai.journal_memory.prompt import PROMPT_VERSION, PROMPTS
from lifeos_ai.journal_memory.schema import AnswerRequest

from lifeos_ai_evals.core.engine import Prediction
from lifeos_ai_evals.evaluators.journal_answer.model import AnswerOutput
from lifeos_ai_evals.journal_memory.identity import ANSWER_CONTROL
from lifeos_ai_evals.production import runtime

# PROMPTS is the production registry (lifeos_ai.journal_memory.prompt), imported, never
# copied. Production serves PROMPT_VERSION; v2 is an evaluation candidate (AI-005.2).
LOGGER = "lifeos_ai.journal_memory"
_OUTCOME = re.compile(r"^journal answer result=(\S+) outcome=(\S+) ")


class _OutcomeCapture(logging.Handler):
    def __init__(self):
        super().__init__(logging.INFO)
        self.outcomes: list[tuple[str, str]] = []

    def emit(self, record: logging.LogRecord) -> None:
        match = _OUTCOME.match(record.getMessage())
        if match:
            self.outcomes.append((match.group(1), match.group(2)))


class ControlAnswerer(GroqJournalAnswerer):
    """The production answerer with the control's generation settings (identical) and
    the experiment's registered prompt version (the production default for the
    control)."""

    def __init__(
        self,
        settings,
        *,
        generation: dict,
        prompt_version: str = PROMPT_VERSION,
        **kwargs,
    ):
        super().__init__(settings, **kwargs)
        # The adapter's own log line then names the prompt that was actually sent.
        self.prompt_version = prompt_version
        self._generation = generation

    def request_body(self, request: AnswerRequest) -> dict:
        body = super().request_body(request)
        body = runtime.replace_system_prompt(body, PROMPTS[self.prompt_version])
        return runtime.apply_generation_settings(body, self._generation)


class LiveAnswerSession:
    """Shared by the answer and end-to-end evaluators: one production answer call."""

    def __init__(
        self,
        *,
        identity: dict = ANSWER_CONTROL,
        api_key: str | None = None,
        transport: httpx.AsyncBaseTransport | None = None,
        sleep=None,
        case_sleep=time.sleep,
    ):
        if PROMPT_VERSION != ANSWER_CONTROL["prompt_version"]:
            raise ValueError("production prompt drift: update AI-005 deliberately")
        # Only a registered prompt version may differ from the control.
        control_like = {**identity, "prompt_version": ANSWER_CONTROL["prompt_version"]}
        if identity["prompt_version"] not in PROMPTS or control_like != ANSWER_CONTROL:
            raise ValueError(
                "the answer identity may differ from the control only in a registered "
                "prompt_version"
            )
        self.identity = identity
        self._generation = runtime.validate_generation_settings(
            ANSWER_CONTROL["generation_settings"]
        )
        self._api_key = api_key or (
            "offline-test-key" if transport else runtime.api_key_from_environment()
        )
        self._transport = transport
        self._sleep = sleep
        self._case_sleep = case_sleep
        self._calls = 0
        self.telemetry = runtime.Telemetry()

    def answerer(self) -> ControlAnswerer:
        kwargs = {"client": runtime.client(self.telemetry, self._transport)}
        if self._sleep is not None:
            kwargs["sleep"] = self._sleep
        return ControlAnswerer(
            runtime.groq_settings(
                self._api_key, ANSWER_CONTROL["model"], self._generation
            ),
            generation=self._generation,
            prompt_version=self.identity["prompt_version"],
            **kwargs,
        )

    def answer(self, request: AnswerRequest) -> AnswerOutput:
        if self._calls:
            self._case_sleep(runtime.RUNTIME["inter_case_delay_seconds"])
        self._calls += 1
        return asyncio.run(self._answer(request))

    async def _answer(self, request: AnswerRequest) -> AnswerOutput:
        answerer = self.answerer()
        self.telemetry.logical_requests += 1
        mark = self.telemetry.mark()
        capture = _OutcomeCapture()
        logger = logging.getLogger(LOGGER)
        level = logger.level
        logger.addHandler(capture)
        logger.setLevel(logging.INFO)
        started = time.perf_counter()
        try:
            try:
                answer = await answerer.answer(request)
            except AnswerFailed:
                outcome = (
                    capture.outcomes[-1][1] if capture.outcomes else "invalid_output"
                )
                return self._output(
                    False, outcome, None, "", (), mark, started, request
                )
        finally:
            logger.removeHandler(capture)
            logger.setLevel(level)
            await answerer.aclose()
        return self._output(
            True,
            answer.status,
            answer.status,
            answer.answer,
            tuple(answer.citations),
            mark,
            started,
            request,
        )

    def _output(self, valid, outcome, status, text, citations, mark, started, request):
        attempts = self.telemetry.since(mark)
        usage = [a.usage for a in attempts if a.usage]

        def total(key):
            counts = [u[key] for u in usage if u.get(key) is not None]
            return sum(counts) if counts else None

        return AnswerOutput(
            valid,
            outcome,
            status,
            text,
            citations,
            attempts=len(attempts),
            latency_seconds=time.perf_counter() - started,
            input_tokens=total("input_tokens"),
            output_tokens=total("output_tokens"),
            total_tokens=total("total_tokens"),
            source_count=len(request.sources),
        )

    def metadata(self) -> dict:
        return {
            "runtime": dict(runtime.RUNTIME),
            "adapter": "lifeos_ai.journal_memory.groq.GroqJournalAnswerer",
            "telemetry": self.telemetry.summary(),
        }


class LiveAnswerPredictor:
    version = "1.0.0"

    def __init__(self, identity: dict, **kwargs):
        self.identity = identity
        self.name = (
            "journal-answer-production-control"
            if identity == ANSWER_CONTROL
            else "journal-answer-prompt-candidate"
        )
        self.session = LiveAnswerSession(identity=identity, **kwargs)
        self.configuration = {
            **identity,
            "runtime": dict(runtime.RUNTIME),
            "adapter": "lifeos_ai.journal_memory.groq.GroqJournalAnswerer",
        }

    def predict(self, value: AnswerRequest) -> Prediction[AnswerOutput]:
        return Prediction(self.session.answer(value))

    def experiment_metadata(self) -> dict:
        return {
            "reproducibility": "experiment identity; probabilistic outputs",
            "telemetry": self.session.telemetry.summary(),
        }
