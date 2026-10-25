"""Environment-only configuration. Secrets are never read from files or logged."""

import os
from collections.abc import Mapping

from lifeos_ai.action_agent.agent import ActionAgentModel, UnconfiguredAgent
from lifeos_ai.action_agent.groq import GroqActionAgent
from lifeos_ai.action_agent.prompt import PROMPT_VERSION as ACTION_AGENT_PROMPT_VERSION
from lifeos_ai.assistant.interpreter import AssistantInterpreter, DisabledInterpreter
from lifeos_ai.journal_memory.answer import JournalAnswerer, UnconfiguredAnswerer
from lifeos_ai.journal_memory.embedding import JournalEmbedder, UnconfiguredEmbedder
from lifeos_ai.journal_memory.gemini_embeddings import (
    DEFAULT_EMBEDDING_MODEL,
    EMBEDDING_DIMENSIONS,
    GeminiEmbeddingSettings,
    GeminiJournalEmbedder,
)
from lifeos_ai.journal_memory.groq import GroqJournalAnswerer
from lifeos_ai.journal_memory.openai_answer import (
    DEFAULT_ANSWER_MODEL as OPENAI_ANSWER_MODEL,
)
from lifeos_ai.journal_memory.openai_answer import (
    OpenAIAnswerSettings,
    OpenAIJournalAnswerer,
)
from lifeos_ai.journal_memory.openai_embeddings import (
    DEFAULT_EMBEDDING_MODEL as OPENAI_EMBEDDING_MODEL,
)
from lifeos_ai.journal_memory.openai_embeddings import (
    OpenAIEmbeddingSettings,
    OpenAIJournalEmbedder,
)
from lifeos_ai.journal_memory.prompt import PROMPT_VERSION as JOURNAL_ANSWER_PROMPT_VERSION
from lifeos_ai.nutrition.estimator import NutritionEstimator, UnconfiguredEstimator
from lifeos_ai.nutrition.groq import DEFAULT_MODEL, GroqNutritionEstimator, GroqSettings
from lifeos_ai.nutrition.prompt import PROMPT_VERSION
from lifeos_ai.weekly_review.groq import GroqWeeklyReviewInterpreter
from lifeos_ai.weekly_review.interpreter import UnconfiguredInterpreter, WeeklyReviewInterpreter
from lifeos_ai.weekly_review.prompt import PROMPT_VERSION as WEEKLY_REVIEW_PROMPT_VERSION

API_KEY_VARIABLE = "GROQ_API_KEY"
MODEL_VARIABLE = "LIFEOS_AI_NUTRITION_MODEL"
WEEKLY_REVIEW_MODEL_VARIABLE = "LIFEOS_AI_WEEKLY_REVIEW_MODEL"
ACTION_AGENT_MODEL_VARIABLE = "LIFEOS_AI_ACTION_AGENT_MODEL"

# AI-004: journal embeddings (Google Gemini) and grounded answers (Groq), kept for an explicit
# rollback/test selection (AI-006.1). Without the selected provider's key the service still starts
# and that journal capability reports configured=false.
GEMINI_API_KEY_VARIABLE = "GEMINI_API_KEY"
JOURNAL_EMBEDDING_MODEL_VARIABLE = "LIFEOS_AI_JOURNAL_EMBEDDING_MODEL"
JOURNAL_ANSWER_MODEL_VARIABLE = "LIFEOS_AI_JOURNAL_ANSWER_MODEL"

# AI-006.1: the promoted AI-006 stack is the journal default (OpenAI text-embedding-3-small / 1536
# and gpt-6.1-sol). One provider per capability; google/groq only when selected explicitly. A
# selected (or default) provider without its key is unconfigured (503), never a fallback to another
# provider. Journal only: Nutrition, Weekly Review and the Action Agent stay on Groq.
OPENAI_API_KEY_VARIABLE = "OPENAI_API_KEY"
JOURNAL_EMBEDDING_PROVIDER_VARIABLE = "LIFEOS_AI_JOURNAL_EMBEDDING_PROVIDER"
JOURNAL_ANSWER_PROVIDER_VARIABLE = "LIFEOS_AI_JOURNAL_ANSWER_PROVIDER"
JOURNAL_EMBEDDING_PROVIDERS = ("google", "openai")
JOURNAL_ANSWER_PROVIDERS = ("groq", "openai")
DEFAULT_JOURNAL_EMBEDDING_PROVIDER = "openai"
DEFAULT_JOURNAL_ANSWER_PROVIDER = "openai"

# AI-007 F3-A: the LifeOS Assistant interpreter. ADR-014 (external provider for the assistant) is
# PROPOSED / NOT APPROVED, so "disabled" is the only value: no assistant text is sent anywhere, and
# selecting a provider (e.g. "openai") stops the service instead of enabling it. F3-B adds the
# approved provider here.
ASSISTANT_PROVIDER_VARIABLE = "LIFEOS_AI_ASSISTANT_PROVIDER"
ASSISTANT_PROVIDERS = ("disabled",)
DEFAULT_ASSISTANT_PROVIDER = "disabled"

# PROD-AI-001: the secret shared with the LifeOS API (NutritionAi__ServiceKey on the .NET side).
SERVICE_KEY_VARIABLE = "LIFEOS_AI_SERVICE_KEY"
MIN_SERVICE_KEY_LENGTH = 32


class ServiceKeyError(RuntimeError):
    """The service key is missing or too weak. The message names the variable, never a value."""


class ProviderSelectionError(RuntimeError):
    """An unknown provider was selected: the service does not start rather than guess one."""


