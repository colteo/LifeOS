"""AI-007 F3: the provider-neutral assistant interpretation port and its failure kinds.

The default interpreter is `DisabledInterpreter`, which never makes a network call. The OpenAI
adapter (`openai_interpreter.OpenAIAssistantInterpreter`: Responses API, store=false, strict
structured output) implements `AssistantInterpreter`, holds its answer to the same protocol rules
and is selected only by explicit configuration (config.py).

AI-008: an interpreter answers both protocol versions. A version-1 request gets a version-1
interpretation (schema.py, the configured prompt); a version-2 request a version-2 one
(schema_v2.py, `prompt_version_v2`).
"""

import json
from typing import Protocol

from pydantic import ValidationError

from lifeos_ai.assistant import schema_v2
from lifeos_ai.assistant.prompt import PROMPT_V3_VERSION, PROMPT_VERSION
from lifeos_ai.assistant.schema import (
    AssistantInterpretation,
    InterpretRequest,
    ProtocolViolation,
    validate_interpretation,
)
from lifeos_ai.errors import ProviderUnavailable, ServiceError

__all__ = [
    "PROMPT_VERSION",
    "AssistantInterpreter",
    "DisabledInterpreter",
    "InterpretationFailed",
    "InterpretationRefused",
    "ProviderUnavailable",
    "parse_provider_output",
    "prompt_version_for",
]

AnyRequest = InterpretRequest | schema_v2.InterpretRequest
AnyInterpretation = AssistantInterpretation | schema_v2.AssistantInterpretation


class InterpretationFailed(ServiceError):
    """The provider answered, but not with a valid interpretation (malformed or breaking rules)."""

    code = "interpretation_failed"
    status = 502


class InterpretationRefused(ServiceError):
    """The provider refused to interpret the message."""

    code = "interpretation_refused"
    status = 502


class AssistantInterpreter(Protocol):
    provider: str
    model: str
    # The prompt of protocol-1 requests; `prompt_version_v2` the one of protocol-2 requests.
    prompt_version: str

    @property
    def configured(self) -> bool: ...

    async def interpret(self, request: AnyRequest) -> AnyInterpretation: ...

    async def aclose(self) -> None: ...


class DisabledInterpreter:
    """The default interpreter: every interpretation is unavailable, no traffic."""

    provider = "disabled"
    model = "-"
    prompt_version = PROMPT_VERSION
    prompt_version_v2 = PROMPT_V3_VERSION
    configured = False

    async def interpret(self, request: AnyRequest) -> AnyInterpretation:
        raise ProviderUnavailable("the assistant provider is disabled")

    async def aclose(self) -> None:
        return None


def prompt_version_for(interpreter: AssistantInterpreter, protocol_version: int) -> str:
    """The prompt an interpreter answers a request of this protocol version with."""
    if protocol_version == schema_v2.PROTOCOL_VERSION:
        return getattr(interpreter, "prompt_version_v2", PROMPT_V3_VERSION)
    return interpreter.prompt_version


def parse_provider_output(
    raw: str | dict, message: str, protocol_version: int = 1
) -> AnyInterpretation:
    """The provider's structured output, held to the protocol; InterpretationFailed otherwise.

    The failure carries no provider text: only that the output was unusable.
    """
    model, validate = (
        (schema_v2.AssistantInterpretation, schema_v2.validate_interpretation)
        if protocol_version == schema_v2.PROTOCOL_VERSION
        else (AssistantInterpretation, validate_interpretation)
    )
    try:
        data = json.loads(raw) if isinstance(raw, str) else raw
        interpretation = model.model_validate(data)
        validate(interpretation, message)
    except (json.JSONDecodeError, ValidationError, ProtocolViolation, TypeError) as failure:
        raise InterpretationFailed("the provider output is not a valid interpretation") from failure
    return interpretation
