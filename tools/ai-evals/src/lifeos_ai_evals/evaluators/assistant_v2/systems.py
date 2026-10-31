"""AI-008 G1/G2: systems that answer `assistant_v2` cases.

Both run the PRODUCTION `OpenAIAssistantInterpreter` with protocol-2 requests (prompt
`prompt_version_v2`, `assistant-interpret-v3` by default; v2 output schema; protocol
validation; failure mapping); only what sits behind its HTTP client differs:

- `fake` (offline, default): an httpx mock answers each request with the reference
  interpretation built from the case's expectation. No network and no key. It proves
  the datasets are satisfiable under protocol 2 and that the scorer, the adapter and the
  gates agree end to end; it measures nothing about a model.
- live (live_run.py only): the real HTTPS transport, after explicit opt-in.
"""

import asyncio
import json
from collections.abc import Iterable

import httpx
from lifeos_ai.assistant.interpreter import InterpretationFailed, InterpretationRefused
from lifeos_ai.assistant.openai_interpreter import OpenAIAssistantInterpreter
from lifeos_ai.assistant.openai_transport import HttpResponsesTransport
from lifeos_ai.assistant.schema_v2 import InterpretRequest

from lifeos_ai_evals.core.engine import Dataset, Prediction
from lifeos_ai_evals.evaluators.assistant_finance.systems import (
    FAKE_BASE_URL,
    FAKE_MODEL,
    fake_settings,
    request_key,
    responses_body,
)
from lifeos_ai_evals.evaluators.assistant_v2.model import (
    DATE_PARTS,
    DRAFT_KIND_SECTIONS,
    SECTION_FIELDS,
)


def _text(spec=None) -> dict:
    if spec is None or spec["state"] in ("absent", "clear"):
        state = "absent" if spec is None else spec["state"]
        return {"state": state, "value": None, "span": None}
    if spec["state"] == "inferred":
        return {"state": "inferred", "value": spec["values"][0], "span": None}
    return {"state": "stated", "value": spec["value"], "span": spec["span"]}


def _date(spec=None) -> dict:
    value = {part: None for part in DATE_PARTS}
    if spec is None or spec["state"] != "stated":
        state = "absent" if spec is None else spec["state"]
        return {"state": state, **value, "span": None}
    value.update({part: spec.get(part) for part in DATE_PARTS})
    return {"state": "stated", **value, "span": spec["span"]}


def _time(spec=None) -> dict:
    if spec is None or spec["state"] != "stated":
        state = "absent" if spec is None else spec["state"]
        return {"state": state, "hour": None, "minute": None, "span": None}
    return {
        "state": "stated",
        "hour": spec["hour"],
        "minute": spec["minute"],
        "span": spec["span"],
    }


def _section(name: str, fields: dict) -> dict:
    builders = {"date": _date, "time": _time}
    return {
        field: builders.get(field, _text)(fields.get(field))
        for field in SECTION_FIELDS[name]
    }


def reference_interpretation(expected: dict) -> dict:
    """The answer a perfect model would give, built from the expectation."""
    return {
        "protocol_version": 2,
        "intent": expected["intent"],
        **{
            name: _section(
                name, expected["fields"] if name == expected["section"] else {}
            )
            for name in SECTION_FIELDS
        },
        "ambiguous_fields": list(expected.get("ambiguous_fields", [])),
        "unsupported_reason": expected.get("unsupported_reason"),
    }


class V2Predictor:
    """Runs one interpreter over protocol-2 cases on one event loop (its HTTP client is
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
        draft = request.context.open_draft
        base = {
            "interpretation": None,
            "open_draft_section": DRAFT_KIND_SECTIONS[draft.kind] if draft else None,
        }
        try:
            interpretation = self._loop.run_until_complete(
                self.interpreter.interpret(request)
            )
        except InterpretationRefused:
            return Prediction({**base, "outcome": "interpretation_refused"})
        except InterpretationFailed:
            return Prediction({**base, "outcome": "interpretation_failed"})
        # ProviderUnavailable (and anything else) propagates: an execution error, not a
        # score.
        return Prediction(
            {
                **base,
                "outcome": "ok",
                "interpretation": interpretation.model_dump(mode="json"),
            }
        )

    def close(self) -> None:
        self._loop.run_until_complete(self.interpreter.aclose())
        self._loop.close()


class _ReferenceResponses(httpx.AsyncBaseTransport):
    """Offline: the reference answer of the request's case."""

    def __init__(self, references: dict[str, dict]):
        self.references = references

    async def handle_async_request(self, request: httpx.Request) -> httpx.Response:
        body = json.loads(request.content)
        if body.get("store") is not False:
            raise AssertionError("store=false must be sent")
        key = request_key(json.loads(body["input"][1]["content"]))
        return httpx.Response(200, json=responses_body(self.references[key]))


def fake_system(datasets: Iterable[Dataset]) -> V2Predictor:
    """References from the datasets' expectations. A request repeated across the
    datasets is refused (a holdout must not copy its development set)."""
    references: dict[str, dict] = {}
    for dataset in datasets:
        for case in dataset.cases:
            key = request_key(case.input["request"])
            if key in references:
                raise ValueError(f"{case.id}: another case has the same request")
            references[key] = reference_interpretation(case.expected)
    settings = fake_settings()
    transport = HttpResponsesTransport(
        api_key=settings.api_key,
        base_url=FAKE_BASE_URL,
        timeout_seconds=settings.timeout_seconds,
        max_attempts=settings.max_attempts,
        client=httpx.AsyncClient(
            transport=_ReferenceResponses(references), base_url=FAKE_BASE_URL
        ),
    )
    interpreter = OpenAIAssistantInterpreter(settings, transport=transport)
    return V2Predictor(
        interpreter,
        "assistant-v2-offline-fake",
        {
            "provider": "offline-fake",
            "model": FAKE_MODEL,
            "prompt_version": interpreter.prompt_version_v2,
            "structured_output": "strict json_schema (production adapter, mocked HTTP)",
        },
    )