def _provider(environ: Mapping[str, str], variable: str, allowed: tuple, default: str) -> str:
    provider = environ.get(variable, "").strip().lower() or default
    if provider not in allowed:
        raise ProviderSelectionError(f"{variable} must be one of: {', '.join(allowed)}.")
    return provider


def nutrition_estimator_from_environment(
    environ: Mapping[str, str] = os.environ,
) -> NutritionEstimator:
    model = environ.get(MODEL_VARIABLE, "").strip() or DEFAULT_MODEL
    api_key = environ.get(API_KEY_VARIABLE, "").strip()
    if not api_key:
        return UnconfiguredEstimator(provider="groq", model=model, prompt_version=PROMPT_VERSION)
    return GroqNutritionEstimator(GroqSettings(api_key=api_key, model=model))


def weekly_review_interpreter_from_environment(
    environ: Mapping[str, str] = os.environ,
) -> WeeklyReviewInterpreter:
    model = environ.get(WEEKLY_REVIEW_MODEL_VARIABLE, "").strip() or DEFAULT_MODEL
    api_key = environ.get(API_KEY_VARIABLE, "").strip()
    if not api_key:
        return UnconfiguredInterpreter(
            provider="groq", model=model, prompt_version=WEEKLY_REVIEW_PROMPT_VERSION
        )
    return GroqWeeklyReviewInterpreter(GroqSettings(api_key=api_key, model=model))


def action_agent_from_environment(
    environ: Mapping[str, str] = os.environ,
) -> ActionAgentModel:
    model = environ.get(ACTION_AGENT_MODEL_VARIABLE, "").strip() or DEFAULT_MODEL
    api_key = environ.get(API_KEY_VARIABLE, "").strip()
    if not api_key:
        return UnconfiguredAgent(
            provider="groq", model=model, prompt_version=ACTION_AGENT_PROMPT_VERSION
        )
    return GroqActionAgent(GroqSettings(api_key=api_key, model=model))


def journal_embedder_from_environment(
    environ: Mapping[str, str] = os.environ,
) -> JournalEmbedder:
    provider = _provider(
        environ,
        JOURNAL_EMBEDDING_PROVIDER_VARIABLE,
        JOURNAL_EMBEDDING_PROVIDERS,
        DEFAULT_JOURNAL_EMBEDDING_PROVIDER,
    )
    if provider == "openai":
        model = environ.get(JOURNAL_EMBEDDING_MODEL_VARIABLE, "").strip() or OPENAI_EMBEDDING_MODEL
        api_key = environ.get(OPENAI_API_KEY_VARIABLE, "").strip()
        if not api_key:
            return UnconfiguredEmbedder(
                provider="openai", model=model, dimensions=EMBEDDING_DIMENSIONS
            )
        return OpenAIJournalEmbedder(OpenAIEmbeddingSettings(api_key=api_key, model=model))

    model = environ.get(JOURNAL_EMBEDDING_MODEL_VARIABLE, "").strip() or DEFAULT_EMBEDDING_MODEL
    api_key = environ.get(GEMINI_API_KEY_VARIABLE, "").strip()
    if not api_key:
        return UnconfiguredEmbedder(provider="google", model=model, dimensions=EMBEDDING_DIMENSIONS)
    return GeminiJournalEmbedder(GeminiEmbeddingSettings(api_key=api_key, model=model))


def journal_answerer_from_environment(
    environ: Mapping[str, str] = os.environ,
) -> JournalAnswerer:
    provider = _provider(
        environ,
        JOURNAL_ANSWER_PROVIDER_VARIABLE,
        JOURNAL_ANSWER_PROVIDERS,
        DEFAULT_JOURNAL_ANSWER_PROVIDER,
    )
    if provider == "openai":
        model = environ.get(JOURNAL_ANSWER_MODEL_VARIABLE, "").strip() or OPENAI_ANSWER_MODEL
        api_key = environ.get(OPENAI_API_KEY_VARIABLE, "").strip()
        if not api_key:
            return UnconfiguredAnswerer(
                provider="openai", model=model, prompt_version=JOURNAL_ANSWER_PROMPT_VERSION
            )
        return OpenAIJournalAnswerer(OpenAIAnswerSettings(api_key=api_key, model=model))

    model = environ.get(JOURNAL_ANSWER_MODEL_VARIABLE, "").strip() or DEFAULT_MODEL
    api_key = environ.get(API_KEY_VARIABLE, "").strip()
    if not api_key:
        return UnconfiguredAnswerer(
            provider="groq", model=model, prompt_version=JOURNAL_ANSWER_PROMPT_VERSION
        )
    return GroqJournalAnswerer(GroqSettings(api_key=api_key, model=model))


def assistant_interpreter_from_environment(
    environ: Mapping[str, str] = os.environ,
) -> AssistantInterpreter:
    _provider(environ, ASSISTANT_PROVIDER_VARIABLE, ASSISTANT_PROVIDERS, DEFAULT_ASSISTANT_PROVIDER)
    return DisabledInterpreter()


def validate_service_key(key: str | None) -> str:
    """Returns the key; raises ServiceKeyError so the service cannot start unauthenticated."""
    key = (key or "").strip()
    if not key:
        raise ServiceKeyError(f"{SERVICE_KEY_VARIABLE} is required.")
    if len(key) < MIN_SERVICE_KEY_LENGTH or not all("!" <= char <= "~" for char in key):
        raise ServiceKeyError(
            f"{SERVICE_KEY_VARIABLE} must be at least {MIN_SERVICE_KEY_LENGTH} visible ASCII "
            "characters without spaces."
        )
    return key


def service_key_from_environment(environ: Mapping[str, str] = os.environ) -> str:
    return validate_service_key(environ.get(SERVICE_KEY_VARIABLE))
