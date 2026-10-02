# AI-EVAL-002 — Groq + LangChain Structured Trip Detection

## Authoritative contract

Implement the user-supplied AI-EVAL-002 contract as an additive experiment on
feature/ai-eval-002. AI-EVAL-001 remains the source of the generic Predictor,
evaluation engine, frozen synthetic v1 dataset, deterministic baseline and exact
TripScorer. No production changes, deployment, database access or real user data.

Use only Groq Free, environment-only GROQ_API_KEY, official langchain_groq.ChatGroq,
LangChain Core prompt composition and native json_schema strict structured output.
Freeze openai/gpt-oss-20b, temperature 0, and one deliberate versioned v1 prompt.
Do not optimize against fixtures, compare models, introduce fallback providers,
enable billing or implement AI-EVAL-003. Keep Python >=3.12 and verified 3.14.7;
stop on actual dependency incompatibility. Pin dependencies with uv.lock.

Pass only parsed synthetic Event fields to the model. Never expose expected
labels, fixture tags/ids, baseline predictions or scorer results. Convert strict
structured intervals into the existing tuple of Trip objects in the adapter.
Reject malformed/invalid output as execution errors, never incorrect predictions.

Keep baseline/default pytest offline and credential-free. Explicit CLI system
selection must preserve the existing baseline command. Compare only identical
dataset name/version/hash, cases and scorer settings, including IoU threshold.
Report cases/errors/TP/FP/FN/precision/recall/F1/mean boundary error, signed LLM
minus baseline deltas and incorrect-case differences in text and JSON. Never
declare a winner from a single metric. Generated results stay ignored.

Fail missing-key configuration before any cases run. Finite provider timeout,
at most three provider attempts per case for 429, Retry-After-aware modest
bounded backoff; exhausted retries/timeouts are case errors and processing
continues. No SDK nested retries. Optional usage totals must not affect scoring
or cause errors. Never request/persist reasoning traces, secrets or raw headers.

Record provider/model/prompt/structured-output method/generation settings/package
versions. Experiment identity is repeatable; LLM bytes are not guaranteed even at
temperature zero. Default tests fake the provider; live calls only by explicit
request. Live acceptance requires all 16 frozen cases and actual JSON comparison.
Unavailable provider means BLOCKED/NEEDS_DECISION, never fabricated metrics.

Quality gates: uv sync, uv run python -m pytest, uv run ruff check .,
uv run ruff format --check ., frozen offline baseline and explicit live evaluation
when credentials are available. Self-review; commit and push only the feature
branch; create PR if possible, never merge.

## Pre-flight evidence

- Clean feature/ai-eval-002 at 4e2cf363592767386d695101283c96629677233d,
  identical to freshly fetched origin/main.
- Python 3.14.7; original offline baseline: 16 cases, zero errors, TP 8, FP 1,
  FN 2, precision 0.8889, recall 0.8000, F1 0.8421, boundary error 0.125 days.
- GROQ_API_KEY absent. Live acceptance pending; no provider metrics claimed.

## Implementation and final validation

- Added GroqTripDetector v1.0.0 implementing the existing Predictor contract.
  Lazy plugin selection preserves offline baseline operation. The engine,
  TripScorer, baseline and frozen dataset are unchanged.
- Direct runtime dependency: langchain-groq 1.1.3 (uv.lock); LangChain Core
  1.6.6, Groq SDK 0.37.1, Pydantic, HTTP and LangSmith are normal transitives.
  No monolithic langchain, LangGraph or direct SDK application integration.
- Native strict json_schema, every object required/closed; Event-only stable
  JSON in ChatPromptTemplate; parse and validate existing Trip values locally.
- Frozen prompt trip-detection-groq-v1, model openai/gpt-oss-20b, temperature 0,
  max_tokens 2048, reasoning_effort low, include_reasoning false. ChatGroq
  normalizes constructor temperature 0 to 1e-8; the adapter restores 0 after
  construction. An offline real-wrapper HTTP test verifies actual wire settings.
- 45-second attempt timeout, SDK retries off, three attempts maximum for 429,
  Retry-After seconds/HTTP dates, fallback waits 2/4 seconds, three seconds
  between cases. Retry-After over 60 seconds aborts the case without early retry.
- Numeric usage only, optional/partial coverage recorded separately. Raw messages,
  exception text, keys and reasoning are not persisted; ambient tracing disabled.
- CLI selects --system baseline/groq; compare reads two saved JSON runs and
  verifies dataset/hash/cases/labels/scorer/threshold/harness/error-policy parity.
  Exports both runs, metrics, signed deltas and incorrect-case statuses.
- Changed files: this task; lab README, pyproject.toml, uv.lock, __main__.py,
  trip plugin; new core/comparison.py, groq_detector.py and test_groq_detector.py.
- Quality gates: uv sync passed; 115 offline tests passed; ruff check and
  ruff format --check passed. No provider calls from default tests, including
  native-wrapper tests using fake HTTP success and timeout responses.
- Final offline baseline: 16 cases, zero errors; TP 8, FP 1, FN 2,
  precision 0.8888888888888888, recall 0.8, F1 0.8421052631578947,
  start/end/mean boundary error 0.125 days. Export: ignored
  tools/ai-evals/results/trip-baseline.json.
- Frozen v1 SHA-256:
  025ae1009030c1ce4aaea5ca2e4e25978f167457cc23f8eeda477c87c4546890.
- Groq CLI pre-flight returned configuration error (exit 2): GROQ_API_KEY absent,
  before any cases. **BLOCKED**: no live Groq metrics, usage or comparison
  claimed; live acceptance is still required. No credentials requested in chat.
- Self-review: production changes NONE; frozen files unchanged; results ignored;
  no secrets added; no AI-EVAL-003 changes. Feature delivery may proceed as draft
  while live acceptance remains blocked. No merge.

## Risks and next step

Free account status cannot be verified by the adapter. User must supply a Free
account key via environment before live acceptance. Small authored synthetic
sample and matched-only boundaries limit interpretation; probabilistic outputs
are not byte-reproducible. Error-excluded aggregates require coverage inspection.
Run the README baseline/Groq/compare workflow once access is available, preserve
actual results including failures, and record metrics here without fixture-driven
prompt tuning. Only then consider AI-EVAL-003 prompt/context/model comparisons.
