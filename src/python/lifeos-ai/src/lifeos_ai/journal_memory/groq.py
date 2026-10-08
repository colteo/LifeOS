"""AI-004: Groq grounded answers over the shared Chat Completions transport (groq_chat.py).

Same generation policy as the other Groq capabilities (ADR-011): strict json_schema output,
temperature 0, low reasoning effort without returned reasoning, bounded attempts. The answer is
validated twice here: the schema (JournalAnswer) and citation membership (only labels the request
supplied). LifeOS validates both again.
"""

import asyncio
import logging
import time
from collections.abc import Awaitable, Callable

import httpx
from pydantic import ValidationError

from lifeos_ai.groq_chat import (
    CompletionFailed,
    GroqChatCompletions,
    GroqSettings,
    TokenUsage,
    usage_log_fields,
)
from lifeos_ai.journal_memory.answer import AnswerFailed, ProviderUnavailable
from lifeos_ai.journal_memory.prompt import PROMPT_VERSION, build_messages
from lifeos_ai.journal_memory.schema import ANSWER_OUTPUT_SCHEMA, AnswerRequest, JournalAnswer

logger = logging.getLogger("lifeos_ai.journal_memory")


class GroqJournalAnswerer:
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

    def request_body(self, request: AnswerRequest) -> dict:
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
                    "name": "journal_answer",
                    "strict": True,
                    "schema": ANSWER_OUTPUT_SCHEMA,
                },
            },
        }

    async def answer(self, request: AnswerRequest) -> JournalAnswer:
        started = time.monotonic()
        sources = len(request.sources)
        try:
            completion = await self._chat.complete(self.request_body(request))
        except CompletionFailed as failure:
            result = "unavailable" if failure.unavailable else "invalid"
            self._log(result, failure.outcome, sources, failure.attempts, started, failure.usage)
            if failure.unavailable:
                raise ProviderUnavailable(f"provider unavailable ({failure.outcome})") from None
            raise AnswerFailed("provider output is not a valid answer") from None

        try:
            answer = JournalAnswer.model_validate_json(completion.content)
        except ValidationError:
            self._log(
                "invalid", "invalid_output", sources, completion.attempts, started, completion.usage
            )
            raise AnswerFailed("provider output is not a valid answer") from None

        if not answer.cites_only(request.labels()):
            self._log(
                "invalid",
                "unknown_citation",
                sources,
                completion.attempts,
                started,
                completion.usage,
            )
            raise AnswerFailed("the answer cites a source that was not supplied")

        self._log("success", answer.status, sources, completion.attempts, started, completion.usage)
        return answer

    async def aclose(self) -> None:
        await self._chat.aclose()

    def _log(
        self,
        result: str,
        outcome: str,
        sources: int,
        attempts: int,
        started: float,
        usage: TokenUsage | None,
    ) -> None:
        # Diagnostics only: never the question, the passages, the answer or reasoning.
        logger.info(
            "journal answer result=%s outcome=%s provider=%s model=%s prompt=%s source_count=%d "
            "attempts=%d seconds=%.3f %s",
            result,
            outcome,
            self.provider,
            self.model,
            self.prompt_version,
            sources,
            attempts,
            time.monotonic() - started,
            usage_log_fields(usage),
        )
