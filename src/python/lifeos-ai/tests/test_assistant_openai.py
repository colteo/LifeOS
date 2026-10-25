"""AI-007 F3-B1: the assistant's OpenAI adapter, offline (scripted httpx transport, manual clock).

No network, no real key: every response below is a synthetic Responses API body. Nothing here may
reach OpenAI; `no_network` makes any real send fail the test.
"""

import asyncio
import json
import logging
from decimal import Decimal

import httpx
import pytest

from lifeos_ai.assistant.budget import Prices, SpendGuard
from lifeos_ai.assistant.interpreter import InterpretationFailed, InterpretationRefused
from lifeos_ai.assistant.openai_interpreter import (
    PROVIDER_OUTPUT_SCHEMA,
    OpenAIAssistantInterpreter,
    OpenAIAssistantSettings,
    provider_output_schema,
)
from lifeos_ai.assistant.openai_transport import HttpResponsesTransport
from lifeos_ai.assistant.prompt import SYSTEM_PROMPT, build_input
from lifeos_ai.assistant.schema import INTERPRETATION_JSON_SCHEMA, InterpretRequest
from lifeos_ai.errors import ProviderUnavailable
from tests.assistant_fixtures import EXPENSE, MESSAGE, expense, request
from tests.fakes import ScriptedTransport

BASE_URL = "https://openai.test/v1"
URL = f"{BASE_URL}/responses"
KEY = "test-assistant-key-not-real"
MODEL = "gpt-test-model"
REQUEST = InterpretRequest.model_validate(request())
USAGE = {
    "input_tokens": 3100,
    "input_tokens_details": {"cached_tokens": 2048},
    "output_tokens": 420,
    "output_tokens_details": {"reasoning_tokens": 128},
    "total_tokens": 3520,
}


@pytest.fixture(autouse=True)
def no_network(monkeypatch):
    real_send = httpx.AsyncClient.send

    async def guarded(self, request, **kwargs):
        if request.url.host != "openai.test":
            raise AssertionError(f"unexpected network call to {request.url.host}")
        return await real_send(self, request, **kwargs)

    monkeypatch.setattr(httpx.AsyncClient, "send", guarded)


class ManualClock:
    """Monotonic time that moves only when the code under test sleeps."""

    def __init__(self):
        self.now = 1000.0
        self.sleeps: list[float] = []

    def __call__(self) -> float:
        return self.now

    async def sleep(self, delay: float) -> None:
        self.sleeps.append(delay)
        self.now += delay


def settings(**changes) -> OpenAIAssistantSettings:
    values = {
        "api_key": KEY,
        "model": MODEL,
        "region": "global",
        "daily_budget_usd": Decimal("1"),
        "monthly_budget_usd": Decimal("10"),
        "prices": Prices(Decimal("0.10"), Decimal("0.50")),
    }
    values.update(changes)
    return OpenAIAssistantSettings(**values)


def adapter(*replies, transport=None, guard=None, **changes):
    scripted = transport or ScriptedTransport(*replies)
    clock = ManualClock()
    config = settings(**changes)
    http = HttpResponsesTransport(
        api_key=config.api_key,
        base_url=BASE_URL,
        timeout_seconds=config.timeout_seconds,
        max_attempts=config.max_attempts,
        client=httpx.AsyncClient(transport=scripted, base_url=BASE_URL),
        sleep=clock.sleep,
        clock=clock,
        jitter=lambda: 1.0,
    )
    interpreter = OpenAIAssistantInterpreter(config, transport=http, guard=guard, clock=clock)
    return interpreter, scripted, clock


def text_part(content):
    if not isinstance(content, str):
        content = json.dumps(content)
    return {"type": "output_text", "text": content, "annotations": []}


def message_item(*parts):
    return {"type": "message", "id": "msg_x", "role": "assistant", "content": list(parts)}


REASONING = {"type": "reasoning", "id": "rs_x", "summary": []}


