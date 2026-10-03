"""Offline fakes: a scripted Groq HTTP transport and a recording sleep. No network."""

import json

import httpx

from lifeos_ai.nutrition.groq import GroqNutritionEstimator, GroqSettings

VALID_OUTPUT = {
    "status": "estimated",
    "calories_kcal": 620,
    "protein_grams": 52.5,
    "carbs_grams": 58,
    "fat_grams": 20,
    "assumptions": ["about 180 g chicken breast", "about 250 g potatoes"],
}


def completion(content, finish_reason="stop") -> httpx.Response:
    if not isinstance(content, str):
        content = json.dumps(content)
    return httpx.Response(
        200,
        json={
            "choices": [
                {
                    "message": {"role": "assistant", "content": content},
                    "finish_reason": finish_reason,
                }
            ]
        },
    )


class ScriptedTransport(httpx.AsyncBaseTransport):
    """Replies with the scripted responses in order; an exception item is raised instead."""

    def __init__(self, *replies):
        self.replies = list(replies)
        self.requests: list[httpx.Request] = []

    async def handle_async_request(self, request: httpx.Request) -> httpx.Response:
        self.requests.append(request)
        reply = self.replies.pop(0)
        if isinstance(reply, Exception):
            raise reply
        return reply

    def bodies(self) -> list[dict]:
        return [json.loads(request.content) for request in self.requests]


class RecordingSleep:
    def __init__(self):
        self.delays: list[float] = []

    async def __call__(self, delay: float) -> None:
        self.delays.append(delay)


def estimator(*replies, **settings):
    transport = ScriptedTransport(*replies)
    sleep = RecordingSleep()
    groq = GroqNutritionEstimator(
        GroqSettings(api_key="test-key", base_url="https://groq.test/openai/v1", **settings),
        client=httpx.AsyncClient(transport=transport, base_url="https://groq.test/openai/v1"),
        sleep=sleep,
    )
    return groq, transport, sleep
