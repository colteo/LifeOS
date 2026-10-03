# AI-EVAL-003 — Trip benchmark variants and frozen v2

Status: complete; offline validation and live V2 acceptance completed on
feature/ai-eval-003.
Research/evaluation only. Production changes: NONE. No database or real data.

## Implemented

V1 dataset, deterministic baseline, Groq v1 detector/prompt and TripScorer
remain unchanged. V2 contains 60 synthetic cases: 30 positive, 30 negative,
35 expected trips. Composition covers all requested travel and false-positive
classes, with overlapping tags and explicit descriptions/truth. It was authored
and frozen without observing any candidate-model results. Comparison SHA-256
(CRLF normalized to LF, strategy `sha256-lf-v1`):
4644e129ad5dd5c939e2003ac59f812f6550f52a254b6600dda5ad04754964c1.
The same identity is used on Windows CRLF and Linux LF checkouts.

A provider-neutral ExperimentVariant contract and adapter registration record
provider, model, prompt/context versions, output method and generation settings.
The CLI accepts --variant JSON; baseline and Groq legacy commands still work.
One candidate, trip-detection-v2, addresses general AI-EVAL-002 failure classes.
Context remains events-v1. Control retains trip-detection-groq-v1 and
openai/gpt-oss-20b. Model selection is configurable; no second SDK was added.
Unknown provider adapters fail before cases or network calls.

Comparison accepts two or more saved runs, checks dataset identity/hash, case
ids/labels/tags and scorer configuration including IoU, and reports all runs,
coverage, signed metrics/coverage deltas, incorrect cases, execution errors and
tag breakdowns. No winner is selected. Boundaries remain matched-trip metrics.
Variant telemetry includes request-attempt counts, latency summaries and
per-token-field usage totals/coverage. Missing usage/cost is never fabricated.

## Tests and baseline acceptance

See tools/ai-evals/README.md for exact setup, offline, live and comparison
commands, variant extension instructions and anti-overfitting rules.
Normal pytest uses fakes and requires no credentials/network. Live provider
calls are not required for automated validation. Initial implementation was
validated offline; completed live V2 acceptance is recorded below.

| Metric | Frozen v1 baseline | V2 baseline |
|---|---:|---:|
| Cases / scored | 16 / 16 | 60 / 60 |
| Execution errors | 0 | 0 |
| TP / FP / FN | 8 / 1 / 2 | 23 / 6 / 12 |
| Precision | 0.888889 | 0.793103 |
| Recall | 0.800000 | 0.657143 |
| F1 | 0.842105 | 0.718750 |
| Matched mean start error, days | 0.125000 | 0.043478 |
| Matched mean end error, days | 0.125000 | 0.130435 |
| Matched mean absolute boundary error, days | 0.125000 | 0.086957 |

No new packages, lockfile changes, migrations or production builds are needed.
Changed files: v2 dataset; two experiment configurations; generic experiment,
engine/comparison/CLI modules; trip plugin and separate variant adapter;
experiment regression tests; README and this task record.

## Blocking issues

None. Offline validation and live V2 acceptance are complete. Alternative-model
access and real-world quality remain unmeasured.

## Deferred improvements

Independent synthetic holdout and repeated runs for uncertainty estimates;
additional provider adapters only when concretely required and credentialed.
Do not alter v2 after model results or tune on individual fixtures. A new
prompt/context requires a future task/version. No overall superiority claim.

## Validation results

Python 3.14.7. `uv sync --locked` passed; `uv run python -m pytest`:
138 passed; `uv run ruff check .` and `uv run ruff format --check .` passed.
Offline v1/v2 baseline repeat runs and three-run saved comparison passed.
Frozen control content hashes are tested. No live requests were executed.

## Reproducibility correction before live acceptance