def body(output, *, status="completed", model=MODEL, usage=USAGE, **extra) -> dict:
    data = {"id": "resp_never_read", "object": "response", "status": status, "model": model}
    data["output"] = output
    if usage is not None:
        data["usage"] = usage
    data.update(extra)
    return data


def reply(data: dict, status: int = 200, headers=None) -> httpx.Response:
    return httpx.Response(status, json=data, headers=headers)


def ok(content=EXPENSE, **kwargs) -> httpx.Response:
    return reply(body([REASONING, message_item(text_part(content))], **kwargs))


def error(status, *, code=None, param=None, headers=None) -> httpx.Response:
    detail = {"message": "provider secret detail about Pamela", "type": "invalid_request_error"}
    if code is not None:
        detail["code"] = code
    if param is not None:
        detail["param"] = param
    return httpx.Response(status, json={"error": detail}, headers=headers)


def run(interpreter, value=REQUEST):
    return asyncio.run(interpreter.interpret(value))


# ---- Request: stateless, private, read-only ----


def test_a_valid_answer_is_returned_as_the_protocol_interpretation():
    interpreter, transport, _ = adapter(ok())

    interpretation = run(interpreter)

    assert interpretation.model_dump(mode="json") == EXPENSE
    sent = transport.requests[0]
    assert (sent.method, str(sent.url)) == ("POST", URL)
    assert sent.headers["authorization"] == f"Bearer {KEY}"


def test_the_request_is_exactly_the_stateless_strict_structured_output_call():
    interpreter, transport, _ = adapter(ok())

    run(interpreter)

    assert transport.bodies() == [
        {
            "model": MODEL,
            "input": build_input(REQUEST),
            "store": False,
            "reasoning": {"effort": "low"},
            "max_output_tokens": 2000,
            "text": {
                "format": {
                    "type": "json_schema",
                    "name": "assistant_interpretation",
                    "strict": True,
                    "schema": PROVIDER_OUTPUT_SCHEMA,
                }
            },
        }
    ]


def test_no_remote_state_tool_identifier_or_media_is_ever_sent():
    interpreter, transport, _ = adapter(error(503), ok())

    run(interpreter)

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
            "prompt_cache_retention",
            "prompt_cache_options",
            "stream",
            "service_tier",
        ):
            assert key not in sent, key
        # Text only: a system message and one user message whose content is a string.
        assert [item["role"] for item in sent["input"]] == ["system", "user"]
        assert all(isinstance(item["content"], str) for item in sent["input"])


def test_the_user_turn_is_the_id_free_protocol_request_as_data():
    system, user = build_input(REQUEST)

    assert system["content"] == SYSTEM_PROMPT
    assert json.loads(user["content"]) == REQUEST.model_dump(mode="json")
    lowered = user["content"].lower()
    for marker in ("_id", '"id"', "user", "account_name", "balance"):
        assert marker not in lowered, marker


# ---- Model output ----


@pytest.mark.parametrize(
    "response",
    [
        pytest.param(
            ok(expense(amount={"state": "stated", "value": "7", "span": "7 euro"})), id="protocol"
        ),
        pytest.param(ok({**EXPENSE, "confidence": 0.9}), id="schema_extra_field"),
        pytest.param(ok({k: v for k, v in EXPENSE.items() if k != "intent"}), id="schema_missing"),
        pytest.param(ok("{not json"), id="invalid_json"),
        pytest.param(ok(""), id="empty_text"),
        pytest.param(reply(body([REASONING])), id="no_message"),
        pytest.param(reply(body([message_item()])), id="no_content"),
        pytest.param(reply(body([])), id="empty_output"),
        pytest.param(reply(body(None)), id="null_output"),
        pytest.param(
            reply(body([message_item(text_part(EXPENSE), text_part(EXPENSE))])), id="two_parts"
        ),
        pytest.param(
            reply(body([{"type": "function_call", "name": "x", "arguments": "{}"}])),
            id="tool_call",
        ),
        pytest.param(
            reply(
                body(
                    [message_item(text_part('{"protocol_version": 1, "inte'))],
                    status="incomplete",
                    incomplete_details={"reason": "max_output_tokens"},
                )
            ),
            id="truncated",
        ),
        pytest.param(reply(body([], status="in_progress")), id="unexpected_status"),
    ],
)
def test_an_unusable_output_is_interpretation_failed_and_never_repaired(response):
    interpreter, transport, _ = adapter(response)

    with pytest.raises(InterpretationFailed) as failure:
        run(interpreter)

    assert (failure.value.status, failure.value.code) == (502, "interpretation_failed")
    assert len(transport.requests) == 1  # an output problem is never retried


