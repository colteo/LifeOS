"""AI-007 F3-B1: systems that answer `assistant_finance` cases.

Both run the PRODUCTION `OpenAIAssistantInterpreter` (request body, output parsing,
protocol validation, failure mapping); only what sits behind its HTTP client differs:

- `fake` (offline, default): an httpx mock answers each request with the reference
  interpretation built from the case's expectation (or the case's scripted malformed
  response). No network and no key. It proves the dataset is satisfiable under protocol
  v1 and that the scorer, the adapter and the gates agree end to end; it measures
  nothing about a model.
- live (live_run.py only): the real HTTPS transport, after explicit opt-in.
"""

import asyncio
import json
from decimal import Decimal
from pathlib import Path

import httpx
from lifeos_ai.assistant.budget import Prices
from lifeos_ai.assistant.interpreter import InterpretationFailed, InterpretationRefused
from lifeos_ai.assistant.openai_interpreter import (
    OpenAIAssistantInterpreter,
    OpenAIAssistantSettings,
)
from lifeos_ai.assistant.openai_transport import HttpResponsesTransport
from lifeos_ai.assistant.schema import InterpretRequest

from lifeos_ai_evals.core.engine import Prediction
from lifeos_ai_evals.evaluators.assistant_finance.model import DATE_PARTS

FAKE_MODEL = "offline-fake-model"
FAKE_BASE_URL = "https://offline.invalid/v1"


def request_key(request: dict) -> str:
    return json.dumps(request, ensure_ascii=False, sort_keys=True)


def reference_interpretation(expected: dict) -> dict:
    """The answer a perfect model would give, built from the expectation."""

    def text(spec=None):
        if spec is None or spec["state"] in ("absent", "clear"):
            state = "absent" if spec is None else spec["state"]
            return {"state": state, "value": None, "span": None}
        if spec["state"] == "inferred":
            return {"state": "inferred", "value": spec["values"][0], "span": None}
        return {"state": "stated", "value": spec["value"], "span": spec["span"]}

    def date(spec=None):
        value = {part: None for part in DATE_PARTS}
        if spec is None or spec["state"] != "stated":
            return {
                "state": "absent" if spec is None else spec["state"],
                **value,
                "span": None,
            }
        value.update({part: spec.get(part) for part in DATE_PARTS})
        return {"state": "stated", **value, "span": spec["span"]}

    def time(spec=None):
        if spec is None or spec["state"] != "stated":
            state = "absent" if spec is None else spec["state"]
            return {"state": state, "hour": None, "minute": None, "span": None}
        return {
            "state": "stated",
            "hour": spec["hour"],
            "minute": spec["minute"],
            "span": spec["span"],
        }

    fields = expected["fields"]
    finance = {
        name: text(fields.get(name))
        for name in ("type", "amount", "currency", "account", "category", "note")
    }
    finance["date"] = date(fields.get("date"))
    finance["time"] = time(fields.get("time"))
    return {
        "protocol_version": 1,
        "intent": expected["intent"],
        "finance": finance,
        "ambiguous_fields": list(expected.get("ambiguous_fields", [])),
        "unsupported_reason": expected.get("unsupported_reason"),
    }


def responses_body(interpretation: dict, model: str = FAKE_MODEL) -> dict:
    return {
        "object": "response",
        "status": "completed",
        "model": model,
        "output": [
            {
                "type": "message",
                "role": "assistant",
                "content": [
                    {"type": "output_text", "text": json.dumps(interpretation)}
                ],
            }
        ],
        "usage": {"input_tokens": 0, "output_tokens": 0},
    }


class InterpreterPredictor:
    """Runs one interpreter over the cases on one event loop (its HTTP client is
    bound to it)."""

    version = "1.0.0"

    def __init__(
        self, interpreter: OpenAIAssistantInterpreter, name: str, configuration: dict
    ):
        self.name = name
        self.configuration = configuration
        self.interpreter = interpreter
        self._loop = asyncio.new_event_loop()

    def predict(self, value: dict) -> Prediction:
        request = InterpretRequest.model_validate(value["request"])
        open_draft = request.context.finance.open_draft is not None
        try:
            interpretation = self._loop.run_until_complete(
                self.interpreter.interpret(request)
            )
        except InterpretationRefused:
            return Prediction(
                {
                    "outcome": "interpretation_refused",
                    "interpretation": None,
                    "open_draft": open_draft,
                }
            )
        except InterpretationFailed:
            return Prediction(
                {
                    "outcome": "interpretation_failed",
                    "interpretation": None,
                    "open_draft": open_draft,
                }
            )
        # ProviderUnavailable (and anything else) propagates: an execution error, not a
        # score.
        return Prediction(
            {
                "outcome": "ok",
                "interpretation": interpretation.model_dump(mode="json"),
                "open_draft": open_draft,
            }
        )

    def close(self) -> None:
        self._loop.run_until_complete(self.interpreter.aclose())
        self._loop.close()


class _ReferenceResponses(httpx.AsyncBaseTransport):
    """Offline: the current case's scripted response, else its reference answer."""

    def __init__(self, references: dict[str, dict]):
        self.references = references
        self.scripted: dict | None = None

    async def handle_async_request(self, request: httpx.Request) -> httpx.Response:
        body = json.loads(request.content)
        if body.get("store") is not False:
            raise AssertionError("store=false must be sent")
        if self.scripted is not None:
            return httpx.Response(200, json={"model": FAKE_MODEL, **self.scripted})
        key = request_key(json.loads(body["input"][1]["content"]))
        return httpx.Response(200, json=responses_body(self.references[key]))


class _FakePredictor(InterpreterPredictor):
    def __init__(
        self, interpreter, name, configuration, responses: _ReferenceResponses
    ):
        super().__init__(interpreter, name, configuration)
        self._responses = responses

    def predict(self, value: dict) -> Prediction:
        self._responses.scripted = value.get("scripted_response")
        try:
            return super().predict(value)
        finally:
            self._responses.scripted = None


def fake_settings() -> OpenAIAssistantSettings:
    return OpenAIAssistantSettings(
        api_key="offline-fake-key",
        model=FAKE_MODEL,
        region="global",
        daily_budget_usd=Decimal("1"),
        monthly_budget_usd=Decimal("1"),
        prices=Prices(Decimal("0.000001"), Decimal("0.000001")),
        max_attempts=1,
    )


def fake_system(dataset_path: Path) -> InterpreterPredictor:
    """References from the message dataset; pipeline cases carry their response."""
    references: dict[str, dict] = {}
    for case in json.loads(dataset_path.read_text(encoding="utf-8"))["cases"]:
        key = request_key(case["input"]["request"])
        if key in references:
            raise ValueError(f"{case['id']}: another case has the same request")
        references[key] = reference_interpretation(case["expected"])
    responses = _ReferenceResponses(references)
    settings = fake_settings()
    transport = HttpResponsesTransport(
        api_key=settings.api_key,
        base_url=FAKE_BASE_URL,
        timeout_seconds=settings.timeout_seconds,
        max_attempts=settings.max_attempts,
        client=httpx.AsyncClient(transport=responses, base_url=FAKE_BASE_URL),
    )
    interpreter = OpenAIAssistantInterpreter(settings, transport=transport)
    return _FakePredictor(
        interpreter,
        "assistant-finance-offline-fake",
        {
            "provider": "offline-fake",
            "model": FAKE_MODEL,
            "prompt_version": interpreter.prompt_version,
            "structured_output": "strict json_schema (production adapter, mocked HTTP)",
        },
        responses,
    )
