"""FastAPI transport. Stateless: no database, no users, no LifeOS identifiers.

Run: uv run python -m uvicorn lifeos_ai.app:create_app --factory --host 127.0.0.1 --port 8000
(LIFEOS_AI_SERVICE_KEY is required; see README.md.)
"""

import logging
from contextlib import asynccontextmanager

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse

from lifeos_ai.action_agent.agent import ActionAgentModel
from lifeos_ai.action_agent.schema import (
    OUTPUT_VERSION as ACTION_AGENT_OUTPUT_VERSION,
)
from lifeos_ai.action_agent.schema import (
    ActionAgentStepRequest,
    ActionAgentStepResponse,
)
from lifeos_ai.auth import ServiceKeyGuard
from lifeos_ai.config import (
    action_agent_from_environment,
    journal_answerer_from_environment,
    journal_embedder_from_environment,
    nutrition_estimator_from_environment,
    service_key_from_environment,
    validate_service_key,
    weekly_review_interpreter_from_environment,
)
from lifeos_ai.errors import ServiceError
from lifeos_ai.journal_memory.answer import JournalAnswerer
from lifeos_ai.journal_memory.chunking import CHUNKING_VERSION
from lifeos_ai.journal_memory.embedding import JournalEmbedder
from lifeos_ai.journal_memory.indexing import embed_query, index_entry
from lifeos_ai.journal_memory.schema import (
    OUTPUT_VERSION as JOURNAL_MEMORY_OUTPUT_VERSION,
)
from lifeos_ai.journal_memory.schema import (
    AnswerRequest,
    AnswerResponse,
    EmbedQueryRequest,
    EmbedQueryResponse,
    IndexEntryRequest,
    IndexEntryResponse,
)
from lifeos_ai.nutrition.estimator import NutritionEstimator
from lifeos_ai.nutrition.schema import EstimateMealRequest, NutritionEstimate
from lifeos_ai.weekly_review.interpreter import WeeklyReviewInterpreter
from lifeos_ai.weekly_review.schema import (
    OUTPUT_VERSION,
    InterpretWeeklyReviewRequest,
    InterpretWeeklyReviewResponse,
)

logger = logging.getLogger("lifeos_ai")


def error(status: int, code: str) -> JSONResponse:
    return JSONResponse(status_code=status, content={"error": {"code": code}})