def test_a_200_whose_body_is_not_json_is_unavailable_and_not_retried(caplog):
    interpreter, transport, _ = adapter(httpx.Response(200, text="<html>"))

    with caplog.at_level(logging.INFO, logger="lifeos_ai"), pytest.raises(ProviderUnavailable):
        run(interpreter)

    assert len(transport.requests) == 1
    assert "outcome=malformed_response" in caplog.text


def test_a_truncated_output_is_failed_with_its_own_outcome(caplog):
    truncated = reply(
        body(
            [message_item(text_part('{"protocol_version": 1'))],
            status="incomplete",
            incomplete_details={"reason": "max_output_tokens"},
        )
    )
    interpreter, _, _ = adapter(truncated)

    with caplog.at_level(logging.INFO, logger="lifeos_ai"), pytest.raises(InterpretationFailed):
        run(interpreter)

    assert "outcome=incomplete_max_output_tokens" in caplog.text


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
def test_a_refusal_is_interpretation_refused(response):
    interpreter, transport, _ = adapter(response)

    with pytest.raises(InterpretationRefused) as failure:
        run(interpreter)

    assert (failure.value.status, failure.value.code) == (502, "interpretation_refused")
    assert len(transport.requests) == 1


def test_a_failed_response_is_unavailable():
    interpreter, _, _ = adapter(reply(body([], status="failed")))

    with pytest.raises(ProviderUnavailable):
        run(interpreter)


def test_an_answer_from_another_model_is_never_accepted_silently(caplog):
    interpreter, _, _ = adapter(ok(model="gpt-other-model"))

    with caplog.at_level(logging.INFO, logger="lifeos_ai"), pytest.raises(ProviderUnavailable):
        run(interpreter)

    assert "outcome=model_mismatch" in caplog.text


def test_a_dated_snapshot_of_the_configured_model_is_accepted():
    interpreter, _, _ = adapter(ok(model=f"{MODEL}-2026-10-01"))

    assert run(interpreter).intent == "finance_expense"


@pytest.mark.parametrize(
    ("configured", "answered"),
    [
        ("gpt-6", "gpt-6-luna"),  # another model of the same family, not a snapshot
        ("gpt-6", "gpt-6.1-sol"),
        ("gpt-6-luna", "gpt-6-luna-mini"),
        ("gpt-6-luna", "gpt-6-luna-2026-10"),
        ("gpt-6-luna", "x-gpt-6-luna"),
    ],
)
def test_a_model_that_only_shares_the_configured_prefix_is_never_accepted(configured, answered):
    interpreter, _, _ = adapter(ok(model=answered), model=configured)

    with pytest.raises(ProviderUnavailable):
        run(interpreter)


# ---- HTTP failures: classification and retries ----


