"""AI-007 F3: the provider-neutral assistant interpretation port and its failure kinds.

F3-A ships no provider adapter: ADR-014 (external provider for the assistant) is PROPOSED / NOT
APPROVED, so the only interpreter that configuration can select is `DisabledInterpreter`, which
never makes a network call. A provider adapter (F3-B, OpenAI Responses API with store=false and
`INTERPRETATION_JSON_SCHEMA` as strict output) implements `AssistantInterpreter` and uses
`parse_provider_output` so its answer is held to the same protocol rules.
"""

import json
from typing import Protocol

from pydantic import ValidationError

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
]

# Reserved for the F3-B prompt; no prompt text exists yet (it is written and frozen together with
# the offline evaluation, after ADR-014 is accepted).
PROMPT_VERSION = "assistant-finance-interpret-v1"


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
    prompt_version: str

    @property
    def configured(self) -> bool: ...

    async def interpret(self, request: InterpretRequest) -> AssistantInterpretation: ...

    async def aclose(self) -> None: ...


class DisabledInterpreter:
    """The only selectable interpreter in F3-A: every interpretation is unavailable, no traffic."""

    provider = "disabled"
    model = "-"
    prompt_version = PROMPT_VERSION
    configured = False

    async def interpret(self, request: InterpretRequest) -> AssistantInterpretation:
        raise ProviderUnavailable("the assistant provider is disabled (ADR-014 not accepted)")

    async def aclose(self) -> None:
        return None


def parse_provider_output(raw: str | dict, message: str) -> AssistantInterpretation:
    """The provider's structured output, held to the protocol; InterpretationFailed otherwise.

    The failure carries no provider text: only that the output was unusable.
    """
    try:
        data = json.loads(raw) if isinstance(raw, str) else raw
        interpretation = AssistantInterpretation.model_validate(data)
        validate_interpretation(interpretation, message)
    except (json.JSONDecodeError, ValidationError, ProtocolViolation, TypeError) as failure:
        raise InterpretationFailed("the provider output is not a valid interpretation") from failure
    return interpretation
