"""Offline Groq HTTP fakes for the live adapters (no network, no key)."""

import json

import httpx


class ScriptedTransport(httpx.AsyncBaseTransport):
    """Replies in order; an exception item is raised instead. Records request bodies."""

    def __init__(self, *replies):
        self.replies = list(replies)
        self.bodies: list[dict] = []

    async def handle_async_request(self, request: httpx.Request) -> httpx.Response:
        self.bodies.append(json.loads(request.content))
        reply = self.replies.pop(0)
        if isinstance(reply, Exception):
            raise reply
        return reply

    async def aclose(self) -> None:
        return None


def usage(prompt=900, completion=120):
    return {
        "prompt_tokens": prompt,
        "completion_tokens": completion,
        "total_tokens": prompt + completion,
    }


def content(value, *, tokens=None) -> httpx.Response:
    body = {
        "choices": [
            {
                "message": {"role": "assistant", "content": json.dumps(value)},
                "finish_reason": "stop",
            }
        ]
    }
    if tokens is not None:
        body["usage"] = tokens
    return httpx.Response(200, json=body)


def tool_call(name, arguments, *, tokens=None) -> httpx.Response:
    body = {
        "choices": [
            {
                "message": {
                    "role": "assistant",
                    "content": None,
                    "tool_calls": [
                        {
                            "id": "call_x",
                            "type": "function",
                            "function": {
                                "name": name,
                                "arguments": json.dumps(arguments),
                            },
                        }
                    ],
                },
                "finish_reason": "tool_calls",
            }
        ]
    }
    if tokens is not None:
        body["usage"] = tokens
    return httpx.Response(200, json=body)


async def no_sleep(_delay):
    return None