def create_app(
    estimator: NutritionEstimator | None = None,
    *,
    interpreter: WeeklyReviewInterpreter | None = None,
    agent: ActionAgentModel | None = None,
    embedder: JournalEmbedder | None = None,
    answerer: JournalAnswerer | None = None,
    service_key: str | None = None,
) -> FastAPI:
    # First, so a missing or weak key stops the service before anything else is created.
    guard = ServiceKeyGuard(
        validate_service_key(service_key)
        if service_key is not None
        else service_key_from_environment()
    )
    nutrition = estimator or nutrition_estimator_from_environment()
    weekly_review = interpreter or weekly_review_interpreter_from_environment()
    action_agent = agent or action_agent_from_environment()
    journal_embedding = embedder or journal_embedder_from_environment()
    journal_answer = answerer or journal_answerer_from_environment()

    @asynccontextmanager
    async def lifespan(_: FastAPI):
        yield
        await nutrition.aclose()
        await weekly_review.aclose()
        await action_agent.aclose()
        await journal_embedding.aclose()
        await journal_answer.aclose()

    app = FastAPI(
        title="LifeOS AI",
        version="0.1.0",
        lifespan=lifespan,
        docs_url=None,
        redoc_url=None,
        openapi_url=None,
    )

    # Everything except /health/live requires the service key (auth.py), before any validation.
    app.middleware("http")(guard)

    # The default 422 body echoes the submitted input (meal text, review figures); fields only.
    @app.exception_handler(RequestValidationError)
    async def invalid_request(_: Request, exc: RequestValidationError) -> JSONResponse:
        fields = sorted({".".join(str(part) for part in e["loc"][1:]) for e in exc.errors()})
        return JSONResponse(
            status_code=422, content={"error": {"code": "invalid_request", "fields": fields}}
        )

    # Public liveness for Render's health check and the external keepalive: no provider call,
    # no configuration, no identity.
    @app.get("/health/live")
    async def live() -> dict:
        return {"status": "ok"}

    # Detailed health: protected by the service key like the estimate endpoint.
    @app.get("/health")
    async def health() -> dict:
        return {
            "status": "ok",
            "nutrition": {
                "provider": nutrition.provider,
                "model": nutrition.model,
                "prompt_version": nutrition.prompt_version,
                "configured": nutrition.configured,
            },
            "weekly_review": {
                "provider": weekly_review.provider,
                "model": weekly_review.model,
                "prompt_version": weekly_review.prompt_version,
                "configured": weekly_review.configured,
            },
            "action_agent": {
                "provider": action_agent.provider,
                "model": action_agent.model,
                "prompt_version": action_agent.prompt_version,
                "configured": action_agent.configured,
            },
            "journal_embedding": {
                "provider": journal_embedding.provider,
                "model": journal_embedding.model,
                "dimensions": journal_embedding.dimensions,
                "chunking_version": CHUNKING_VERSION,
                "configured": journal_embedding.configured,
            },
            "journal_answer": {
                "provider": journal_answer.provider,
                "model": journal_answer.model,
                "prompt_version": journal_answer.prompt_version,
                "configured": journal_answer.configured,
            },
        }

    @app.post("/v1/nutrition/estimate-meal", response_model=NutritionEstimate)
    async def estimate_meal(request: EstimateMealRequest):
        try:
            return await nutrition.estimate(request)
        except ServiceError as failure:
            return error(failure.status, failure.code)
        except Exception:
            # Unexpected: logged without the meal text; the caller sees a stable code only.
            logger.exception("unexpected nutrition estimation failure")
            return error(500, "internal_error")

    # AI-001: insights for one saved weekly review. Stateless: the figures arrive in the request,
    # the insights leave in the response, nothing is kept.
    @app.post("/v1/weekly-review/interpret", response_model=InterpretWeeklyReviewResponse)
    async def interpret_weekly_review(request: InterpretWeeklyReviewRequest):
        try:
            insights = await weekly_review.interpret(request)
        except ServiceError as failure:
            return error(failure.status, failure.code)
        except Exception:
            # Unexpected: logged without the review figures; the caller sees a stable code only.
            logger.exception("unexpected weekly review interpretation failure")
            return error(500, "internal_error")
        return InterpretWeeklyReviewResponse(
            output_version=OUTPUT_VERSION,
            provider=weekly_review.provider,
            model=weekly_review.model,
            prompt_version=weekly_review.prompt_version,
            insights=insights,
        )

    # AI-002: ONE step of the Action Agent. Stateless: LifeOS sends the tools it offers and the
    # results of the calls it executed; the decision leaves in the response. Nothing is executed
    # here and nothing is kept.
    @app.post("/v1/action-agent/step", response_model=ActionAgentStepResponse)
    async def action_agent_step(request: ActionAgentStepRequest):
        try:
            decision = await action_agent.step(request)
        except ServiceError as failure:
            return error(failure.status, failure.code)
        except Exception:
            # Unexpected: logged without the request data; the caller sees a stable code only.
            logger.exception("unexpected action agent failure")
            return error(500, "internal_error")
        return ActionAgentStepResponse(
            output_version=ACTION_AGENT_OUTPUT_VERSION,
            provider=action_agent.provider,
            model=action_agent.model,
            prompt_version=action_agent.prompt_version,
            decision=decision,
        )

    # AI-004: the Journal Memory Layer. Stateless: journal text arrives in the request, chunks,
    # vectors or one grounded answer leave in the response; nothing is kept and no LifeOS
    # identifier is ever received. Failures are stable codes only; logs carry no text or vectors.
    @app.post("/v1/journal-memory/index-entry", response_model=IndexEntryResponse)
    async def journal_memory_index_entry(request: IndexEntryRequest):
        try:
            return await index_entry(journal_embedding, request)
        except ServiceError as failure:
            return error(failure.status, failure.code)
        except Exception:
            logger.exception("unexpected journal indexing failure")
            return error(500, "internal_error")

    @app.post("/v1/journal-memory/embed-query", response_model=EmbedQueryResponse)
    async def journal_memory_embed_query(request: EmbedQueryRequest):
        try:
            return await embed_query(journal_embedding, request)
        except ServiceError as failure:
            return error(failure.status, failure.code)
        except Exception:
            logger.exception("unexpected journal query embedding failure")
            return error(500, "internal_error")

    @app.post("/v1/journal-memory/answer", response_model=AnswerResponse)
    async def journal_memory_answer(request: AnswerRequest):
        try:
            result = await journal_answer.answer(request)
        except ServiceError as failure:
            return error(failure.status, failure.code)
        except Exception:
            logger.exception("unexpected journal answer failure")
            return error(500, "internal_error")
        return AnswerResponse(
            output_version=JOURNAL_MEMORY_OUTPUT_VERSION,
            provider=journal_answer.provider,
            model=journal_answer.model,
            prompt_version=journal_answer.prompt_version,
            result=result,
        )

    return app