@pytest.mark.parametrize(
    ("response", "outcome"),
    [
        (error(400), "bad_request"),
        (error(400, param="model"), "bad_request_model"),
        (error(400, param="text.format.schema"), "bad_request"),
        (error(401), "unauthenticated"),
        (error(403), "forbidden"),
        (error(404), "not_found"),
        (error(422), "http_422"),
        (error(429, code="insufficient_quota"), "quota_exhausted"),
        (error(429, code="project_spend_limit_exceeded"), "quota_exhausted"),
        (error(429, code="credit_balance_exhausted"), "quota_exhausted"),
    ],
)
def test_final_errors_are_unavailable_and_never_retried(response, outcome, caplog):
    interpreter, transport, clock = adapter(response, max_attempts=3)

    with caplog.at_level(logging.INFO, logger="lifeos_ai"), pytest.raises(ProviderUnavailable):
        run(interpreter)

    assert len(transport.requests) == 1
    assert clock.sleeps == []
    assert f"outcome={outcome} " in caplog.text and "attempts=1" in caplog.text


@pytest.mark.parametrize(
    "first",
    [
        pytest.param(error(408), id="408"),
        pytest.param(error(429), id="429"),
        pytest.param(error(500), id="500"),
        pytest.param(error(502), id="502"),
        pytest.param(error(503), id="503"),
        pytest.param(error(504), id="504"),
        pytest.param(httpx.ReadTimeout("synthetic"), id="read_timeout"),
        pytest.param(httpx.ConnectTimeout("synthetic"), id="connect_timeout"),
        pytest.param(httpx.ConnectError("synthetic"), id="connect_error"),
        pytest.param(httpx.RemoteProtocolError("synthetic"), id="connection_dropped"),
        pytest.param(httpx.ReadError("synthetic"), id="read_error"),
    ],
)
def test_transient_failures_are_retried_with_bounded_backoff(first):
    interpreter, transport, clock = adapter(first, ok())

    assert run(interpreter).intent == "finance_expense"
    assert len(transport.requests) == 2
    assert len(clock.sleeps) == 1 and 0 < clock.sleeps[0] <= 4.0


@pytest.mark.parametrize(
    ("failure", "outcome"),
    [
        (error(429), "rate_limited"),
        (error(408), "timeout"),
        (httpx.ReadTimeout("synthetic"), "timeout"),
        (error(500), "server_error_500"),
        (error(502), "server_error_502"),
        (error(503), "server_error_503"),
        (httpx.RemoteProtocolError("synthetic"), "connection_error"),
    ],
)
def test_attempts_are_capped_and_each_failure_keeps_its_outcome(failure, outcome, caplog):
    interpreter, transport, _ = adapter(failure, failure, failure, max_attempts=3)

    with caplog.at_level(logging.INFO, logger="lifeos_ai"), pytest.raises(ProviderUnavailable):
        run(interpreter)

    assert len(transport.requests) == 3
    assert f"outcome={outcome} " in caplog.text and "attempts=3" in caplog.text


def test_backoff_is_exponential_with_jitter_and_capped():
    clock = ManualClock()
    transport = HttpResponsesTransport(
        api_key=KEY,
        base_url=BASE_URL,
        timeout_seconds=20,
        max_attempts=6,
        client=httpx.AsyncClient(
            transport=ScriptedTransport(*[error(503)] * 5, ok()), base_url=BASE_URL
        ),
        sleep=clock.sleep,
        clock=clock,
        jitter=lambda: 0.0,
    )

    asyncio.run(transport.create_response({}, deadline=clock.now + 300))

    # base 0.5 s doubling, capped at 4 s, scaled by a jitter factor in [0.5, 1.0] (here 0.5).
    assert clock.sleeps == [0.25, 0.5, 1.0, 2.0, 2.0]


def test_retry_after_is_honoured_as_a_minimum():
    interpreter, _, clock = adapter(error(429, headers={"retry-after": "3"}), ok())

    run(interpreter)

    assert clock.sleeps == [3.0]


def test_no_retry_whose_wait_would_outlive_the_deadline(caplog):
    # Retry-After 60 s with a 40 s deadline: give up at once instead of waiting.
    interpreter, transport, clock = adapter(
        error(429, headers={"retry-after": "60"}), ok(), max_attempts=3
    )

    with caplog.at_level(logging.INFO, logger="lifeos_ai"), pytest.raises(ProviderUnavailable):
        run(interpreter)

    assert len(transport.requests) == 1 and clock.sleeps == []
    assert "outcome=rate_limited" in caplog.text


