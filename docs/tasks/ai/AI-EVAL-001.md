# AI-EVAL-001 — Generic AI Evaluation Harness

Type: local research/evaluation spike. Branch: spike/ai-evals.

## Authoritative scope

Build a reusable reproducible benchmark laboratory in tools/ai-evals.
Trip detection is the first evaluator, not the architecture of the harness.
Future Finance summaries, anomaly detection, Nutrition analysis, workout insights,
cross-domain weekly reviews and recommendation quality must reuse the core.

Production remains .NET, PostgreSQL and MAUI. No production code, schema,
Dockerfile, Render infrastructure, endpoints, deployment, real user data,
credentials or provider calls may be added. No Python service is introduced.

## Stack and structure

Python >=3.12, uv, pytest and ruff. Runtime dependencies: none.
Use the standard library. Do not install LangChain, LangGraph, provider SDKs,
pandas, numpy, scikit-learn, pydantic, vector databases or notebooks.
Separate src/lifeos_ai_evals/core from evaluators/trip_detection;
keep synthetic JSON under datasets/trip_detection and tests under tests.
Exact filenames are implementation decisions; avoid oversized abstractions.

## Generic contracts

Provide Dataset, Case, Predictor/SystemUnderTest, Prediction, Scorer/Metric,
CaseResult, EvaluationResult and aggregate metrics. Use small typed contracts,
Protocols/generics when useful. A new evaluator requires no engine changes.
The core orchestrates; trip interval matching and metrics belong to the plugin.

Datasets are deterministic, synthetic, human-readable and version-controlled.
Each case has a stable unique id, input, ground truth, optional tags and
description. Reject malformed datasets clearly; never silently skip cases.

Console and JSON results include evaluator, system name/version, dataset
name/version, count, aggregates, individual predictions, failures/errors and
reproducibility metadata. Avoid machine paths and secrets. Generated results
are ignored by default. Distinguish incorrect prediction from execution error;
continue after case errors, but abort invalid datasets/configuration.
No network, clock-dependent decisions or unrecorded randomness. Repeated runs
must yield equivalent metrics. Provide a standard-library CLI with dataset,
output and verbose options, and document actual commands.

## First evaluator: trip detection

Explicit synthetic financial events use calendar date, transaction type,
amount, currency, category and synthetic description. No bank/GPS dependency
or coupling to EF/production entities. Ground truth is zero or more inclusive
calendar-date intervals with start_date and end_date.

Include normal month, weekend, longer leisure trip, two trips, cross-month,
sparse transactions, local restaurant/hotel activity, transport without travel,
mixed ordinary spending and ambiguity. Compatible useful older-draft scenarios
also include foreign currency, advance booking and expensive non-travel purchase.
Use enough cases for useful aggregates; do not tune rules to individual fixtures.

Implement an explainable deterministic TripDetector using simple accommodation,
transport/travel signals and temporal/consecutive-day clustering. No ML or LLM.
Expose concise deterministic reasoning with predictions and document rules.

Use deterministic one-to-one interval matching with a documented inclusive-day
IoU threshold. Report TP, FP, FN, precision, recall and F1; explicitly define
and test zero denominators. Report matched start/end absolute errors in days
and mean boundary errors independently of F1. Never score list length alone.

## Tests and quality gates

Core tests: loading, malformed rejection, stable ids, repeatability, per-case
results, aggregation, predictor exceptions, JSON serialization and CLI success.
Trip tests: exact/overlapping/insufficient overlap, one prediction vs two expected,
empty, FP, FN, multiple/cross-month trips, precision/recall/F1, boundary errors
and deterministic baseline. Run uv sync, uv run pytest,
uv run ruff check ., uv run ruff format --check .
After installation, evaluation/tests require no network or credentials.

## Documentation and roadmap

README explains purpose, production boundary, uv installation, tests, CLI/JSON,
architecture, extension, baseline rules, scoring and an actual output example.
AI-EVAL-002 evaluates Groq Free plus LangChain for model abstraction,
structured outputs and prompt composition, and baseline-vs-LLM comparison.
AI-EVAL-003 compares prompt/context/model variants. AI-EVAL-004 evaluates
LangGraph only for a genuinely multi-step cross-domain workflow. Future domains
include Finance, Gym, Nutrition/Food, Tasks, Focus/Pomodoro and other modules.
Do not implement later tasks or constrain future architecture to Finance/Gym.
Use these frameworks only for defensible requirements.

## Completion and delivery

Done: working uv project, generic core, versioned dataset, deterministic baseline,
detection and boundary metrics, console/JSON, passing pytest/ruff, extension docs,
no external model/provider and no production changes. Self-review the full diff.
Commit feat(ai-evals): add generic evaluation harness on spike/ai-evals;
push only that branch and create a PR to main if tooling permits. Do not merge.

Final report fields: RESULT (PASS/BLOCKED/NEEDS_DECISION), HEAD, PR, PYTHON,
DEPENDENCIES (runtime/dev), DATASET (count/categories), BASELINE, METRICS (actual),
VALIDATION (pytest/ruff check/format), PRODUCTION CHANGES, KEY DECISIONS, RISKS,
NEXT (AI-EVAL-002 recommendation).
