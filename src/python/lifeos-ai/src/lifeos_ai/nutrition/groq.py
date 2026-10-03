"""Groq adapter over its OpenAI-compatible Chat Completions API (plain HTTPS, no SDK).

Generation settings follow the configuration proven in AI-EVAL-002/003: strict
json_schema output, temperature 0, low reasoning effort without returned reasoning,
finite timeouts and a bounded number of attempts.
"""

import asyncio
import logging
import math
import time
from collections.abc import Awaitable, Callable
from dataclasses import dataclass
from datetime import UTC, datetime
from email.utils import parsedate_to_datetime

import httpx
from pydantic import ValidationError

from lifeos_ai.nutrition.estimator import EstimationFailed, ProviderUnavailable
from lifeos_ai.nutrition.prompt import PROMPT_VERSION, build_messages
from lifeos_ai.nutrition.schema import (
    OUTPUT_SCHEMA,
    EstimateMealRequest,
    NutritionEstimate,
    ProviderNutritionOutput,
)

GROQ_BASE_URL = "https://api.groq.com/openai/v1"
DEFAULT_MODEL = "openai/gpt-oss-20b"

# Transient: rate limiting and server-side failures. Anything else is final.
RETRYABLE_STATUS = frozenset({429, 500, 502, 503, 504})

logger = logging.getLogger("lifeos_ai.nutrition")


@dataclass(frozen=True)
class GroqSettings:
    api_key: str
    model: str = DEFAULT_MODEL
    base_url: str = GROQ_BASE_URL
    timeout_seconds: float = 15.0
    max_attempts: int = 3
    max_retry_wait_seconds: float = 4.0
    max_completion_tokens: int = 2048


class GroqNutritionEstimator:
    provider = "groq"
    configured = True

    def __init__(
        self,
        settings: GroqSettings,
        *,
        client: httpx.AsyncClient | None = None,
        sleep: Callable[[float], Awaitable[None]] = asyncio.sleep,
    ):
        if not settings.api_key.strip():
            raise ValueError("GROQ_API_KEY is required")
        if settings.max_attempts < 1:
            raise ValueError("max_attempts must be at least 1")
        self.model = settings.model
        self.prompt_version = PROMPT_VERSION
        self._settings = settings
        self._client = client or httpx.AsyncClient(
            base_url=settings.base_url, timeout=settings.timeout_seconds
        )
        self._sleep = sleep

    def request_body(self, request: EstimateMealRequest) -> dict:
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
                    "name": "nutrition_estimate",
                    "strict": True,
                    "schema": OUTPUT_SCHEMA,
                },
            },
        }

    async def estimate(self, request: EstimateMealRequest) -> NutritionEstimate:
        body = self.request_body(request)
        headers = {"Authorization": f"Bearer {self._settings.api_key}"}
        started = time.monotonic()
        attempts = self._settings.max_attempts

        for attempt in range(1, attempts + 1):
            try:
                response = await self._client.post(
                    "chat/completions",
                    json=body,
                    headers=headers,
                    timeout=self._settings.timeout_seconds,
                )
            except httpx.TimeoutException:
                outcome, delay = "timeout", self._backoff(attempt)
            except httpx.TransportError:
                outcome, delay = "transport_error", self._backoff(attempt)
            else:
                if response.status_code == 200:
                    estimate = self._parse(response, attempt, started)
                    self._log("estimated", attempt, started)
                    return estimate
                if response.status_code not in RETRYABLE_STATUS:
                    self._log(f"http_{response.status_code}", attempt, started)
                    # 400: the request or the generated output was rejected (e.g. schema
                    # validation). Credential or model problems make the provider unusable.
                    if response.status_code == 400:
                        raise EstimationFailed("provider rejected the estimation request")
                    raise ProviderUnavailable(f"provider returned {response.status_code}")
                outcome = f"http_{response.status_code}"
                delay = self._retry_delay(response, attempt)

            if attempt == attempts or delay is None:
                self._log(outcome, attempt, started)
                raise ProviderUnavailable(f"provider unavailable ({outcome})")
            await self._sleep(delay)

        raise AssertionError("unreachable")  # pragma: no cover

    async def aclose(self) -> None:
        await self._client.aclose()

    def _backoff(self, attempt: int) -> float:
        return min(float(2 ** (attempt - 1)), self._settings.max_retry_wait_seconds)

    def _retry_delay(self, response: httpx.Response, attempt: int) -> float | None:
        # Honour Retry-After, but never wait longer than the cap: give up instead.
        header = response.headers.get("retry-after")
        if header is None:
            return self._backoff(attempt)
        try:
            delay = float(header)
        except ValueError:
            try:
                delay = (parsedate_to_datetime(header) - datetime.now(UTC)).total_seconds()
            except (TypeError, ValueError, OverflowError):
                return self._backoff(attempt)
        if not math.isfinite(delay) or delay > self._settings.max_retry_wait_seconds:
            return None
        return max(0.0, delay)

    def _parse(self, response: httpx.Response, attempt: int, started: float) -> NutritionEstimate:
        try:
            choice = response.json()["choices"][0]
            content = choice["message"]["content"]
            if choice.get("finish_reason") not in (None, "stop"):
                raise ValueError("incomplete completion")
            output = ProviderNutritionOutput.model_validate_json(content)
        except (ValueError, KeyError, IndexError, TypeError, ValidationError) as error:
            self._log("invalid_output", attempt, started)
            raise EstimationFailed("provider output is not a valid estimate") from error

        if output.status != "estimated":
            self._log("not_estimable", attempt, started)
            raise EstimationFailed("the meal could not be estimated")

        return NutritionEstimate.model_validate(output.model_dump(exclude={"status"}))

    def _log(self, outcome: str, attempts: int, started: float) -> None:
        # Diagnostics only: never the meal text, the prompt or the provider response body.
        logger.info(
            "nutrition estimate outcome=%s provider=%s model=%s prompt=%s attempts=%d seconds=%.3f",
            outcome,
            self.provider,
            self.model,
            self.prompt_version,
            attempts,
            time.monotonic() - started,
        )
