# LifeOS AI evaluation lab

AI-EVAL-001 is a reusable local benchmark laboratory for future intelligence
experiments. It establishes repeatable measurements before adding probabilistic
systems. Trip detection proves the generic harness; it does not define it.
Finance summaries, anomalies, Nutrition, workout insights, recommendations,
Tasks, Focus and cross-domain reviews can supply different inputs and scorers.

## Production boundary

This directory is research/tooling only. There are no production references,
services, endpoints, database access, production credentials or deployment
changes. LifeOS production remains .NET, PostgreSQL and MAUI. Every fixture is
synthetic; descriptions explicitly mark evaluator-only labels as synthetic.
Do not import personal data into this lab or commit generated results.

## Setup and commands

Run from the repository root in PowerShell:

```powershell
cd tools/ai-evals
uv sync --locked
uv run pytest
uv run ruff check .
uv run ruff format --check .
uv run python -m lifeos_ai_evals evaluate trip_detection
uv run python -m lifeos_ai_evals evaluate trip_detection --verbose
uv run python -m lifeos_ai_evals evaluate trip_detection --output results/baseline.json
uv run python -m lifeos_ai_evals evaluate trip_detection --dataset datasets/trip_detection/v1.json
```

Python >=3.12 is supported by the source; `.python-version` pins the verified
existing interpreter, 3.14.7. uv can obtain that version if missing. `uv.lock`
pins pytest/ruff, langchain-groq and their transitive dependencies. The Groq
adapter uses LangChain Core and the official integration. `uv_build==0.12.19`
is a build-only backend for
editable installation of the src-layout package, not a runtime dependency.

On this Windows host, the WindowsApps Python alias failed to run; uv discovered
the real installed interpreter. Windows also denied the generated `pytest.exe`
launcher even after local reinstall. The equivalent portable command below
runs the complete suite without changing OS policy or test selection:

```powershell
uv run python -m pytest
```

Once synchronized, baseline evaluation and default validation require no network
or credentials:

```powershell
uv run --offline --locked python -m pytest
uv run --offline --locked ruff check .
uv run --offline --locked ruff format --check .
uv run --offline --locked python -m lifeos_ai_evals evaluate trip_detection
```

`results/` is ignored locally. The lab uses its editable source checkout to find
the default dataset; it is not a distribution of bundled datasets. Supply
`--dataset` for another local fixture.

## Small generic architecture

`core/engine.py` owns typed dataclasses and Protocols:

- `Case[Input, Output]`: stable id, input, expected, optional tags/description.
- `Dataset[Input, Output]`: name, version, ordered cases and source SHA-256.
- `Predictor`: name/version/configuration and `predict(input) -> Prediction`.
- `Prediction[Output]`: structured value plus deterministic explanations.
- `Scorer`: `score(expected, predicted) -> Score` and `aggregate(scores)`.
- `Score`: correctness flag, named numeric metrics and structured details.
- `CaseResult`: prediction/score or execution error with stage/type.
- `EvaluationResult`: identities, aggregates, cases, failures/errors and metadata.

The engine has no event, interval, trip or precision logic. It executes each
predictor then delegates scoring and aggregation. The trip plugin supplies its
own models, parser, baseline, interval matching and aggregation. The CLI imports
`evaluators.<name>.plugin` by convention, with no central evaluator registry.
Tests run a second arithmetic plugin without changing the core or CLI.

Plugin values and score details must be JSON-compatible primitives, containers
or dataclasses containing those types. Dates in this evaluator use ISO strings
so the JSON contract remains explicit. `to_json()` rejects non-finite numbers.

Dataset/configuration errors abort before evaluation. Predictor or case-scoring
exceptions produce `status=error` and remaining cases continue. Error records
retain stage and exception type, excluding raw messages/tracebacks which could
expose future adapter secrets. Errors are excluded from scorer totals; reports
always include error and scored-case counts. An all-error trip run has null
precision/recall/F1, not a misleading perfect score. Incorrect predictions remain
scored and are separate from execution errors.

CLI exit codes: 0 for completed runs (even an imperfect baseline), 1 for case
execution errors, 2 for invalid dataset/configuration. Aggregate-scoring errors
are fatal programming/configuration failures rather than individual bad cases.

Metadata includes schema/harness versions, Python version, dataset hash,
system name/version/configuration, scorer configuration and failure policy.
It omits paths, wall-clock timestamps, machine identity, timings and randomness.
The deterministic baseline returns equivalent metrics and JSON on the same
Python version. LLM outputs are probabilistic even at temperature zero; metadata
records experiment identity rather than promising byte-for-byte reproducibility.
When comparing experiments, preserve commit SHA alongside
exported results and bump system/dataset versions after behavior/fixture changes.

## Dataset format and extension

