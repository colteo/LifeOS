from fastapi.testclient import TestClient

from lifeos_ai.app import create_app
from lifeos_ai.config import nutrition_estimator_from_environment
from lifeos_ai.nutrition.estimator import EstimationFailed, ProviderUnavailable
from lifeos_ai.nutrition.schema import EstimateMealRequest, NutritionEstimate

ESTIMATE_PATH = "/v1/nutrition/estimate-meal"

# Synthetic, test-only service key (never a real secret).
SERVICE_KEY = "test-service-key-0123456789abcdefghijklmnop"


class FakeEstimator:
    provider = "fake"
    model = "fake-model"
    prompt_version = "nutrition-estimation-v1"
    configured = True

    def __init__(self, outcome=None):
        self.outcome = outcome
        self.requests: list[EstimateMealRequest] = []
        self.closed = False

    async def estimate(self, request):
        self.requests.append(request)
        if isinstance(self.outcome, Exception):
            raise self.outcome
        return NutritionEstimate(
            calories_kcal=620,
            protein_grams=52,
            carbs_grams=58,
            fat_grams=20,
            assumptions=["about 180 g chicken breast"],
        )

    async def aclose(self):
        self.closed = True


def client(estimator):
    """An authenticated caller, as the LifeOS API is."""
    return TestClient(
        create_app(estimator, service_key=SERVICE_KEY),
        headers={"Authorization": f"Bearer {SERVICE_KEY}"},
    )


def test_authenticated_health_reports_identity_but_no_secrets(monkeypatch):
    monkeypatch.setenv("GROQ_API_KEY", "secret-test-key")
    monkeypatch.delenv("OPENAI_API_KEY", raising=False)
    estimator = nutrition_estimator_from_environment()

    with client(estimator) as http:
        response = http.get("/health")

    assert response.status_code == 200
    assert response.json() == {
        "status": "ok",
        "nutrition": {
            "provider": "groq",
            "model": "openai/gpt-oss-20b",
            "prompt_version": "nutrition-estimation-v1",
            "configured": True,
        },
        "weekly_review": {
            "provider": "groq",
            "model": "openai/gpt-oss-20b",
            "prompt_version": "weekly-review-insights-v1",
            "configured": True,
        },
        "action_agent": {
            "provider": "groq",
            "model": "openai/gpt-oss-20b",
            "prompt_version": "action-agent-v1",
            "configured": True,
        },
        # AI-004: no OPENAI_API_KEY here, so journal embeddings are reported unconfigured.
        "journal_embedding": {
            "provider": "openai",
            "model": "text-embedding-3-small",
            "dimensions": 1536,
            "chunking_version": "journal-chunking-v1",
            "configured": False,
        },
        "journal_answer": {
            "provider": "groq",
            "model": "openai/gpt-oss-20b",
            "prompt_version": "journal-rag-answer-v1",
            "configured": True,
        },
    }
    assert "secret-test-key" not in response.text


def test_estimate_returns_the_estimate_and_sends_only_meal_data():
    fake = FakeEstimator()

    with client(fake) as http:
        response = http.post(
            ESTIMATE_PATH, json={"description": " Pollo con le patate ", "meal_type": "Lunch"}
        )

    assert response.status_code == 200
    assert response.json() == {
        "calories_kcal": 620.0,
        "protein_grams": 52.0,
        "carbs_grams": 58.0,
        "fat_grams": 20.0,
        "assumptions": ["about 180 g chicken breast"],
    }
    assert fake.requests == [
        EstimateMealRequest(description="Pollo con le patate", meal_type="Lunch")
    ]
    assert fake.closed


def test_invalid_requests_are_422_without_echoing_the_meal_text():
    fake = FakeEstimator()

    with client(fake) as http:
        response = http.post(ESTIMATE_PATH, json={"description": "secret meal", "user_id": "u-1"})
        missing = http.post(ESTIMATE_PATH, json={})

    assert response.status_code == 422
    assert response.json() == {"error": {"code": "invalid_request", "fields": ["user_id"]}}
    assert "secret meal" not in response.text
    assert missing.json()["error"]["fields"] == ["description"]
    assert fake.requests == []


def test_provider_unavailable_is_503_with_a_stable_code():
    with client(FakeEstimator(ProviderUnavailable("Groq said: internal detail"))) as http:
        response = http.post(ESTIMATE_PATH, json={"description": "Pasta"})

    assert response.status_code == 503
    assert response.json() == {"error": {"code": "provider_unavailable"}}
    assert "internal detail" not in response.text


def test_estimation_failure_is_502_with_a_stable_code():
    with client(FakeEstimator(EstimationFailed("model said: detail"))) as http:
        response = http.post(ESTIMATE_PATH, json={"description": "asdf"})

    assert response.status_code == 502
    assert response.json() == {"error": {"code": "estimation_failed"}}


def test_unexpected_errors_never_leak_details():
    with client(FakeEstimator(RuntimeError("stack detail"))) as http:
        response = http.post(ESTIMATE_PATH, json={"description": "Pasta"})

    assert response.status_code == 500
    assert response.json() == {"error": {"code": "internal_error"}}
    assert "stack detail" not in response.text


def test_without_credentials_the_service_runs_but_estimates_are_unavailable(monkeypatch):
    monkeypatch.delenv("GROQ_API_KEY", raising=False)

    with client(nutrition_estimator_from_environment()) as http:
        health = http.get("/health")
        response = http.post(ESTIMATE_PATH, json={"description": "Pasta"})

    assert health.json()["nutrition"]["configured"] is False
    assert response.status_code == 503
    assert response.json() == {"error": {"code": "provider_unavailable"}}


def test_model_is_configurable_through_the_environment():
    estimator = nutrition_estimator_from_environment(
        {"GROQ_API_KEY": "k", "LIFEOS_AI_NUTRITION_MODEL": " other/model "}
    )

    assert estimator.model == "other/model"
    assert nutrition_estimator_from_environment({"GROQ_API_KEY": "k"}).model == "openai/gpt-oss-20b"


def test_there_are_no_interactive_docs():
    with client(FakeEstimator()) as http:
        assert http.get("/docs").status_code == 404
        assert http.get("/openapi.json").status_code == 404
