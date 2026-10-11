"""AI-006: OpenAI grounded answers over the Responses API (plain HTTPS, no SDK), the journal answer
CANDIDATE adapter.

Selected only explicitly (LIFEOS_AI_JOURNAL_ANSWER_PROVIDER=openai, config.py); production keeps its
default answer provider until promotion. Transport policy: openai_http.py.

Every request is stateless and private:
- `store: false` is always sent (the Responses API stores responses unless told not to);
- no `previous_response_id`, no conversation, no tools (no web/file search, functions, code
  interpreter, MCP or vector store); the response id is never read or kept;
- the prompt is the production registry's (`journal-rag-answer-v1`), as the shared
  `build_messages` builds it for every provider: the same system text and JSON user payload;
- structured output is `text.format` json_schema with `strict: true` over the production
  ANSWER_OUTPUT_SCHEMA; `reasoning.effort` is medium; temperature/top_p are not sent.

The output is validated defensively anyway: the response must be `completed` with exactly one
assistant message holding exactly one `output_text` part. A refusal, an incomplete response, a
missing or extra part, malformed JSON, a schema-invalid answer and an unknown citation are each an
invalid output (502); a `failed` response is unavailable (503). LifeOS validates the answer again.

Logs: one line per request with the result, outcome code, identity, source count, attempts, latency
and token counts (input, cached input, output, reasoning, total). Never the question, the passages,
the answer, reasoning or the key.
"""

import asyncio
import logging
import time
from collections.abc import Awaitable, Callable
from dataclasses import dataclass

import httpx
from pydantic import ValidationError

from lifeos_ai.journal_memory.answer import AnswerFailed, ProviderUnavailable
from lifeos_ai.journal_memory.openai_http import (
    OPENAI_BASE_URL,
    OpenAITransport,
    ProviderFailure,
    usage_count,
)
from lifeos_ai.journal_memory.prompt import PROMPT_VERSION, build_messages
from lifeos_ai.journal_memory.schema import ANSWER_OUTPUT_SCHEMA, AnswerRequest, JournalAnswer

DEFAULT_ANSWER_MODEL = "gpt-6.1-sol"
REASONING_EFFORT = "medium"
SCHEMA_NAME = "journal_answer"

logger = logging.getLogger("lifeos_ai.journal_memory")


@dataclass(frozen=True)
class OpenAIAnswerSettings:
    api_key: str
    model: str = DEFAULT_ANSWER_MODEL
    base_url: str = OPENAI_BASE_URL
    # Medium reasoning needs more than the other adapters' 15 s; 3 attempts stay within the API's
    # AI-service timeout (NutritionAi:TimeoutSeconds, 120 s in Production).
    timeout_seconds: float = 30.0
    max_attempts: int = 3
    max_retry_wait_seconds: float = 4.0
    # Reasoning tokens count toward this bound; an answer is at most 1200 characters.
    max_output_tokens: int = 8192


@dataclass(frozen=True)
class ResponseUsage:
    """Token counts of one answered request; None when the provider did not report one."""

    input_tokens: int | None
    cached_input_tokens: int | None
    output_tokens: int | None
    reasoning_tokens: int | None
    total_tokens: int | None

    @classmethod
    def from_response(cls, response: httpx.Response) -> "ResponseUsage":
        try:
            usage = response.json().get("usage")
        except (ValueError, AttributeError):
            usage = None
        return cls(
            usage_count(usage, "input_tokens"),
            usage_count(usage, "input_tokens_details", "cached_tokens"),
            usage_count(usage, "output_tokens"),
            usage_count(usage, "output_tokens_details", "reasoning_tokens"),
            usage_count(usage, "total_tokens"),
        )

    def log_fields(self) -> str:
        return " ".join(
            f"{name}={'-' if count is None else count}"
            for name, count in (
                ("input_tokens", self.input_tokens),
                ("cached_input_tokens", self.cached_input_tokens),
                ("output_tokens", self.output_tokens),
                ("reasoning_tokens", self.reasoning_tokens),
                ("total_tokens", self.total_tokens),
            )
        )


_NO_USAGE = ResponseUsage(None, None, None, None, None)


