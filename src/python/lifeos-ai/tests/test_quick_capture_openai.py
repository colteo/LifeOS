"""AI-009 I1: the Quick Capture OpenAI adapter, offline (scripted httpx transport, manual clock).

No network, no real key, no live call: every response is a synthetic Responses API body and
`no_network` fails any real send. The transport, spend guard and concurrency limit are the
Assistant adapter's (ResponsesCaller), exercised here through the Quick Capture roles.
"""

import asyncio
import json
import logging
from decimal import Decimal

import httpx
import pytest

from lifeos_ai.assistant.budget import SpendGuard
from lifeos_ai.assistant.interpreter import InterpretationFailed, InterpretationRefused
from lifeos_ai.assistant.openai_interpreter import (
    OpenAIAssistantInterpreter,
    ResponsesCaller,
    provider_output_schema,
)
from lifeos_ai.assistant.openai_transport import HttpResponsesTransport
from lifeos_ai.errors import ProviderUnavailable
from lifeos_ai.quick_capture.openai_interpreter import (
    PROVIDER_OUTPUT_SCHEMAS,
    ROUTER_DEADLINE_SECONDS,
    SPECIALIST_DEADLINE_SECONDS,
    OpenAIQuickCaptureInterpreter,
)
from lifeos_ai.quick_capture.prompt import DEFAULT_PROMPTS, build_input
from lifeos_ai.quick_capture.router import ROUTER_JSON_SCHEMA, RouteRequest
from lifeos_ai.quick_capture.specialists import (
    FINANCE_JSON_SCHEMA,
    HYDRATION_JSON_SCHEMA,
    MEAL_JSON_SCHEMA,
    FinanceInterpretRequest,
    HydrationInterpretRequest,
    MealInterpretRequest,
)
from tests.assistant_fixtures import request as assistant_request
from tests.fakes import ScriptedTransport
from tests.quick_capture_fixtures import ROLES, answer, request
from tests.test_assistant_openai import (  # noqa: F401
    BASE_URL,
    KEY,
    MODEL,
    REASONING,
    SUPPORTED_KEYWORDS,
    SUPPORTED_TYPES,
    URL,
    GatedTransport,
    ManualClock,
    body,
    error,
    message_item,
    no_network,
    reply,
    settings,
    text_part,
    walk,
)

MODELS = {
    "router": RouteRequest,
    "finance": FinanceInterpretRequest,
    "hydration": HydrationInterpretRequest,
    "meal": MealInterpretRequest,
}
SCHEMAS = {
    "router": ROUTER_JSON_SCHEMA,
    "finance": FINANCE_JSON_SCHEMA,
    "hydration": HYDRATION_JSON_SCHEMA,
    "meal": MEAL_JSON_SCHEMA,
}


def model_request(role: str):
    return MODELS[role].model_validate(request(role))


def ok(role: str = "router", content=None, **kwargs) -> httpx.Response:
    content = answer(role) if content is None else content
    return reply(body([REASONING, message_item(text_part(content))], **kwargs))


def http_transport(scripted, clock, config) -> HttpResponsesTransport:
    return HttpResponsesTransport(
        api_key=config.api_key,
        base_url=BASE_URL,
        timeout_seconds=config.timeout_seconds,
        max_attempts=config.max_attempts,
        client=httpx.AsyncClient(transport=scripted, base_url=BASE_URL),
        sleep=clock.sleep,
        clock=clock,
        jitter=lambda: 1.0,
    )


def adapter(*replies, transport=None, guard=None, **changes):
    scripted = transport or ScriptedTransport(*replies)
    clock = ManualClock()
    config = settings(**changes)
    interpreter = OpenAIQuickCaptureInterpreter(
        config, transport=http_transport(scripted, clock, config), guard=guard, clock=clock
    )
    return interpreter, scripted, clock


async def call(interpreter, role: str):
    value = model_request(role)
    if role == "router":
        return await interpreter.route(value)
    return await interpreter.interpret(value)


def run(interpreter, role: str = "router"):
    return asyncio.run(call(interpreter, role))


# ---- Requests: stateless, private, read-only, one frozen prompt and schema per role ----