JSON envelope: `name`, `version` (nonempty strings), `cases` (nonempty array).
Each case requires a unique nonempty `id`, `input` and `expected`; `tags` is an
optional string array and `description` an optional string. Loading validates
all cases before running. Invalid JSON, duplicate keys/ids, NaN/Infinity,
invalid metadata and malformed evaluator payloads are rejected with context.

Trip `input` contains exactly `events`, an array. Each event requires:
`date` (YYYY-MM-DD), `transaction_type` (expense/income/transfer), `amount`
(finite nonnegative decimal string), `currency` (three uppercase ASCII letters),
`category` and `description` (nonempty strings). Categories are explicit
synthetic evaluator labels, not production enum or EF mappings. Amounts are
magnitudes; transaction type supplies direction. No bank APIs or GPS are used.

Trip `expected` is an array of inclusive intervals, each with `start_date` and
`end_date`. Empty means no trip. Reversed, duplicate or overlapping ground-truth
intervals are invalid. Events may be unsorted. Ground truth is normalized by
calendar order and is never passed to the predictor.

The 16 cases cover ordinary month, weekend, long leisure trip, two trips,
cross-month, sparse evidence, local hotel/restaurant activity, transport-only,
mixed business/ordinary spending, ambiguous local activity, foreign currency,
advance booking, expensive non-travel purchase, restaurant-heavy week,
accommodation-only travel and uncertain boundaries. Labels reflect synthetic
author knowledge; ambiguity is retained instead of tuning it away.

To add a case, assign a permanent descriptive id, enter synthetic events and
truth intervals, add optional tags/description, and bump dataset version.
Add an immutable new dataset file for an experiment comparison; do not silently
change the meaning of a published version. Re-run tests and evaluation.

To add an evaluator:

1. Create `evaluators/<name>/` with evaluator-specific input/output models.
2. Supply parsing callbacks to `core.load_dataset` for its JSON payloads.
3. Implement the Predictor and Scorer methods above. The scorer defines
   correctness, per-case metrics and domain-appropriate aggregate weighting.
4. Add `plugin.py` exporting `default_dataset() -> Path`, `load(path) -> Dataset`,
   `system() -> Predictor`, and `scorer() -> Scorer`.
5. Add versioned synthetic fixtures under `datasets/<name>/` and focused tests.
6. Run `uv run python -m lifeos_ai_evals evaluate <name>`.

No engine or CLI changes are needed. A future alternative trip detector can
implement the same Predictor contract and be evaluated against the same dataset
and scorer using `core.evaluate`. Plugins may optionally accept a system name;
no-argument `system()` remains supported for existing evaluator plugins.

## Deterministic baseline

`travel-cluster-baseline` v1.0.0 uses only expense categories `accommodation`,
`transport` and `travel`. Sort this evidence by date and split clusters whenever
the gap between consecutive evidence dates exceeds two calendar days. Require
at least two distinct evidence days, accommodation, and transport or travel.
Return the first/last evidence dates with a concise explanation. Ordinary
spending does not extend an interval. Names, amounts and currency never change
the prediction. No fixture-specific names, ML, LLM, current date or randomness.

This is intentionally limited: prepaid or accommodation-only travel can be
missed, local activity can look like a trip, and sparse receipts obscure actual
boundaries. These errors are benchmark evidence, not reasons to tune to fixtures.

## Matching and metrics

Inclusive-day intersection-over-union is intersection days / union days.
A pair qualifies when IoU >= **0.5**. Bipartite augmenting-path matching finds
maximum-cardinality one-to-one assignments so a greedy choice cannot lose a
valid detection. Predictions are traversed by index, candidate truth intervals
by descending IoU then index; reassignment is deterministic. This maximizes TP,
not total IoU or boundary quality. Match indexes and IoU are exported per case.

TP = matched pairs; FP = unmatched predictions; FN = unmatched truth intervals.
Aggregates sum counts across scored cases (micro averages):
precision = TP/(TP+FP), recall = TP/(TP+FN), F1 = 2TP/(2TP+FP+FN).
If both lists are empty, precision/recall/F1 are 1. If only one is empty,
all three are 0. Undefined zero-denominator precision/recall thus become 0
when detection errors exist, or 1 when both are empty. Negative cases do not
inflate micro TP. No scored cases yields null ratios.

For matched pairs, report absolute start and end date error in calendar days,
with separate means and mean absolute boundary error over both endpoints.
Boundary metrics are null when no matches exist; unmatched trips are penalized
in detection counts. Case correctness requires the exact interval set, so an
approximate match can earn a TP while the case still reports INCORRECT.

Example actual v1.0.0 output (per-case rows omitted):

```text
Evaluator: trip_detection
System: travel-cluster-baseline v1.0.0
Dataset: synthetic-trip-scenarios v1.0.0
Cases: 16 | Scored: 16 | Incorrect: 4 | Errors: 0
true_positives: 8
false_positives: 1
false_negatives: 2
precision: 0.8889
recall: 0.8000
f1: 0.8421
mean_start_error_days: 0.1250
mean_end_error_days: 0.1250
mean_absolute_boundary_error_days: 0.1250
```

