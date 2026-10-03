"""PROD-AI-001: only the LifeOS API (holding the shared service key) may call the service."""

import logging

import pytest
from fastapi.testclient import TestClient

from lifeos_ai.app import create_app
from lifeos_ai.config import ServiceKeyError, service_key_from_environment
from tests.test_api import ESTIMATE_PATH, SERVICE_KEY, FakeEstimator

MEAL = {"description": "Pollo con le patate", "meal_type": "Lunch"}
UNAUTHORIZED = {"error": {"code": "unauthorized"}}


def anonymous(estimator=None):
    return TestClient(create_app(estimator or FakeEstimator(), service_key=SERVICE_KEY))


def test_liveness_is_public_and_minimal():
    with anonymous() as http:
        response = http.get("/health/live")

    assert response.status_code == 200
    assert response.json() == {"status": "ok"}


def test_liveness_never_calls_the_estimator_and_reveals_no_configuration():
    fake = FakeEstimator()

    with anonymous(fake) as http:
        response = http.get("/health/live")

    assert fake.requests == []
    for detail in ("fake", "groq", "model", "prompt", "configured", SERVICE_KEY):
        assert detail not in response.text


@pytest.mark.parametrize(
    "authorization",
    [
        None,
        "",
        "Bearer",
        "Bearer ",
        f"Basic {SERVICE_KEY}",
        f"Token {SERVICE_KEY}",
        SERVICE_KEY,
        "Bearer wrong-key-0123456789abcdefghijklmnopqrs",
        f"Bearer {SERVICE_KEY[:-1]}",
        f"Bearer {SERVICE_KEY}x",
    ],
)
def test_estimates_without_the_right_bearer_key_are_401(authorization):
    fake = FakeEstimator()
    headers = {} if authorization is None else {"Authorization": authorization}

    with anonymous(fake) as http:
        response = http.post(ESTIMATE_PATH, json=MEAL, headers=headers)

    assert response.status_code == 401
    assert response.json() == UNAUTHORIZED
    assert response.headers["www-authenticate"] == "Bearer"
    assert SERVICE_KEY not in response.text
    assert fake.requests == []


def test_authentication_runs_before_request_validation():
    with anonymous() as http:
        response = http.post(ESTIMATE_PATH, json={"user_id": "u-1"})
        unknown = http.get("/not-a-route")

    assert response.status_code == 401
    assert response.json() == UNAUTHORIZED
    assert unknown.status_code == 401


def test_the_right_bearer_key_reaches_the_estimator():
    fake = FakeEstimator()

    with anonymous(fake) as http:
        response = http.post(
            ESTIMATE_PATH, json=MEAL, headers={"Authorization": f"Bearer {SERVICE_KEY}"}
        )
        lowercase_scheme = http.post(
            ESTIMATE_PATH, json=MEAL, headers={"Authorization": f"bearer {SERVICE_KEY}"}
        )

    assert response.status_code == 200
    assert lowercase_scheme.status_code == 200
    assert len(fake.requests) == 2
    assert SERVICE_KEY not in response.text


def test_detailed_health_is_not_anonymous():
    with anonymous() as http:
        response = http.get("/health")
        authorized = http.get("/health", headers={"Authorization": f"Bearer {SERVICE_KEY}"})

    assert response.status_code == 401
    assert response.json() == UNAUTHORIZED
    for detail in ("fake", "model", "prompt", "configured"):
        assert detail not in response.text
    assert authorized.status_code == 200
    assert authorized.json()["nutrition"]["provider"] == "fake"
    assert SERVICE_KEY not in authorized.text


def test_the_key_is_never_logged(caplog):
    caplog.set_level(logging.DEBUG)

    with anonymous(FakeEstimator(RuntimeError("boom"))) as http:
        http.post(ESTIMATE_PATH, json=MEAL, headers={"Authorization": f"Bearer {SERVICE_KEY}"})
        http.post(ESTIMATE_PATH, json=MEAL, headers={"Authorization": "Bearer guessed-key"})

    assert caplog.records, "the unexpected failure is logged"
    assert SERVICE_KEY not in caplog.text
    assert "guessed-key" not in caplog.text


# ---- Configuration ----


@pytest.mark.parametrize("value", [None, "", "   ", "short-key", "a" * 31, "a" * 31 + " b"])
def test_the_service_cannot_start_without_a_strong_key(monkeypatch, value):
    if value is None:
        monkeypatch.delenv("LIFEOS_AI_SERVICE_KEY", raising=False)
    else:
        monkeypatch.setenv("LIFEOS_AI_SERVICE_KEY", value)

    with pytest.raises(ServiceKeyError) as failure:
        create_app(FakeEstimator())

    assert "LIFEOS_AI_SERVICE_KEY" in str(failure.value)
    if value and value.strip():
        assert value.strip() not in str(failure.value)


def test_the_key_is_read_from_the_environment_and_trimmed(monkeypatch):
    monkeypatch.setenv("LIFEOS_AI_SERVICE_KEY", f" {SERVICE_KEY}\n")

    assert service_key_from_environment() == SERVICE_KEY

    with TestClient(create_app(FakeEstimator())) as http:
        response = http.get("/health", headers={"Authorization": f"Bearer {SERVICE_KEY}"})

    assert response.status_code == 200


def test_an_explicit_weak_key_is_rejected_too():
    with pytest.raises(ServiceKeyError):
        create_app(FakeEstimator(), service_key="")