class OpenAIJournalAnswerer:
    provider = "openai"
    configured = True

    def __init__(
        self,
        settings: OpenAIAnswerSettings,
        *,
        client: httpx.AsyncClient | None = None,
        sleep: Callable[[float], Awaitable[None]] = asyncio.sleep,
    ):
        self.model = settings.model
        self.prompt_version = PROMPT_VERSION
        self._settings = settings
        self._transport = OpenAITransport(
            api_key=settings.api_key,
            base_url=settings.base_url,
            timeout_seconds=settings.timeout_seconds,
            max_attempts=settings.max_attempts,
            max_retry_wait_seconds=settings.max_retry_wait_seconds,
            client=client,
            sleep=sleep,
        )

    def request_body(self, request: AnswerRequest) -> dict:
        return {
            "model": self.model,
            "input": build_messages(request),
            "store": False,
            "reasoning": {"effort": REASONING_EFFORT},
            "max_output_tokens": self._settings.max_output_tokens,
            "text": {
                "format": {
                    "type": "json_schema",
                    "name": SCHEMA_NAME,
                    "strict": True,
                    "schema": ANSWER_OUTPUT_SCHEMA,
                }
            },
        }

    async def answer(self, request: AnswerRequest) -> JournalAnswer:
        started = time.monotonic()
        sources = len(request.sources)
        usage = _NO_USAGE
        try:
            response, attempts = await self._transport.post("responses", self.request_body(request))
            usage = ResponseUsage.from_response(response)
            text = _output_text(response, attempts)
        except ProviderFailure as failure:
            result = "unavailable" if failure.unavailable else "invalid"
            self._log(result, failure.outcome, sources, failure.attempts, started, usage)
            if failure.unavailable:
                raise ProviderUnavailable(f"provider unavailable ({failure.outcome})") from None
            raise AnswerFailed("provider output is not a valid answer") from None

        try:
            answer = JournalAnswer.model_validate_json(text)
        except ValidationError:
            self._log("invalid", "invalid_output", sources, attempts, started, usage)
            raise AnswerFailed("provider output is not a valid answer") from None

        if not answer.cites_only(request.labels()):
            self._log("invalid", "unknown_citation", sources, attempts, started, usage)
            raise AnswerFailed("the answer cites a source that was not supplied")

        self._log("success", answer.status, sources, attempts, started, usage)
        return answer

    async def aclose(self) -> None:
        await self._transport.aclose()

    def _log(
        self,
        result: str,
        outcome: str,
        sources: int,
        attempts: int,
        started: float,
        usage: ResponseUsage,
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
            usage.log_fields(),
        )


def _output_text(response: httpx.Response, attempts: int) -> str:
    """The one structured-output text of a completed response; ProviderFailure otherwise.

    Reasoning items are skipped unread. Anything but exactly one assistant message with exactly one
    `output_text` part (a refusal, a tool call, a second message or part) is an invalid output."""

    def invalid(outcome: str) -> ProviderFailure:
        return ProviderFailure(outcome, attempts, unavailable=False)

    try:
        body = response.json()
    except ValueError:
        raise invalid("invalid_output") from None
    if not isinstance(body, dict):
        raise invalid("invalid_output")

    status = body.get("status")
    if status == "incomplete":
        raise invalid("incomplete")
    if status == "failed":
        raise ProviderFailure("response_failed", attempts, unavailable=True)
    if status != "completed":
        raise invalid("invalid_output")

    output = body.get("output")
    if not isinstance(output, list):
        raise invalid("missing_output")
    parts = []
    for item in output:
        if not isinstance(item, dict):
            raise invalid("invalid_output")
        if item.get("type") == "reasoning":
            continue
        if item.get("type") != "message" or item.get("role") != "assistant":
            raise invalid("unexpected_output_item")
        content = item.get("content")
        if not isinstance(content, list):
            raise invalid("invalid_output")
        parts.extend(content)

    if any(isinstance(part, dict) and part.get("type") == "refusal" for part in parts):
        raise invalid("refusal")
    if len(parts) != 1:
        raise invalid("missing_output" if not parts else "unexpected_output_item")
    part = parts[0]
    if not isinstance(part, dict) or part.get("type") != "output_text":
        raise invalid("unexpected_output_item")
    text = part.get("text")
    if not isinstance(text, str):
        raise invalid("missing_output")
    return text
