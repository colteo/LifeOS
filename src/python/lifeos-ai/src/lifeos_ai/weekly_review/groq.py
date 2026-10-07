"""Groq weekly-review interpreter over the shared Chat Completions transport.

Same generation policy as nutrition estimation (ADR-011): strict json_schema output,
temperature 0, low reasoning effort without returned reasoning, bounded attempts.
"""

import asyncio
import logging
import time
from collections.abc import Awaitable, Callable

import httpx
from pydantic import ValidationError

from lifeos_ai.groq_chat import CompletionFailed, GroqChatCompletions, GroqSettings
from lifeos_ai.weekly_review.interpreter import InterpretationFailed, ProviderUnavailable
from lifeos_ai.weekly_review.prompt import PROMPT_VERSION, build_messages
from lifeos_ai.weekly_review.schema import (
    OUTPUT_SCHEMA,
    InterpretWeeklyReviewRequest,
    WeeklyReviewInsights,
)

logger = logging.getLogger("lifeos_ai.weekly_review")


class GroqWeeklyReviewInterpreter:
    provider = "groq"
    configured = True

    def __init__(
        self,
        settings: GroqSettings,
        *,
        client: httpx.AsyncClient | None = None,
        sleep: Callable[[float], Awaitable[None]] = asyncio.sleep,
    ):
        self.model = settings.model
        self.prompt_version = PROMPT_VERSION
        self._settings = settings
        self._chat = GroqChatCompletions(settings, client=client, sleep=sleep)

    def request_body(self, request: InterpretWeeklyReviewRequest) -> dict:
        return {
            "model": self.model,
            "messages": build_messages(request),
            "temperature": 0,
            "max_completion_tokens": self._settings.max_completion_tokens,
            "reasoning_effort": "low",
            "include_reasoning": False,
            "response_format": {
                "type": "json_schema",
                "json_schema": {
                    "name": "weekly_review_insights",
                    "strict": True,
                    "schema": OUTPUT_SCHEMA,
                },
            },
        }

    async def interpret(self, request: InterpretWeeklyReviewRequest) -> WeeklyReviewInsights:
        started = time.monotonic()
        try:
            completion = await self._chat.complete(self.request_body(request))
        except CompletionFailed as failure:
            self._log(failure.outcome, failure.attempts, started)
            if failure.unavailable:
                raise ProviderUnavailable(f"provider unavailable ({failure.outcome})") from None
            raise InterpretationFailed("provider output is not valid insights") from None

        try:
            insights = WeeklyReviewInsights.model_validate_json(completion.content)
        except ValidationError as error:
            # Nothing partial is ever returned: one invalid field rejects the whole answer.
            self._log("invalid_output", completion.attempts, started)
            raise InterpretationFailed("provider output is not valid insights") from error

        self._log("interpreted", completion.attempts, started)
        return insights

    async def aclose(self) -> None:
        await self._chat.aclose()

    def _log(self, outcome: str, attempts: int, started: float) -> None:
        # Diagnostics only: never the snapshot, the prompt or the provider response body.
        logger.info(
            "weekly review interpretation outcome=%s provider=%s model=%s prompt=%s attempts=%d "
            "seconds=%.3f",
            outcome,
            self.provider,
            self.model,
            self.prompt_version,
            attempts,
            time.monotonic() - started,
        )