def test_every_attempt_fits_the_total_deadline_far_inside_the_turn_lease():
    class SlowTimeouts(httpx.AsyncBaseTransport):
        def __init__(self, clock):
            self.clock = clock
            self.timeouts: list[float] = []

        async def handle_async_request(self, request):
            timeout = request.extensions["timeout"]["read"]
            self.timeouts.append(timeout)
            self.clock.now += timeout  # the attempt uses its whole timeout
            raise httpx.ReadTimeout("synthetic")

    clock = ManualClock()
    slow = SlowTimeouts(clock)
    config = settings(timeout_seconds=20, deadline_seconds=40, max_attempts=3)
    transport = HttpResponsesTransport(
        api_key=KEY,
        base_url=BASE_URL,
        timeout_seconds=config.timeout_seconds,
        max_attempts=config.max_attempts,
        client=httpx.AsyncClient(transport=slow, base_url=BASE_URL),
        sleep=clock.sleep,
        clock=clock,
        jitter=lambda: 1.0,
    )
    interpreter = OpenAIAssistantInterpreter(config, transport=transport, clock=clock)
    started = clock.now

    with pytest.raises(ProviderUnavailable):
        run(interpreter)

    assert clock.now - started <= 40  # well under the 45 s .NET timeout and the 90 s lease
    assert all(timeout <= 20 for timeout in slow.timeouts)
    assert len(slow.timeouts) <= 3


def test_an_attempt_is_bounded_as_a_whole_not_only_per_read(caplog):
    # A server that keeps the connection busy (e.g. trickling bytes) never trips an httpx
    # per-read timeout; the attempt itself must still end at its timeout.
    class Stalling(httpx.AsyncBaseTransport):
        def __init__(self):
            self.count = 0

        async def handle_async_request(self, request):
            self.count += 1
            await asyncio.sleep(5)
            return ok()

    stalling = Stalling()
    transport = HttpResponsesTransport(
        api_key=KEY,
        base_url=BASE_URL,
        timeout_seconds=0.05,
        max_attempts=1,
        client=httpx.AsyncClient(transport=stalling, base_url=BASE_URL),
    )
    interpreter = OpenAIAssistantInterpreter(
        settings(timeout_seconds=0.05, max_attempts=1), transport=transport
    )

    async def scenario():
        loop = asyncio.get_running_loop()
        started = loop.time()
        with pytest.raises(ProviderUnavailable):
            await interpreter.interpret(REQUEST)
        return loop.time() - started

    with caplog.at_level(logging.INFO, logger="lifeos_ai"):
        elapsed = asyncio.run(scenario())

    assert elapsed < 2 and stalling.count == 1
    assert "outcome=timeout " in caplog.text


# ---- Cancellation and concurrency ----


class GatedTransport(httpx.AsyncBaseTransport):
    """Holds every request until released; records how many were in flight at once."""

    def __init__(self, reply_factory=ok):
        self.gate = asyncio.Event()
        self.started = asyncio.Event()
        self.in_flight = 0
        self.peak = 0
        self.count = 0
        self.reply_factory = reply_factory

    async def handle_async_request(self, request):
        self.count += 1
        self.in_flight += 1
        self.peak = max(self.peak, self.in_flight)
        self.started.set()
        try:
            await self.gate.wait()
        finally:
            self.in_flight -= 1
        return self.reply_factory()


def test_cancellation_propagates_and_releases_the_slot_and_the_log_says_so(caplog):
    async def scenario():
        gated = GatedTransport()
        interpreter, _, _ = adapter(transport=gated, max_concurrency=1)
        task = asyncio.create_task(interpreter.interpret(REQUEST))
        await gated.started.wait()
        task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await task
        # The slot is free again: a new interpretation proceeds.
        gated.gate.set()
        return await interpreter.interpret(REQUEST)

    with caplog.at_level(logging.INFO, logger="lifeos_ai"):
        interpretation = asyncio.run(scenario())

    assert interpretation.intent == "finance_expense"
    assert "outcome=cancelled" in caplog.text


