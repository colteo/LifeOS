"""FastAPI transport. Stateless: no database, no users, no LifeOS identifiers.

Run: uv run uvicorn lifeos_ai.app:create_app --factory --host 127.0.0.1 --port 8000
"""

import logging
from contextlib import asynccontextmanager

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse

from lifeos_ai.config import nutrition_estimator_from_environment
from lifeos_ai.nutrition.estimator import (
    EstimationError,
    EstimationFailed,
    NutritionEstimator,
    ProviderUnavailable,
)
from lifeos_ai.nutrition.schema import EstimateMealRequest, NutritionEstimate

logger = logging.getLogger("lifeos_ai")

# Stable error codes; provider messages and exception details never leave the service.
ERROR_STATUS = {ProviderUnavailable: 503, EstimationFailed: 502}


def error(status: int, code: str) -> JSONResponse:
    return JSONResponse(status_code=status, content={"error": {"code": code}})


def create_app(estimator: NutritionEstimator | None = None) -> FastAPI:
    nutrition = estimator or nutrition_estimator_from_environment()

    @asynccontextmanager
    async def lifespan(_: FastAPI):
        yield
        await nutrition.aclose()

    app = FastAPI(
        title="LifeOS AI",
        version="0.1.0",
        lifespan=lifespan,
        docs_url=None,
        redoc_url=None,
        openapi_url=None,
    )

    # The default 422 body echoes the submitted input (the meal text); report fields only.
    @app.exception_handler(RequestValidationError)
    async def invalid_request(_: Request, exc: RequestValidationError) -> JSONResponse:
        fields = sorted({".".join(str(part) for part in e["loc"][1:]) for e in exc.errors()})
        return JSONResponse(
            status_code=422, content={"error": {"code": "invalid_request", "fields": fields}}
        )

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
        }

    @app.post("/v1/nutrition/estimate-meal", response_model=NutritionEstimate)
    async def estimate_meal(request: EstimateMealRequest):
        try:
            return await nutrition.estimate(request)
        except EstimationError as failure:
            return error(ERROR_STATUS.get(type(failure), 503), failure.code)
        except Exception:
            # Unexpected: logged without the meal text; the caller sees a stable code only.
            logger.exception("unexpected nutrition estimation failure")
            return error(500, "internal_error")

    return app
