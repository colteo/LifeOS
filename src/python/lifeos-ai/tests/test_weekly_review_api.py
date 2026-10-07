"""AI-001: POST /v1/weekly-review/interpret through the real FastAPI app (fake interpreter)."""

from fastapi.testclient import TestClient

from lifeos_ai.app import create_app
from lifeos_ai.config import weekly_review_interpreter_from_environment
from lifeos_ai.weekly_review.interpreter import InterpretationFailed, ProviderUnavailable
from lifeos_ai.weekly_review.schema import InterpretWeeklyReviewRequest, WeeklyReviewInsights
from tests.test_api import SERVICE_KEY, FakeEstimator
from tests.weekly_review_fixtures import TYPICAL_WEEK, VALID_INSIGHTS, week

INTERPRET_PATH = "/v1/weekly-review/interpret"


class FakeInterpreter:
    provider = "fake"
    model = "fake-model"
    prompt_version = "weekly-review-insights-v1"
    configured = True

    def __init__(self, outcome=None):
        self.outcome = outcome
        self.requests: list[InterpretWeeklyReviewRequest] = []
        self.closed = False

    async def interpret(self, request):
        self.requests.append(request)
        if isinstance(self.outcome, Exception):
            raise self.outcome
        return WeeklyReviewInsights.model_validate(VALID_INSIGHTS)

    async def aclose(self):
        self.closed = True


def client(interpreter, *, authorized=True):
    headers = {"Authorization": f"Bearer {SERVICE_KEY}"} if authorized else {}
    return TestClient(
        create_app(FakeEstimator(), interpreter=interpreter, service_key=SERVICE_KEY),
        headers=headers,
    )


def test_interpret_returns_versioned_attributed_insights():
    fake = FakeInterpreter()

    with client(fake) as http:
        response = http.post(INTERPRET_PATH, json=TYPICAL_WEEK)

    assert response.status_code == 200
    assert response.json() == {
        "output_version": 1,
        "provider": "fake",
        "model": "fake-model",
        "prompt_version": "weekly-review-insights-v1",
        "insights": VALID_INSIGHTS,
    }
    assert fake.requests == [InterpretWeeklyReviewRequest.model_validate(TYPICAL_WEEK)]
    assert fake.closed


def test_the_service_key_is_required_before_validation():
    fake = FakeInterpreter()

    with client(fake, authorized=False) as http:
        response = http.post(INTERPRET_PATH, json=TYPICAL_WEEK)
        invalid = http.post(INTERPRET_PATH, json={"user_id": "u-1"})

    assert response.status_code == invalid.status_code == 401
    assert response.json() == {"error": {"code": "unauthorized"}}
    assert fake.requests == []


def test_invalid_requests_are_422_without_echoing_the_figures():
    fake = FakeInterpreter()
    data = week()
    data["review_id"] = "r-1"
    data["gym"]["workouts"][0]["workout_name"] = "Private Workout Name"
    data["gym"]["workouts"][0]["day"] = "Someday"

    with client(fake) as http:
        response = http.post(INTERPRET_PATH, json=data)

    assert response.status_code == 422
    assert response.json() == {
        "error": {"code": "invalid_request", "fields": ["gym.workouts.0.day", "review_id"]}
    }
    assert "Private Workout Name" not in response.text
    assert fake.requests == []


def test_provider_unavailable_is_503_with_a_stable_code():
    with client(FakeInterpreter(ProviderUnavailable("Groq said: internal detail"))) as http:
        response = http.post(INTERPRET_PATH, json=TYPICAL_WEEK)

    assert response.status_code == 503
    assert response.json() == {"error": {"code": "provider_unavailable"}}
    assert "internal detail" not in response.text


def test_invalid_model_output_is_502_with_a_stable_code():
    with client(FakeInterpreter(InterpretationFailed("model said: detail"))) as http:
        response = http.post(INTERPRET_PATH, json=TYPICAL_WEEK)

    assert response.status_code == 502
    assert response.json() == {"error": {"code": "interpretation_failed"}}
    assert "detail" not in response.text


def test_unexpected_errors_never_leak_details(caplog):
    with client(FakeInterpreter(RuntimeError("stack detail"))) as http:
        response = http.post(INTERPRET_PATH, json=TYPICAL_WEEK)

    assert response.status_code == 500
    assert response.json() == {"error": {"code": "internal_error"}}
    assert "stack detail" not in response.text
    assert "Groceries" not in caplog.text


def test_without_credentials_interpretation_is_unavailable(monkeypatch):
    monkeypatch.delenv("GROQ_API_KEY", raising=False)

    with client(weekly_review_interpreter_from_environment()) as http:
        health = http.get("/health")
        response = http.post(INTERPRET_PATH, json=TYPICAL_WEEK)

    assert health.json()["weekly_review"] == {
        "provider": "groq",
        "model": "openai/gpt-oss-20b",
        "prompt_version": "weekly-review-insights-v1",
        "configured": False,
    }
    assert response.status_code == 503
    assert response.json() == {"error": {"code": "provider_unavailable"}}


def test_the_model_is_configurable_separately_from_nutrition():
    interpreter = weekly_review_interpreter_from_environment(
        {"GROQ_API_KEY": "k", "LIFEOS_AI_WEEKLY_REVIEW_MODEL": " other/model "}
    )
    default = weekly_review_interpreter_from_environment(
        {"GROQ_API_KEY": "k", "LIFEOS_AI_NUTRITION_MODEL": "nutrition/model"}
    )

    assert interpreter.model == "other/model"
    assert default.model == "openai/gpt-oss-20b"
