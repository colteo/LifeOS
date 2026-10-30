"""AI-009 I1: the provider-neutral Quick Capture port and its disabled default.

The default is `DisabledQuickCaptureInterpreter`, which never makes a network call: every endpoint
answers 503 `provider_unavailable`. The OpenAI adapter (`openai_interpreter`) implements the same
port; config.py refuses to select it until ADR-015 and the ADR-014 addendum are accepted.
"""

from typing import Protocol

from lifeos_ai.errors import ProviderUnavailable
from lifeos_ai.quick_capture.prompt import DEFAULT_PROMPTS
from lifeos_ai.quick_capture.router import RouteRequest, RouterOutput
from lifeos_ai.quick_capture.specialists import SpecialistInterpretation, SpecialistRequest


class QuickCaptureInterpreter(Protocol):
    provider: str
    model: str
    # The prompt version of each role: "router", "finance", "hydration", "meal".
    prompt_versions: dict[str, str]

    @property
    def configured(self) -> bool: ...

    async def route(self, request: RouteRequest) -> RouterOutput: ...

    async def interpret(self, request: SpecialistRequest) -> SpecialistInterpretation: ...

    async def aclose(self) -> None: ...


class DisabledQuickCaptureInterpreter:
    """The default: every routing and interpretation is unavailable, no traffic."""

    provider = "disabled"
    model = "-"
    configured = False

    def __init__(self):
        self.prompt_versions = dict(DEFAULT_PROMPTS)

    async def route(self, request: RouteRequest) -> RouterOutput:
        raise ProviderUnavailable("the quick capture provider is disabled")

    async def interpret(self, request: SpecialistRequest) -> SpecialistInterpretation:
        raise ProviderUnavailable("the quick capture provider is disabled")

    async def aclose(self) -> None:
        return None