def test_two_concurrent_interpretations_both_complete_within_the_concurrency_limit():
    async def scenario(limit):
        gated = GatedTransport()
        interpreter, _, _ = adapter(transport=gated, max_concurrency=limit)
        tasks = [asyncio.create_task(interpreter.interpret(REQUEST)) for _ in range(2)]
        await gated.started.wait()
        await asyncio.sleep(0)
        gated.gate.set()
        results = await asyncio.gather(*tasks)
        return results, gated

    results, gated = asyncio.run(scenario(1))
    assert [r.intent for r in results] == ["finance_expense"] * 2
    assert gated.peak == 1 and gated.count == 2

    results, gated = asyncio.run(scenario(2))
    assert gated.peak == 2 and gated.count == 2


def test_no_free_slot_before_the_deadline_is_unavailable(caplog):
    async def scenario():
        gated = GatedTransport()
        interpreter, _, _ = adapter(transport=gated, max_concurrency=1, deadline_seconds=0.05)
        # The manual clock never moves here, so give the waiting call a real short deadline.
        interpreter._clock = asyncio.get_running_loop().time
        first = asyncio.create_task(interpreter.interpret(REQUEST))
        await gated.started.wait()
        with pytest.raises(ProviderUnavailable):
            await interpreter.interpret(REQUEST)
        gated.gate.set()
        await first
        return gated

    with caplog.at_level(logging.INFO, logger="lifeos_ai"):
        gated = asyncio.run(scenario())

    assert gated.count == 1
    assert "outcome=concurrency_limit" in caplog.text


# ---- Budget ----


def test_an_exhausted_budget_sends_nothing(caplog):
    guard = SpendGuard(daily_budget=Decimal("0.000001"), monthly_budget=Decimal("1"))
    interpreter, transport, _ = adapter(ok(), guard=guard)

    with caplog.at_level(logging.INFO, logger="lifeos_ai"), pytest.raises(ProviderUnavailable):
        run(interpreter)

    assert transport.requests == []
    assert "outcome=budget_exhausted_daily" in caplog.text


def test_the_reported_usage_replaces_the_reservation():
    guard = SpendGuard(daily_budget=Decimal("1"), monthly_budget=Decimal("10"))
    interpreter, _, _ = adapter(ok(), guard=guard)

    run(interpreter)

    # 3100 input at 0.10/M + 420 output at 0.50/M
    assert guard.spent() == (Decimal("0.00052"), Decimal("0.00052"))


def test_a_retried_success_still_counts_the_worst_case_of_the_earlier_attempt():
    # The reported usage covers only the answering attempt; the timed-out first one may have
    # been billed too.
    guard = SpendGuard(daily_budget=Decimal("1"), monthly_budget=Decimal("10"))
    interpreter, _, _ = adapter(httpx.ReadTimeout("synthetic"), ok(), guard=guard)

    run(interpreter)

    one_attempt = interpreter.worst_case_cost(interpreter.request_body(REQUEST)) / 2
    assert guard.spent()[0] == Decimal("0.00052") + one_attempt


def test_a_failure_after_sending_keeps_the_conservative_reservation():
    guard = SpendGuard(daily_budget=Decimal("1"), monthly_budget=Decimal("10"))
    interpreter, _, _ = adapter(error(503), error(503), guard=guard)

    with pytest.raises(ProviderUnavailable):
        run(interpreter)

    reserved = interpreter.worst_case_cost(interpreter.request_body(REQUEST))
    assert guard.spent() == (reserved, reserved)