@pytest.mark.parametrize("role", ROLES)
def test_a_valid_answer_is_returned_as_the_roles_output(role):
    interpreter, transport, _ = adapter(ok(role))

    output = run(interpreter, role)

    assert output.model_dump(mode="json") == answer(role)
    sent = transport.requests[0]
    assert (sent.method, str(sent.url)) == ("POST", URL)
    assert sent.headers["authorization"] == f"Bearer {KEY}"


@pytest.mark.parametrize("role", ROLES)
def test_the_request_is_exactly_the_stateless_strict_structured_output_call(role):
    interpreter, transport, _ = adapter(ok(role))

    run(interpreter, role)

    assert transport.bodies() == [
        {
            "model": MODEL,
            "input": build_input(model_request(role), DEFAULT_PROMPTS[role]),
            "store": False,
            "reasoning": {"effort": "low"},
            "max_output_tokens": 2000,
            "text": {
                "format": {
                    "type": "json_schema",
                    "name": f"quick_capture_{role}",
                    "strict": True,
                    "schema": provider_output_schema(SCHEMAS[role]),
                }
            },
        }
    ]


@pytest.mark.parametrize("role", ROLES)
def test_no_remote_state_tool_identifier_or_media_is_ever_sent(role):
    interpreter, transport, _ = adapter(error(503), ok(role))

    run(interpreter, role)

    assert len(transport.requests) == 2
    for sent in transport.bodies():
        assert sent["store"] is False
        for key in (
            "previous_response_id",
            "conversation",
            "background",
            "tools",
            "tool_choice",
            "parallel_tool_calls",
            "include",
            "metadata",
            "user",
            "safety_identifier",
            "prompt_cache_key",
            "stream",
            "service_tier",
        ):
            assert key not in sent, key
        assert [item["role"] for item in sent["input"]] == ["system", "user"]
        assert all(isinstance(item["content"], str) for item in sent["input"])


@pytest.mark.parametrize("role", ROLES)
def test_the_provider_schema_is_the_contract_in_the_documented_strict_subset(role):
    schema = PROVIDER_OUTPUT_SCHEMAS[role]

    assert schema == provider_output_schema(SCHEMAS[role])
    assert schema["properties"]["protocol_version"] == {"type": "integer"}
    for node, _ in walk(schema):
        assert set(node) <= SUPPORTED_KEYWORDS
        types = node["type"] if isinstance(node["type"], list) else [node["type"]]
        assert set(types) <= SUPPORTED_TYPES
        if "object" in types:
            assert node["additionalProperties"] is False
            assert node["required"] == list(node["properties"])
        if "enum" in node:
            assert all(isinstance(value, str) or value is None for value in node["enum"])
            assert (None in node["enum"]) == ("null" in types)


# ---- Outputs: held to the role's contract, refused whole, never repaired ----


def router_output(**segment_changes) -> dict:
    output = answer("router")
    output["segments"][0].update(segment_changes)
    return output


@pytest.mark.parametrize(
    ("response", "outcome"),
    [
        pytest.param(ok(content="{not json"), "invalid_json", id="invalid_json"),
        pytest.param(
            ok(content={**answer("router"), "confidence": 0.9}),
            "schema_invalid",
            id="extra_member",
        ),
        pytest.param(
            ok(content=router_output(span="ho speso 99 euro")),
            "protocol_violation:segment_span_not_in_message",
            id="span_not_in_message",
        ),
        pytest.param(
            ok(content={**answer("router"), "protocol_version": 2}),
            "protocol_violation:protocol_version",
            id="other_version",
        ),
        pytest.param(
            ok(content=router_output(module=None)),
            "protocol_violation:module_required",
            id="module_required",
        ),
        pytest.param(ok(content=""), "empty_output", id="empty_text"),
        pytest.param(reply(body([REASONING])), "empty_output", id="no_message"),
        pytest.param(
            reply(
                body(
                    [message_item(text_part('{"protocol_version": 1'))],
                    status="incomplete",
                    incomplete_details={"reason": "max_output_tokens"},
                )
            ),
            "incomplete_max_output_tokens",
            id="truncated",
        ),
        pytest.param(
            reply(body([{"type": "function_call", "name": "x", "arguments": "{}"}])),
            "unexpected_output_item",
            id="tool_call",
        ),
    ],
)
def test_an_invalid_router_output_is_failed_whole_and_never_retried(response, outcome, caplog):
    interpreter, transport, _ = adapter(response)

    with (
        caplog.at_level(logging.INFO, logger="lifeos_ai"),
        pytest.raises(InterpretationFailed) as failure,
    ):
        run(interpreter)

    assert (failure.value.status, failure.value.code) == (502, "interpretation_failed")
    assert len(transport.requests) == 1
    assert f"outcome={outcome} role=router " in caplog.text