## AI-EVAL-002: Groq Free structured trip detection

Use an existing **Groq Free** account and create a key in the Groq console.
Never enable billing, add a payment method or upgrade for this experiment.
The account must remain Free; the client cannot independently verify account
billing status. No fallback model/provider exists. Unavailable model access
requires NEEDS_DECISION; rate limits never justify an upgrade.

Supply the key only through the process environment, never in repository files,
tracked `.env`, results or logs. Do not paste a key into documentation or chat.

```powershell
$env:GROQ_API_KEY = "<your-key>"
uv run python -m lifeos_ai_evals evaluate trip_detection --system baseline --output results/trip-baseline.json
uv run python -m lifeos_ai_evals evaluate trip_detection --system groq --output results/trip-groq.json
uv run python -m lifeos_ai_evals compare results/trip-baseline.json results/trip-groq.json --output results/trip-comparison.json
```

The original command without `--system` still runs the baseline. Missing or
blank GROQ_API_KEY aborts Groq configuration before any case runs. Default pytest
uses fakes and makes no Groq calls. The explicit Groq evaluation above is the
optional live acceptance workflow: 16 calls normally, no separate automatic
live tests. Inspect case errors and exit code before interpreting metrics.

`GroqTripDetector` implements the same Predictor contract and sends only parsed
Event fields, sorted by all fields with dates first, as readable JSON. It never
sends expected trips, case ids/tags/descriptions, baseline results or scores.
`ChatPromptTemplate` composes a fixed system message and event-only human
message. `ChatGroq` provides model abstraction and
`with_structured_output(schema, method="json_schema", strict=True)` supplies
native constrained output. Every object disallows extra properties and requires
all fields. Adapter validation converts dates into existing Trip objects and
rejects invalid, reversed, duplicate or overlapping intervals. TripScorer and
the generic evaluation engine are unchanged.

Frozen experiment: `openai/gpt-oss-20b`, `trip-detection-groq-v1`, temperature 0,
max_tokens 2048, reasoning_effort low, include_reasoning false. The deliberate
v1 prompt is not optimized against the 16 fixtures. There are no chains/agents
beyond the structured model wrapper, no monolithic langchain and no LangGraph.
LangChain/Groq/Core package versions and generation settings are recorded.
Raw provider messages are temporary only; no raw responses or reasoning traces
are persisted, and ambient LangSmith tracing is disabled around invocation.
Only optional numeric usage totals are collected. Missing usage does not fail
scoring. Totals cover available responses, including parse failures with usage,
and exclude failed requests; usage_responses identifies coverage.

Each attempt has a 45-second provider timeout and SDK retries are disabled.
Only HTTP 429 is retried, with at most three attempts per case. Retry-After
seconds or HTTP dates are respected; otherwise wait 2 then 4 seconds. A requested
wait exceeding 60 seconds ends the case as an execution error rather than retrying
early or waiting indefinitely. Three seconds between cases modestly spaces
requests. Exhausted retries, timeouts and parsing/provider errors retain the
existing error policy: record stage/type, omit exception text, continue, exclude
errors from TP/FP/FN. This does not imply a total run wall-clock deadline.

Comparison reads existing exports without consuming quota and rejects differing
dataset identities/hashes, case order/labels, scorer settings (including IoU
0.5), harness/schema versions and error policies. It reports both systems,
signed candidate-minus-baseline F1 and boundary deltas, plus all cases where
either system is incorrect or errors. Undefined metrics yield null deltas.
Check error coverage first: better F1 on fewer scored cases does not establish
improvement. Boundary error measures matched trips only. A factual difference
in one metric is not an overall winner designation. Results remain ignored.

Current live acceptance is **blocked: GROQ_API_KEY absent**. Baseline: TP 8,
FP 1, FN 2, precision 0.8889, recall 0.8000, F1 0.8421, mean boundary error
0.125 days. No LLM metrics are claimed before a real run.

Provider documentation: [Groq strict structured outputs](https://console.groq.com/docs/structured-outputs)
and [LangChain ChatGroq structured output](https://reference.langchain.com/python/langchain-groq/chat_models/ChatGroq/with_structured_output).

## Next experiments; not implemented here

AI-EVAL-003 compares prompts, context and models after AI-EVAL-002 live acceptance.
AI-EVAL-004 evaluates LangGraph only when a genuinely multi-step cross-domain
workflow exists. These are experiments, not commitments to production frameworks.
Future domains include Finance, Gym, Nutrition/Food, Tasks, Focus/Pomodoro and
other modules; the harness does not restrict that roadmap.

Remaining limitations: small authored synthetic sample, subjective ambiguity,
category dependence, probabilistic output variation, limited Free quota,
no general timeout for arbitrary future predictors, and matched-only boundary
statistics. Results establish a laboratory
baseline, not evidence of real-world trip detection quality.
