# LifeOS AI evaluation lab

AI-EVAL-001 is a reusable local benchmark laboratory for future intelligence
experiments. It establishes repeatable measurements before adding probabilistic
systems. Trip detection proves the generic harness; it does not define it.
Finance summaries, anomalies, Nutrition, workout insights, recommendations,
Tasks, Focus and cross-domain reviews can supply different inputs and scorers.

## Production boundary

This directory is research/tooling only. There are no services, endpoints,
database access, production credentials or deployment changes. Since AI-003 the
lab imports the production AI service package (`src/python/lifeos-ai`, editable
path dependency) so live evaluations call the production prompts, schemas and
transport instead of copies; production never imports the lab. LifeOS production remains .NET, PostgreSQL and MAUI. Every fixture is
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
- `Dataset[Input, Output]`: name, version, ordered cases and LF-normalized SHA-256.
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
It omits paths, wall-clock timestamps, machine identity and randomness.
Variant adapters additionally record request-attempt timings; baseline JSON
remains deterministic.
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

Live acceptance is complete. The user accepted the controlled-harness results
below for the frozen v1 dataset and unchanged scorer (IoU >= 0.5), model
`openai/gpt-oss-20b` and prompt `trip-detection-groq-v1`.

| Metric | Baseline | Groq LLM |
|---|---:|---:|
| Cases | 16 | 16 |
| Provider/runtime errors | 0 | 0 |
| TP | 8 | 6 |
| FP | 1 | 0 |
| FN | 2 | 4 |
| Precision | 0.8889 | 1.0000 |
| Recall | 0.8000 | 0.6000 |
| F1 | 0.8421 | 0.7500 |
| Mean absolute boundary error (days) | 0.1250 | 0.0833 |

Groq-minus-baseline F1 delta: **-0.09210526315789469**. Mean boundary-error
delta: **-0.04166666666666667 days**.

| Incorrect-case differences | Baseline | Groq |
|---|---|---|
| two-trips | correct | incorrect |
| sparse-trip | incorrect | incorrect |
| ambiguous-local-cluster | incorrect | correct |
| foreign-currency-trip | correct | incorrect |
| accommodation-only | incorrect | incorrect |
| uncertain-boundaries | incorrect | incorrect |

Groq had higher precision (1.0 vs 0.8889), lower recall (0.6 vs 0.8), lower F1
(0.75 vs 0.8421), and lower boundary error among matched trips. Boundary error
is matched-trip-only and does not establish overall superiority. There were zero
provider/runtime errors. The experiment successfully demonstrated that an LLM
is not automatically better than an explainable deterministic baseline; no
overall winner is declared. The prompt was not tuned after observing the result.
Final documentation uses the accepted result without consuming additional quota.
Live JSON remains ignored and uncommitted.