@pytest.mark.parametrize("role", ["finance", "hydration", "meal"])
def test_a_specialist_span_outside_the_authorised_text_is_failed(role, caplog):
    output = answer(role)
    section = output[role]
    stated = next(name for name, field in section.items() if field["state"] == "stated")
    section[stated]["span"] = "testo di un altro segmento"
    interpreter, _, _ = adapter(ok(role, output))

    with caplog.at_level(logging.INFO, logger="lifeos_ai"), pytest.raises(InterpretationFailed):
        run(interpreter, role)

    assert "outcome=protocol_violation:" in caplog.text and f"role={role} " in caplog.text


def test_an_answer_of_another_role_is_failed():
    interpreter, _, _ = adapter(ok("router", answer("meal")))

    with pytest.raises(InterpretationFailed):
        run(interpreter, "router")


@pytest.mark.parametrize(
    "response",
    [
        pytest.param(
            reply(body([message_item({"type": "refusal", "refusal": "I can't help"})])),
            id="refusal_part",
        ),
        pytest.param(
            reply(
                body(
                    [message_item(text_part("{}"))],
                    status="incomplete",
                    incomplete_details={"reason": "content_filter"},
                )
            ),
            id="content_filter",
        ),
    ],
)
@pytest.mark.parametrize("role", ROLES)
def test_a_refusal_is_interpretation_refused(role, response):
    interpreter, transport, _ = adapter(response)

    with pytest.raises(InterpretationRefused) as failure:
        run(interpreter, role)

    assert (failure.value.status, failure.value.code) == (502, "interpretation_refused")
    assert len(transport.requests) == 1


# ---- Provider failures: the Assistant's mapping, retries inside the role's deadline ----


@pytest.mark.parametrize(
    ("response", "outcome"),
    [
        (error(400), "bad_request"),
        (error(401), "unauthenticated"),
        (error(403), "forbidden"),
        (error(404), "not_found"),
        (error(429, code="insufficient_quota"), "quota_exhausted"),
        (reply(body([], status="failed")), "response_failed"),
        (ok(model="gpt-other"), "model_mismatch"),
    ],
)
def test_final_errors_are_unavailable_and_never_retried(response, outcome, caplog):
    interpreter, transport, clock = adapter(response, max_attempts=3)

    with caplog.at_level(logging.INFO, logger="lifeos_ai"), pytest.raises(ProviderUnavailable):
        run(interpreter)

    assert len(transport.requests) == 1
    assert clock.sleeps == []
    assert f"outcome={outcome} role=router " in caplog.text


@pytest.mark.parametrize(
    "first",
    [
        pytest.param(error(429), id="429"),
        pytest.param(error(500), id="500"),
        pytest.param(httpx.ReadTimeout("synthetic"), id="read_timeout"),
        pytest.param(httpx.ConnectError("synthetic"), id="connect_error"),
    ],
)
def test_transient_failures_are_retried_by_the_shared_transport(first):
    interpreter, transport, clock = adapter(first, ok("meal"))

    output = run(interpreter, "meal")

    assert output.outcome == "interpreted"
    assert len(transport.requests) == 2 and len(clock.sleeps) == 1


@pytest.mark.parametrize(
    ("failure", "outcome"),
    [
        (lambda: httpx.ReadTimeout("synthetic"), "timeout"),
        (lambda: error(503), "server_error_503"),
    ],
)
def test_a_persistent_timeout_or_server_error_is_unavailable(failure, outcome, caplog):
    interpreter, transport, _ = adapter(failure(), failure())

    with caplog.at_level(logging.INFO, logger="lifeos_ai"), pytest.raises(ProviderUnavailable):
        run(interpreter, "finance")

    assert len(transport.requests) == 2
    assert f"outcome={outcome} role=finance " in caplog.text and "attempts=2" in caplog.text


