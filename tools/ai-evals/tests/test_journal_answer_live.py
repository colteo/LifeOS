"""AI-005 live answer adapter, offline: production reuse, outcomes, telemetry."""

import json
import logging

import httpx
import pytest
from lifeos_ai.groq_chat import GroqSettings
from lifeos_ai.journal_memory import prompt as production_prompt
from lifeos_ai.journal_memory.answer import ProviderUnavailable
from lifeos_ai.journal_memory.groq import GroqJournalAnswerer
from live_fakes import ScriptedTransport, content, no_sleep, usage

from lifeos_ai_evals.core.engine import Dataset, evaluate
from lifeos_ai_evals.evaluators.journal_answer import live, plugin
from lifeos_ai_evals.journal_memory.identity import ANSWER_CONTROL

EXPERIMENTS = plugin.default_dataset().parents[2] / "experiments/journal_answer"
CONTROL = EXPERIMENTS / "control-v1.json"
CANDIDATE = EXPERIMENTS / "candidate-prompt-v2.json"
ANSWER = {"status": "answered", "answer": "You had tajarin.", "citations": ["S1"]}


def predictor(*replies):
    transport = ScriptedTransport(*replies)
    system = live.LiveAnswerPredictor(
        ANSWER_CONTROL, transport=transport, sleep=no_sleep, case_sleep=lambda _: None
    )
    return system, transport


def cases():
    return plugin.load(plugin.default_dataset()).cases


def test_the_registry_references_the_production_prompt():
    assert live.PROMPTS is production_prompt.PROMPTS
    assert (
        live.PROMPTS[ANSWER_CONTROL["prompt_version"]]
        is production_prompt.SYSTEM_PROMPT
    )


def test_control_request_body_equals_the_production_body_for_every_case():
    system, _ = predictor()
    production = GroqJournalAnswerer(GroqSettings(api_key="x"))
    control = system.session.answerer()
    for case in cases():
        assert control.request_body(case.input) == production.request_body(case.input)


def test_experiment_loads_only_the_control_or_the_registered_candidate(
    tmp_path, monkeypatch
):
    monkeypatch.delenv("GROQ_API_KEY", raising=False)
    for accepted in (CONTROL, CANDIDATE):
        with pytest.raises(ValueError, match="GROQ_API_KEY"):
            plugin.experiment(accepted)  # identity accepted, then the key is required
    for key, value in (
        ("prompt_version", "journal-rag-answer-v3"),
        ("model", "openai/gpt-oss-120b"),
        (
            "generation_settings",
            {**ANSWER_CONTROL["generation_settings"], "temperature": 1},
        ),
    ):
        changed = json.loads(CANDIDATE.read_text(encoding="utf-8"))
        changed[key] = value
        path = tmp_path / "other.json"
        path.write_text(json.dumps(changed), encoding="utf-8")
        with pytest.raises(ValueError, match="out of scope"):
            plugin.experiment(path)


def test_valid_answer_is_scored_with_tokens_and_no_content_in_telemetry():
    case = cases()[0]
    system, transport = predictor(content(ANSWER, tokens=usage(800, 40)))
    dataset = Dataset("d", "1", (case,), "x")
    result = evaluate("journal_answer", dataset, system, plugin.scorer())
    assert result.errors == 0
    metrics = result.cases[0].score.metrics
    assert metrics["valid"] == 1 and metrics["total_tokens"] == 840
    assert metrics["attempts"] == 1
    assert len(transport.bodies) == 1
    telemetry = json.dumps(system.experiment_metadata())
    assert "tajarin" not in telemetry and case.input.question not in telemetry
    assert result.metadata["experiment"] == ANSWER_CONTROL


@pytest.mark.parametrize(
    ("reply", "outcome"),
    [
        (
            {"status": "answered", "answer": "x", "citations": ["S9"]},
            "unknown_citation",
        ),
        ({"status": "answered", "answer": "", "citations": []}, "invalid_output"),
        ({"status": "maybe", "answer": "", "citations": []}, "invalid_output"),
        (
            {"status": "insufficient_evidence", "answer": "x", "citations": []},
            "invalid_output",
        ),
    ],
)
def test_invalid_outputs_are_scored_with_the_production_outcome(reply, outcome):
    system, _ = predictor(content(reply, tokens=usage()))
    output = system.predict(cases()[0].input).value
    assert (output.valid, output.outcome, output.status) == (False, outcome, None)
    assert output.total_tokens == 1020  # the provider answered: usage is kept


def test_a_rejected_request_is_an_invalid_output():
    system, _ = predictor(httpx.Response(400, json={"error": "schema"}))
    output = system.predict(cases()[0].input).value
    assert (output.valid, output.outcome) == (False, "http_400")


def test_provider_unavailable_is_an_execution_error_with_retries_counted():
    system, transport = predictor(*[httpx.Response(503) for _ in range(3)])
    level = logging.getLogger(live.LOGGER).level
    with pytest.raises(ProviderUnavailable):
        system.predict(cases()[0].input)
    summary = system.session.telemetry.summary()
    assert summary["request_attempts"] == 3 and summary["retries"] == 2
    assert summary["attempt_status_counts"] == {"http_503": 3}
    assert logging.getLogger(live.LOGGER).level == level  # restored
