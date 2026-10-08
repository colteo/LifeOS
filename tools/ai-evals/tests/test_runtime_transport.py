"""ObservingTransport over content-encoded provider responses: the body is decoded
exactly once, for telemetry and for the production client."""

import asyncio
import gzip
import json
import zlib

import httpx
import pytest
from live_fakes import usage

from lifeos_ai_evals.production.runtime import ObservingTransport, Telemetry, client

ENCODERS = {"gzip": gzip.compress, "deflate": zlib.compress}
WEEKLY_REVIEW = {
    "choices": [
        {
            "message": {"role": "assistant", "content": '{"summary": "ok"}'},
            "finish_reason": "stop",
        }
    ],
    "usage": usage(),
}
ACTION_AGENT = {
    "choices": [
        {
            "message": {
                "role": "assistant",
                "content": None,
                "tool_calls": [
                    {
                        "id": "call_x",
                        "type": "function",
                        "function": {"name": "get_weekly_review", "arguments": "{}"},
                    }
                ],
            },
            "finish_reason": "tool_calls",
        }
    ],
    "usage": usage(prompt=1200, completion=40),
}


class WireStream(httpx.AsyncByteStream):
    """Raw (still encoded) bytes in chunks, as a network transport yields them."""

    def __init__(self, raw: bytes):
        self._raw = raw

    async def __aiter__(self):
        for start in range(0, len(self._raw), 16):
            yield self._raw[start : start + 16]


class EncodedTransport(httpx.AsyncBaseTransport):
    """An unread streaming response with Content-Encoding, like the real transport."""

    def __init__(self, body: dict, encoding: str):
        self.payload = json.dumps(body).encode()
        self._encoding = encoding

    async def handle_async_request(self, request: httpx.Request) -> httpx.Response:
        raw = ENCODERS[self._encoding](self.payload)
        return httpx.Response(
            200,
            headers={
                "content-type": "application/json",
                "content-encoding": self._encoding,
                "content-length": str(len(raw)),
            },
            stream=WireStream(raw),
        )

    async def aclose(self) -> None:
        return None


@pytest.mark.parametrize("encoding", sorted(ENCODERS))
def test_encoded_response_is_observed_and_returned_unchanged(encoding):
    inner = EncodedTransport(WEEKLY_REVIEW, encoding)
    telemetry = Telemetry()
    transport = ObservingTransport(telemetry, inner)
    request = httpx.Request("POST", "https://example.test/chat/completions")

    response = asyncio.run(transport.handle_async_request(request))

    assert response.headers["content-encoding"] == encoding
    assert response.content == inner.payload
    assert response.json() == WEEKLY_REVIEW
    [attempt] = telemetry.attempts
    assert attempt.status == "http_200"
    assert attempt.usage == {
        "input_tokens": 900,
        "output_tokens": 120,
        "total_tokens": 1020,
    }
    assert attempt.tool_names == ()


@pytest.mark.parametrize(
    ("body", "tool_names", "tokens"),
    [
        (WEEKLY_REVIEW, (), 1020),
        (ACTION_AGENT, ("get_weekly_review",), 1240),
    ],
)
def test_production_client_reads_encoded_response_once(body, tool_names, tokens):
    inner = EncodedTransport(body, "gzip")
    telemetry = Telemetry()

    async def call():
        async with client(telemetry, inner) as http:
            response = await http.post("/chat/completions", json={})
            return response.status_code, response.content, response.json()

    status, content, decoded = asyncio.run(call())

    assert status == 200
    assert content == inner.payload
    assert decoded == body
    [attempt] = telemetry.attempts
    assert attempt.status == "http_200"
    assert attempt.usage["total_tokens"] == tokens
    assert attempt.tool_names == tool_names
    assert telemetry.summary()["attempt_status_counts"] == {"http_200": 1}