Provider documentation: [Groq strict structured outputs](https://console.groq.com/docs/structured-outputs)
and [LangChain ChatGroq structured output](https://reference.langchain.com/python/langchain-groq/chat_models/ChatGroq/with_structured_output).

## Next experiments; not implemented here

AI-EVAL-003 implements variant comparison and a frozen 60-case v2 benchmark
after AI-EVAL-002 live acceptance (see below).
AI-EVAL-004 evaluates LangGraph only when a genuinely multi-step cross-domain
workflow exists. These are experiments, not commitments to production frameworks.
Future domains include Finance, Gym, Nutrition/Food, Tasks, Focus/Pomodoro and
other modules; the harness does not restrict that roadmap.

Remaining limitations: small authored synthetic sample, subjective ambiguity,
category dependence, probabilistic output variation, limited Free quota,
no general timeout for arbitrary future predictors, and matched-only boundary
statistics. Results establish a laboratory
baseline, not evidence of real-world trip detection quality.

## AI-EVAL-003: frozen v2 and experiment variants

V1 stays completely frozen: dataset, deterministic baseline, Groq detector,
`trip-detection-groq-v1` and TripScorer/IoU semantics are unchanged. Default
commands still use v1. V2 is `datasets/trip_detection/v2.json`, version 2.0.0:
60 independently authored synthetic cases, 30 positive and 30 negative, with
35 expected trips. Tags overlap and their counts must not be added together.
Cases include ordinary months, weekends, long leisure, business and mixed
visits, close/separate/multiple journeys, cross-month/year stays, sparse and
accommodation-only evidence, private cars, transport traps, foreign purchases
and trips, multiple currencies, advance bookings, expensive purchases, local
restaurants/hotels, uncertain boundaries, no-spend days and interleaved bills.
Ambiguous labels describe author knowledge, which may exceed observable input.
The suite also includes an untrusted-description trap.

V2 is frozen before any candidate-model result is observed. Canonical LF SHA-256:
`4644e129ad5dd5c939e2003ac59f812f6550f52a254b6600dda5ad04754964c1`.
The loader validates the original JSON, then hashes its bytes with CRLF replaced
by LF (`sha256-lf-v1`). `Dataset.sha256` and exported `dataset.sha256` use this
comparison identity, so Windows/Linux checkout line endings do not change it.
No parsing/reserialization, key sorting, whitespace removal or case sorting is
used: all semantic content and case order remain intact. Formatting changes
other than CRLF/LF still produce a different identity. Duplicate keys and
malformed JSON remain rejected before normalization.

New exports include `metadata.dataset_hash.strategy` and `legacy_sha256`, the
raw hashes of the equivalent all-LF and all-CRLF files. These are compatibility
aliases, not comparison identities or model inputs. When comparing a new export
with an older export lacking hash metadata, its raw hash must match one of these
aliases, and all existing case/scorer compatibility checks still apply. Two new
runs must have identical normalized hashes; alias matching cannot bypass that.
Two legacy runs retain exact raw-hash matching. If two old exports differ only
in line endings, produce at least one new export with the updated harness
(baseline offline; LLM only through an explicit live command). Old exports
alone contain insufficient source content to prove equivalence.
Unknown hash strategies are rejected. No checkout-specific diagnostic raw hash
is stored in new results. Regression tests pin canonical content for v1/v2 and
the three frozen implementation files and compare LF/CRLF exports for both
dataset versions. The JSON result shape and schema/harness versions remain
compatible with AI-EVAL-001/002.

### Variant contract and extension

`--variant <JSON>` selects a complete experiment identity instead of `--system`.
It is an explicit live evaluation command. Both old `--system baseline` and
`--system groq` remain available. Do not combine these options.

Configuration requires exactly: `provider`, `model`, `prompt_version`,
`context_version`, `structured_output`, `generation_settings`. Identity strings
are nonempty; generation settings are a JSON object with finite values. The
core has no Groq dependency or provider-specific settings. Each registered
adapter validates its supported settings before running cases. Unknown
providers/prompts/contexts/output methods fail rather than silently fall back.
Do not put credentials, URLs containing credentials or personal data in configs.

The Groq adapter supports `json_schema` with strict output, `events-v1` context,
temperature 0..2, positive max_tokens, low/medium/high reasoning_effort and
include_reasoning=false. Timeout/retry policy remains the frozen v1 policy.
There are two checked-in configurations:

- `experiments/trip_detection/control-v1.json`: frozen prompt/model/settings.
- `experiments/trip_detection/candidate-v2.json`: the **one** new prompt,
  `trip-detection-v2`, same model/settings and event-only context.

The candidate is designed from AI-EVAL-002 failure classes: multiple journeys,
sparse/no-spend evidence, foreign currency, accommodation-only trips and
uncertain boundaries. It was not tuned against fixture results. Neither prompt
receives truth, case ids, tags, dataset descriptions, baseline or scorer output.
The wrapper reuses frozen serialization, retry and structured parsing; it does
not change the v1 adapter. Saved `metadata.experiment` captures identity, while
system configuration includes dependency versions and retry settings. Retain
the repository commit SHA and locked dependencies with exported results.

To compare another accessible model, copy either JSON and change `model` only.
No model-access claim is implied: availability and schema/reasoning capabilities
must be verified by an explicitly requested live run. No automatic fallback.
To add a prompt, register an immutable version in `variants.PROMPTS`. To add a
context, implement and version event-only context construction in the adapter,
then validate that selection; the current adapter deliberately rejects unknown
contexts. To add a provider, implement the generic Predictor contract and its
configuration/telemetry, and register its factory with `core.create_experiment`
in the evaluator plugin. Keep SDK dependencies inside that adapter. A missing
credential or unavailable provider is not a reason to add dependencies or spend
calls. No second provider SDK or live run was added in this task.

### Offline validation and comparison

From `tools/ai-evals`:

```powershell
uv sync --locked
uv run python -m pytest
uv run ruff check .
uv run ruff format --check .
uv run --offline --locked python -m lifeos_ai_evals evaluate trip_detection --output results/v1-baseline.json
uv run --offline --locked python -m lifeos_ai_evals evaluate trip_detection --dataset datasets/trip_detection/v2.json --output results/v2-baseline.json
```

The v1 reference remains TP=8 FP=1 FN=2, precision=0.888889, recall=0.8,
F1=0.842105, start/end/absolute boundary means=0.125 days. V2 baseline:
TP=23 FP=6 FN=12, precision=0.793103, recall=0.657143, F1=0.71875,
start mean=0.043478, end mean=0.130435, absolute boundary mean=0.086957 days.
Both runs have zero execution errors. Boundary means describe matched trips only.

Compare two or more saved runs, all from the **same** dataset and scorer:

```powershell
uv run --offline --locked python -m lifeos_ai_evals compare results/v2-baseline.json results/v2-control.json results/v2-candidate.json --output results/v2-comparison.json
```

Run 0 is the reference; signed candidate-minus-reference deltas are reported for
every detection/boundary metric and scored/error coverage. Reports include full
run identities, incorrect-case statuses, separate execution-error details,
per-run telemetry and tag metrics/deltas. Each tag has case/scored/error coverage;
errors are excluded from its metrics. Negative-only tags can have F1=1 with no
trips, following unchanged scorer semantics; inspect FP and coverage as well.
Comparison rejects dataset name/version/comparison-hash drift, ordered case ids,
labels/tags, scorer configuration/IoU, harness/schema or failure-policy drift.
Older exports without tag data retain their available information. It never
chooses an overall winner. Fewer errors or lower matched boundary error cannot
alone establish better detection.

### Optional live acceptance (not run automatically)

Use the existing Groq Free key via environment only. No key inspection or live
requests are needed for normal validation. Run these only when ready to spend
quota; normally 60 requests each, at most 180 attempts per run with retries:

```powershell
uv run python -m lifeos_ai_evals evaluate trip_detection --dataset datasets/trip_detection/v2.json --variant experiments/trip_detection/control-v1.json --output results/v2-control.json
uv run python -m lifeos_ai_evals evaluate trip_detection --dataset datasets/trip_detection/v2.json --variant experiments/trip_detection/candidate-v2.json --output results/v2-candidate.json
uv run python -m lifeos_ai_evals compare results/v2-baseline.json results/v2-control.json results/v2-candidate.json --output results/v2-comparison.json
```

Variant telemetry counts every actual request attempt, including retries and
failures. Latency min/mean/max covers provider attempts only, excluding retry
and inter-case sleeps. Token totals and coverage are reported independently for
input/output/total tokens; unavailable fields are null, never fabricated zero.
Parse failures with a provider response can still have usage. Failed requests
without a response have none. No provider cost is inferred. Original `--system
groq` retains its original optional aggregate usage metadata; use the variant
control to obtain the additional attempt telemetry without changing its prompt.

### Anti-overfitting and limits

Do not edit v1. Do not modify v2 after observing candidate-model results to
improve scores. Do not repeatedly tune against individual fixtures, include
labels in prompts, or add another prompt/context candidate under AI-EVAL-003.
Record proposed general changes for a future version and independent holdout.
This authored sample is not representative real-world validation. Some exact
boundaries are intentionally unknowable from purchases. Model outputs may vary
at temperature zero; match coverage differs between systems. No model/provider
live acceptance was executed for AI-EVAL-003 and no overall winner is declared.

## AI-003: Weekly Review and Action Agent evaluators

Full design, metrics, gates and limits: `docs/tasks/ai/AI-003.md`.

- `weekly_review`: 28 frozen synthetic weeks (`datasets/weekly_review/v1.json`),
  replayed through the production `GroqWeeklyReviewInterpreter`; deterministic
  numeric grounding, lexicon detectors (advice, history comparison, causality),
  empty-section and structure checks. Offline system: template baseline.
- `action_agent`: 21 frozen synthetic scenarios (`datasets/action_agent/v1.json`)
  replayed through a versioned mirror of the .NET loop (`dotnet_mirror.py`,
  drift-tested against the .NET sources) and the production `GroqActionAgent`;
  decision, grounding, tool, safety and efficiency metrics. Offline system: rule
  baseline.
- Live variants: `experiments/<evaluator>/control-v1.json` (production identity)
  and `candidate-model-gpt-oss-120b.json`. `compare` adds acceptance gates for
  these evaluators and telemetry deltas for all evaluators. `--case <id>` runs a
  subset (small-quota smoke); filtered runs cannot be compared with full runs.

```powershell
uv run --offline --locked python -m lifeos_ai_evals evaluate weekly_review --output results/wr-baseline.json
uv run --offline --locked python -m lifeos_ai_evals evaluate action_agent --output results/aa-baseline.json
$env:GROQ_API_KEY = "<your-key>"   # live runs only
uv run python -m lifeos_ai_evals evaluate weekly_review --variant experiments/weekly_review/control-v1.json --output results/wr-control.json
uv run python -m lifeos_ai_evals evaluate action_agent --variant experiments/action_agent/control-v1.json --output results/aa-control.json
uv run python -m lifeos_ai_evals compare results/wr-baseline.json results/wr-control.json
```
