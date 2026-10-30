"""FastAPI transport. Stateless: no database, no users, no LifeOS identifiers.

Run: uv run python -m uvicorn lifeos_ai.app:create_app --factory --host 127.0.0.1 --port 8000
(LIFEOS_AI_SERVICE_KEY is required; see README.md.)
"""

import logging
from contextlib import asynccontextmanager
from typing import Annotated

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from pydantic import Field

from lifeos_ai.action_agent.agent import ActionAgentModel
from lifeos_ai.action_agent.schema import (
    OUTPUT_VERSION as ACTION_AGENT_OUTPUT_VERSION,
)
from lifeos_ai.action_agent.schema import (
    ActionAgentStepRequest,
    ActionAgentStepResponse,
)
from lifeos_ai.assistant import schema_v2
from lifeos_ai.assistant.interpreter import (
    AssistantInterpreter,
    InterpretationFailed,
    prompt_version_for,
)
from lifeos_ai.assistant.schema import (
    OUTPUT_VERSION as ASSISTANT_OUTPUT_VERSION,
)
from lifeos_ai.assistant.schema import (
    AssistantInterpretation,
    InterpretRequest,
    InterpretResponse,
    ProtocolViolation,
    validate_interpretation,
)
from lifeos_ai.auth import ServiceKeyGuard
from lifeos_ai.config import (
    action_agent_from_environment,
    assistant_interpreter_from_environment,
    journal_answerer_from_environment,
    journal_embedder_from_environment,
    nutrition_estimator_from_environment,
    quick_capture_interpreter_from_environment,
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
from lifeos_ai.quick_capture.common import OUTPUT_VERSION as QUICK_CAPTURE_OUTPUT_VERSION
from lifeos_ai.quick_capture.interpreter import QuickCaptureInterpreter
from lifeos_ai.quick_capture.router import RouteRequest, RouteResponse, RouterOutput, validate_route
from lifeos_ai.quick_capture.specialists import (
    FinanceInterpretation,
    FinanceInterpretRequest,
    FinanceInterpretResponse,
    HydrationInterpretation,
    HydrationInterpretRequest,
    HydrationInterpretResponse,
    MealInterpretation,
    MealInterpretRequest,
    MealInterpretResponse,
    validate_specialist,
)
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
    assistant: AssistantInterpreter | None = None,
    quick_capture: QuickCaptureInterpreter | None = None,
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
    assistant_interpreter = assistant or assistant_interpreter_from_environment()
    quick_capture_interpreter = quick_capture or quick_capture_interpreter_from_environment()

    @asynccontextmanager
    async def lifespan(_: FastAPI):
        yield
        await nutrition.aclose()
        await weekly_review.aclose()
        await action_agent.aclose()
        await journal_embedding.aclose()
        await journal_answer.aclose()
        await assistant_interpreter.aclose()
        await quick_capture_interpreter.aclose()

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
            "assistant": {
                "provider": assistant_interpreter.provider,
                "model": assistant_interpreter.model,
                "prompt_version": assistant_interpreter.prompt_version,
                "prompt_version_v2": prompt_version_for(assistant_interpreter, 2),
                "configured": assistant_interpreter.configured,
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

    # AI-007 F3: one LifeOS Assistant message → one structured interpretation. Stateless: the
    # message and a minimal, id-free context arrive in the request; nothing is kept. The output is
    # held to the protocol here and again by .NET, which alone resolves and applies it. Logs carry
    # the outcome code and identity only, never the message, the categories or the output.
    # AI-008: the request's protocol_version selects the protocol (1: Finance only, unchanged; 2:
    # Finance, water and meals); the answer is of the same version.
    @app.post(
        "/v1/assistant/interpret",
        response_model=InterpretResponse | schema_v2.InterpretResponse,
    )
    async def assistant_interpret(
        request: Annotated[
            InterpretRequest | schema_v2.InterpretRequest,
            Field(discriminator="protocol_version"),
        ],
    ):
        v2 = request.protocol_version == schema_v2.PROTOCOL_VERSION
        prompt_version = prompt_version_for(assistant_interpreter, request.protocol_version)
        try:
            interpretation = await assistant_interpreter.interpret(request)
            if v2:
                if not isinstance(interpretation, schema_v2.AssistantInterpretation):
                    raise ProtocolViolation("protocol_version")
                schema_v2.validate_interpretation(interpretation, request.message)
            else:
                if not isinstance(interpretation, AssistantInterpretation):
                    raise ProtocolViolation("protocol_version")
                validate_interpretation(interpretation, request.message)
        except ProtocolViolation:
            failure = InterpretationFailed()
            _log_assistant(assistant_interpreter, failure.code, prompt_version)
            return error(failure.status, failure.code)
        except ServiceError as failure:
            _log_assistant(assistant_interpreter, failure.code, prompt_version)
            return error(failure.status, failure.code)
        except Exception as unexpected:
            # The type only, no traceback: an exception's text could quote the user's message.
            logger.error(
                "unexpected assistant interpretation failure (%s)", type(unexpected).__name__
            )
            _log_assistant(assistant_interpreter, "internal_error", prompt_version)
            return error(500, "internal_error")
        _log_assistant(assistant_interpreter, "ok", prompt_version)
        response = schema_v2.InterpretResponse if v2 else InterpretResponse
        return response(
            output_version=schema_v2.OUTPUT_VERSION if v2 else ASSISTANT_OUTPUT_VERSION,
            provider=assistant_interpreter.provider,
            model=assistant_interpreter.model,
            prompt_version=prompt_version,
            interpretation=interpretation,
        )

    # AI-009 I1: Quick Capture (ADR-015, PROPOSED; disabled by default and in every environment).
    # The router splits one message into segments; a specialist interprets one segment. Stateless
    # and id-free like the assistant endpoint; every output is held to its contract here and again
    # by .NET, and refused whole when it breaks a rule. Logs: outcome, role and identity only.
    async def quick_capture_call(role: str, call, check, response_type):
        prompt_version = quick_capture_interpreter.prompt_versions[role]
        try:
            output = await call()
            check(output)
        except ProtocolViolation:
            failure = InterpretationFailed()
            _log_quick_capture(quick_capture_interpreter, role, failure.code, prompt_version)
            return error(failure.status, failure.code)
        except ServiceError as failure:
            _log_quick_capture(quick_capture_interpreter, role, failure.code, prompt_version)
            return error(failure.status, failure.code)
        except Exception as unexpected:
            # The type only, no traceback: an exception's text could quote the user's message.
            logger.error("unexpected quick capture failure (%s)", type(unexpected).__name__)
            _log_quick_capture(quick_capture_interpreter, role, "internal_error", prompt_version)
            return error(500, "internal_error")
        _log_quick_capture(quick_capture_interpreter, role, "ok", prompt_version)
        return response_type(
            output_version=QUICK_CAPTURE_OUTPUT_VERSION,
            provider=quick_capture_interpreter.provider,
            model=quick_capture_interpreter.model,
            prompt_version=prompt_version,
            interpretation=output,
        )

    def specialist_check(request, output_type):
        def check(output) -> None:
            if not isinstance(output, output_type):
                raise ProtocolViolation("shape")
            validate_specialist(output, request.mode, request.segment, request.context_spans)

        return check

    @app.post("/v1/quick-capture/route", response_model=RouteResponse)
    async def quick_capture_route(request: RouteRequest):
        def check(output) -> None:
            if not isinstance(output, RouterOutput):
                raise ProtocolViolation("shape")
            validate_route(output, request.message, request.context.max_segments)

        return await quick_capture_call(
            "router", lambda: quick_capture_interpreter.route(request), check, RouteResponse
        )

    @app.post("/v1/quick-capture/finance/interpret", response_model=FinanceInterpretResponse)
    async def quick_capture_finance(request: FinanceInterpretRequest):
        return await quick_capture_call(
            "finance",
            lambda: quick_capture_interpreter.interpret(request),
            specialist_check(request, FinanceInterpretation),
            FinanceInterpretResponse,
        )

    @app.post("/v1/quick-capture/hydration/interpret", response_model=HydrationInterpretResponse)
    async def quick_capture_hydration(request: HydrationInterpretRequest):
        return await quick_capture_call(
            "hydration",
            lambda: quick_capture_interpreter.interpret(request),
            specialist_check(request, HydrationInterpretation),
            HydrationInterpretResponse,
        )

    @app.post("/v1/quick-capture/meal/interpret", response_model=MealInterpretResponse)
    async def quick_capture_meal(request: MealInterpretRequest):
        return await quick_capture_call(
            "meal",
            lambda: quick_capture_interpreter.interpret(request),
            specialist_check(request, MealInterpretation),
            MealInterpretResponse,
        )

    return app


def _log_quick_capture(
    interpreter: QuickCaptureInterpreter, role: str, outcome: str, prompt_version: str
) -> None:
    logger.info(
        "quick capture outcome=%s role=%s provider=%s model=%s prompt_version=%s",
        outcome,
        role,
        interpreter.provider,
        interpreter.model,
        prompt_version,
    )


def _log_assistant(interpreter: AssistantInterpreter, outcome: str, prompt_version: str) -> None:
    logger.info(
        "assistant interpretation outcome=%s provider=%s model=%s prompt_version=%s",
        outcome,
        interpreter.provider,
        interpreter.model,
        prompt_version,
    )