Dataset loading still validates the original JSON and rejects duplicate keys,
invalid constants and malformed payloads. Only after validation does hashing
replace CRLF with LF; it does not reserialize JSON, sort cases or discard any
semantic fields. Formatting beyond checkout line endings remains significant.
Frozen v1/v2 files, prompts, detectors and scorer are not modified.

`dataset.sha256` is now the LF-normalized comparison identity. Additive
`metadata.dataset_hash` records the hash strategy and equivalent LF/CRLF raw
hashes for compatibility with older saved results. New-new comparisons require
the same normalized hash; new-old comparisons require the old raw hash to match
one of those exact equivalents. Old-old comparisons keep raw-hash matching.
Unknown strategies, unrelated hashes and all existing case/scorer mismatches
remain rejected. No raw checkout hash is used as a new comparison identity.
Result schema/harness versions remain unchanged for AI-EVAL-001/002 compatibility.

Tests cover LF/CRLF comparison and old-result compatibility for both frozen
versions, semantic changes and case order, plus duplicate/malformed JSON under
both line endings. Full pytest, Ruff and offline v1/v2 baseline regressions
are rerun for this correction. No provider calls are executed.

Correction validation: 159 pytest tests passed; Ruff check and format check
passed. Both baseline aggregate metric dictionaries match pre-fix exports
exactly, and actual saved pre-fix v1/v2 exports compare successfully with new
exports. Only core hashing/comparison, identity tests and documentation changed.

## Completed live V2 acceptance

Live acceptance is complete. The following results were supplied and accepted
by the user for the frozen V2 dataset and unchanged TripScorer (inclusive-day
IoU >= 0.5). Both Groq variants use `openai/gpt-oss-20b`, `events-v1` context,
strict `json_schema` output and their checked-in generation settings. Control
uses `trip-detection-groq-v1`; candidate uses `trip-detection-v2`. No additional
provider calls were made during finalization, and no dataset, prompt, scorer or
evaluation behavior was changed.

| Metric | Baseline | Groq control-v1 | Groq candidate-v2 |
|---|---:|---:|---:|
| Cases | 60 | 60 | 60 |
| TP / FP / FN | 23 / 6 / 12 | 34 / 0 / 1 | 35 / 0 / 0 |
| Precision | 0.793103 | 1.0 | 1.0 |
| Recall | 0.657143 | 0.971429 | 1.0 |
| F1 | 0.718750 | 0.985507 | 1.0 |
| Matched mean absolute boundary error, days | 0.086957 | 0.029412 | 0.028571 |
| Execution errors | 0 | 0 | 0 |
| Provider requests / attempts | Not applicable | 60 requests | 61 attempts |
| Total tokens | Not applicable | 33385 | 39684 |
| Mean provider-attempt latency, seconds | Not applicable | 0.413125 | 0.480790 |

Candidate-v2 has perfect trip-level precision, recall and F1 under IoU >= 0.5.
`uncertain-arrival` and `uncertain-return` remain case-level incorrect because
their predicted boundaries are not exact. A qualifying interval match earns a
trip-level TP even when its boundaries differ, so boundary metrics remain
non-zero. Boundary means describe matched trips only. The candidate's 61
provider attempts cover 60 cases; attempt count is separate from execution
errors and must not be presented as 61 cases.

These results make no overall production-quality claim and do not designate an
overall winner. V2 is synthetic and is not an independent holdout. No further
prompt tuning should be performed against V2. Both datasets and both prompts
remain frozen; any future changes require a separate task/version and independent
evaluation data. Provider cost is not inferred from token totals.

Finalization validation completed offline: 159 pytest tests passed; Ruff check
and Ruff format check passed. V1 baseline remains TP/FP/FN 8/1/2, F1 0.842105,
matched mean absolute boundary error 0.125000 days and zero execution errors.
V2 baseline remains TP/FP/FN 23/6/12, F1 0.718750, matched mean absolute boundary
error 0.086957 days and zero execution errors. Live acceptance results are
documented from the supplied measurements without rerunning either model.