class DeadlineTransport:
    """Records the deadline of each request and answers at once."""

    def __init__(self, role: str):
        self.role = role
        self.deadlines: list[float] = []

    async def create_response(self, body: dict, *, deadline: float):
        self.deadlines.append(deadline)
        return json.loads(ok(self.role).content), 1

    async def aclose(self) -> None:
        return None


@pytest.mark.parametrize(
    ("role", "seconds"),
    [
        ("router", ROUTER_DEADLINE_SECONDS),
        ("finance", SPECIALIST_DEADLINE_SECONDS),
        ("hydration", SPECIALIST_DEADLINE_SECONDS),
        ("meal", SPECIALIST_DEADLINE_SECONDS),
    ],
)
def test_each_role_has_its_own_deadline_inside_the_turn_lease(role, seconds):
    clock = ManualClock()
    transport = DeadlineTransport(role)
    interpreter = OpenAIQuickCaptureInterpreter(settings(), transport=transport, clock=clock)

    run(interpreter, role)

    assert transport.deadlines == [clock.now + seconds]
    assert (ROUTER_DEADLINE_SECONDS, SPECIALIST_DEADLINE_SECONDS) == (25.0, 35.0)
    # Router then specialists in parallel, plus the 15 s completion margin, fit the 90 s lease.
    assert ROUTER_DEADLINE_SECONDS + SPECIALIST_DEADLINE_SECONDS + 15 <= 90


@pytest.mark.parametrize("deadline", [0, -1])
def test_a_deadline_must_be_positive(deadline):
    with pytest.raises(ValueError):
        OpenAIQuickCaptureInterpreter(settings(), router_deadline_seconds=deadline)


# ---- Concurrency, cancellation, budget: shared with the Assistant ----


def test_specialists_in_parallel_respect_the_concurrency_limit():
    async def scenario(limit):
        gated = GatedTransport(lambda: ok("hydration"))
        interpreter, _, _ = adapter(transport=gated, max_concurrency=limit)
        tasks = [asyncio.create_task(call(interpreter, "hydration")) for _ in range(3)]
        await gated.started.wait()
        await asyncio.sleep(0)
        gated.gate.set()
        results = await asyncio.gather(*tasks)
        return results, gated

    results, gated = asyncio.run(scenario(1))
    assert [r.outcome for r in results] == ["interpreted"] * 3
    assert gated.peak == 1 and gated.count == 3

    results, gated = asyncio.run(scenario(2))
    assert gated.peak == 2 and gated.count == 3


def test_no_free_slot_before_the_deadline_is_unavailable(caplog):
    async def scenario():
        gated = GatedTransport(lambda: ok("router"))
        interpreter, _, _ = adapter(transport=gated, max_concurrency=1)
        interpreter._clock = asyncio.get_running_loop().time
        interpreter._deadlines["router"] = 0.05
        first = asyncio.create_task(call(interpreter, "router"))
        await gated.started.wait()
        with pytest.raises(ProviderUnavailable):
            await call(interpreter, "router")
        gated.gate.set()
        await first
        return gated

    with caplog.at_level(logging.INFO, logger="lifeos_ai"):
        gated = asyncio.run(scenario())

    assert gated.count == 1
    assert "outcome=concurrency_limit role=router " in caplog.text


def test_cancellation_propagates_and_releases_the_slot(caplog):
    async def scenario():
        gated = GatedTransport(lambda: ok("meal"))
        interpreter, _, _ = adapter(transport=gated, max_concurrency=1)
        task = asyncio.create_task(call(interpreter, "meal"))
        await gated.started.wait()
        task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await task
        gated.gate.set()
        return await call(interpreter, "meal")

    with caplog.at_level(logging.INFO, logger="lifeos_ai"):
        output = asyncio.run(scenario())

    assert output.outcome == "interpreted"
    assert "outcome=cancelled role=meal " in caplog.text