def test_the_worst_case_cost_bounds_every_attempt_and_the_full_output_allowance():
    interpreter, _, _ = adapter()
    body = interpreter.request_body(REQUEST)
    size = len(json.dumps(body, ensure_ascii=False).encode("utf-8"))

    expected = (Decimal(size) * Decimal("0.10") + Decimal(2000) * Decimal("0.50")) / 1_000_000
    assert interpreter.worst_case_cost(body) == expected * 2


# ---- Logs ----


def test_logs_never_carry_user_data_output_provider_text_or_the_key(caplog):
    scripts = [
        ok(),
        error(400),
        error(401),
        error(503),
        ok(expense(amount={"state": "stated", "value": "7", "span": "7 euro"})),
        reply(body([message_item({"type": "refusal", "refusal": "Pamela refused"})])),
    ]
    with caplog.at_level(logging.DEBUG):
        for script in scripts:
            interpreter, _, _ = adapter(script, max_attempts=1)
            try:
                run(interpreter)
            except (InterpretationFailed, InterpretationRefused, ProviderUnavailable):
                pass

    text = caplog.text
    assert "outcome=ok" in text and "input_tokens=3100" in text
    for secret in (
        MESSAGE,
        "Pamela",
        "colazione",
        "Ristoranti",
        "Stipendio",
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


# ---- Strict Structured Outputs subset ----

# The subset documented by the Structured Outputs guide (read 2026-10-25).
SUPPORTED_KEYWORDS = {
    "type",
    "properties",
    "required",
    "additionalProperties",
    "items",
    "enum",
    "anyOf",
    "description",
    "$ref",
    "$defs",
    "pattern",
    "format",
    "minimum",
    "maximum",
    "exclusiveMinimum",
    "exclusiveMaximum",
    "multipleOf",
    "minItems",
    "maxItems",
}
SUPPORTED_TYPES = {"string", "number", "integer", "boolean", "object", "array", "null"}


def walk(schema, depth=0):
    yield schema, depth
    for child in schema.get("properties", {}).values():
        yield from walk(child, depth + 1)
    if "items" in schema:
        yield from walk(schema["items"], depth + 1)


def test_the_provider_schema_uses_only_the_documented_strict_subset():
    nodes = list(walk(PROVIDER_OUTPUT_SCHEMA))
    root = PROVIDER_OUTPUT_SCHEMA

    assert root["type"] == "object" and "anyOf" not in root
    for node, _ in nodes:
        assert set(node) <= SUPPORTED_KEYWORDS, set(node) - SUPPORTED_KEYWORDS
        types = node["type"] if isinstance(node["type"], list) else [node["type"]]
        assert set(types) <= SUPPORTED_TYPES
        if "object" in types:
            assert node["additionalProperties"] is False
            assert node["required"] == list(node["properties"])
        if "enum" in node:
            # Documented: string enums, with null only inside a nullable string union.
            assert set(types) <= {"string", "null"}
            assert all(isinstance(value, str) or value is None for value in node["enum"])
            assert (None in node["enum"]) == ("null" in types)
    properties = sum(len(node.get("properties", {})) for node, _ in nodes)
    enum_values = sum(len(node.get("enum", [])) for node, _ in nodes)
    assert max(depth for _, depth in nodes) <= 10
    assert properties <= 5000 and enum_values <= 1000


def test_the_provider_schema_differs_from_the_canonical_only_by_the_integer_enum():
    expected = json.loads(json.dumps(INTERPRETATION_JSON_SCHEMA))
    expected["properties"]["protocol_version"] = {"type": "integer"}

    assert PROVIDER_OUTPUT_SCHEMA == expected
    assert INTERPRETATION_JSON_SCHEMA["properties"]["protocol_version"] == {
        "type": "integer",
        "enum": [1],
    }
    assert provider_output_schema() == PROVIDER_OUTPUT_SCHEMA


def test_a_version_the_provider_schema_allows_is_still_refused_by_the_protocol():
    interpreter, _, _ = adapter(ok({**EXPENSE, "protocol_version": 2}))

    with pytest.raises(InterpretationFailed):
        run(interpreter)