def test_an_exhausted_budget_sends_nothing(caplog):
    guard = SpendGuard(daily_budget=Decimal("0.000001"), monthly_budget=Decimal("1"))
    interpreter, transport, _ = adapter(ok(), guard=guard)

    with caplog.at_level(logging.INFO, logger="lifeos_ai"), pytest.raises(ProviderUnavailable):
        run(interpreter)

    assert transport.requests == []
    assert "outcome=budget_exhausted_daily role=router " in caplog.text


def test_the_reported_usage_is_charged_to_the_guard():
    guard = SpendGuard(daily_budget=Decimal("1"), monthly_budget=Decimal("10"))
    interpreter, _, _ = adapter(ok(), guard=guard)

    run(interpreter)

    # 3100 input at 0.10/M + 420 output at 0.50/M (the synthetic usage)
    assert guard.spent() == (Decimal("0.00052"), Decimal("0.00052"))


def test_a_shared_caller_puts_the_assistant_and_quick_capture_on_one_budget_and_one_slot():
    scripted = ScriptedTransport(ok("router"))
    clock = ManualClock()
    config = settings(max_concurrency=1)
    guard = SpendGuard(daily_budget=Decimal("1"), monthly_budget=Decimal("10"))
    assistant = OpenAIAssistantInterpreter(
        config, transport=http_transport(scripted, clock, config), guard=guard, clock=clock
    )
    quick_capture = OpenAIQuickCaptureInterpreter(config, caller=assistant.caller, clock=clock)

    run(quick_capture)

    assert guard.spent()[0] == Decimal("0.00052")
    assert quick_capture._caller is assistant.caller

    # Closing Quick Capture leaves the shared transport to its owner.
    asyncio.run(quick_capture.aclose())
    scripted.replies.extend([httpx.Response(503), httpx.Response(503)])
    with pytest.raises(ProviderUnavailable):
        asyncio.run(assistant.interpret(_assistant_request()))
    assert len(scripted.requests) == 3  # the assistant's requests still went through


def _assistant_request():
    from lifeos_ai.assistant.schema import InterpretRequest

    return InterpretRequest.model_validate(assistant_request())


def test_a_shared_caller_cannot_be_combined_with_another_transport_or_guard():
    caller = ResponsesCaller(settings())

    with pytest.raises(ValueError):
        OpenAIQuickCaptureInterpreter(
            settings(),
            caller=caller,
            guard=SpendGuard(daily_budget=Decimal("1"), monthly_budget=Decimal("1")),
        )


def test_its_own_caller_is_closed_with_the_adapter():
    class Closing(DeadlineTransport):
        closed = False

        async def aclose(self) -> None:
            self.closed = True

    transport = Closing("router")
    asyncio.run(OpenAIQuickCaptureInterpreter(settings(), transport=transport).aclose())

    assert transport.closed


# ---- Logs and failures: no user, output or provider text ----


def test_logs_never_carry_user_data_output_provider_text_or_the_key(caplog):
    scripts = [
        ("router", ok("router")),
        ("finance", ok("finance")),
        ("hydration", error(400)),
        ("meal", error(401)),
        ("router", ok("router", router_output(span="Pamela non c'è"))),
        ("finance", reply(body([message_item({"type": "refusal", "refusal": "Pamela no"})]))),
    ]
    with caplog.at_level(logging.DEBUG):
        for role, script in scripts:
            interpreter, _, _ = adapter(script, max_attempts=1)
            try:
                run(interpreter, role)
            except (InterpretationFailed, InterpretationRefused, ProviderUnavailable):
                pass

    text = caplog.text
    assert "outcome=ok role=router" in text and "input_tokens=3100" in text
    assert "prompt_version=quick-capture-finance-interpret-v1" in text
    for secret in (
        "pizza",
        "pomodoro",
        "Ieri",
        "Ristoranti",
        "Stipendio",
        "Pamela",
        "provider secret detail",
        KEY,
        "resp_never_read",
        "Traceback",
    ):
        assert secret not in text, secret


def test_failures_raised_to_the_endpoint_carry_no_provider_or_user_text():
    interpreter, _, _ = adapter(error(400))

    with pytest.raises(ProviderUnavailable) as failure:
        run(interpreter)

    assert "Pamela" not in str(failure.value) and "secret" not in str(failure.value)
    assert failure.value.__cause__ is None and failure.value.__suppress_context__
