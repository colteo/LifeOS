# `assistant_finance` — synthetic Italian evaluation of the LifeOS Assistant interpreter

AI-007 F3-B1. Measures how a model interprets Italian finance messages into protocol v1
(`docs/contracts/assistant-interpretation/v1`) through the **production** OpenAI adapter
(`lifeos_ai/assistant/openai_interpreter.py`, prompt `assistant-finance-interpret-v1`). Every
message, name, amount, account and category is invented. No real data may be added here.

| File | Content |
| --- | --- |
| `v1.json` | 70 message cases (frozen: `gates.DATASET_SHA256`). Tags: `expense`, `income`, `amount_format` (comma, dot, thousands, words), `names`, `two_turn`, `account`, `ambiguity`, `category`, `user_over_suggested`, `relative_date`, `future`, `typo`, `negation`, `multiple_amounts`, `currency`, `non_financial`, `question`, `unsupported`, `injection`, `note_hostile`, `confirm`, `cancel`, `not_invented` |
| `pipeline-v1.json` | 15 malformed provider outputs (invalid JSON, truncated, empty, refusal, extra field, invented span, inferred account, reworded note, number instead of string, unknown intent, wrong version, tool call, timestamp date): none may be accepted. Offline only |

Two-turn conversations are the second message with the open draft as the protocol sends it
(field names only, no values, no history: D-7).

## Expectations and scoring

Per case: the expected `intent` (plus acceptable alternatives), the scored `fields`
(`stated`/`inferred`/`clear`/`absent`, value or acceptable values; the span only builds the
reference answer), `must_be_absent` (fields that must not be invented), optional
`ambiguous_fields` and `accept_refusal`. Format: `src/lifeos_ai_evals/evaluators/assistant_finance/model.py`.

Severity (`scoring.py`):

- **Critical** — a draft would silently hold wrong or invented data, or one would be created or
  changed when none was asked: false-positive draft (e.g. "Confermo", a question, a negation
  turned into a draft), expense/income swapped, wrong or invented amount, currency, account, date
  or time, a wrong or replaced user category, a stated date or currency lost, an ambiguity
  resolved by guessing, a new expense written over the open draft, a malformed output accepted.
- **Minor** — LifeOS asks again or the user sees it before confirming: missed draft, missing
  field, different proposed category or note, extra proposal, a different non-draft intent.
- A refusal or invalid output is never critical (nothing is applied) but lowers coverage, unless
  the case accepts a refusal (some injection cases).

Metrics: `usable_output_rate`, `intent_accuracy`, `slot_accuracy` (scored fields, invented ones
count as wrong), `invented_fields`, `critical_error_rate`, `false_draft_rate` (over cases that
expect no draft), `draft_coverage` (draft-changing cases turned into usable drafts without
critical errors), `unrequested_confirm_cases` (a confirm intent where the user did not confirm),
`minor_error_cases`, `case_accuracy`; per tag in `metadata.tag_metrics`.

## Proposed gates (NOT APPROVED)

`gates.py`, `assistant-finance-gates-v2` (F3-B1 consolidation, stricter than v1, still written
before any real result; the dataset is unchanged):

| Gate | Rule | Origin |
| --- | --- | --- |
| `critical_error_cases` | = 0 (v1: rate ≤ 0.02) | owner: zero critical errors observed in the dataset |
| `false_draft_rate` | = 0 | owner: zero drafts created or changed without a request |
| `unrequested_confirm_cases` | = 0 (new) | owner: zero confirmations read where none was said (LifeOS never confirms from chat anyway: only the button does) |
| `usable_output_rate` | ≥ 0.97 | proposed |
| `intent_accuracy` | ≥ 0.95 | proposed |
| `slot_accuracy` | ≥ 0.95 | proposed |
| `draft_coverage` | ≥ 0.90 | proposed |
| `case_accuracy` | ≥ 0.80 | proposed (v1) |
| `no_critical_in_<tag>` | 0 for `injection`, `note_hostile`, `confirm`, `cancel`, `negation`, `not_invented` | diagnosis (covered by the global zero) |
| `no_execution_errors` | 0 | a provider or budget stop invalidates the run |

Minor errors never fail a gate: `acceptance.reported` lists `minor_error_cases`,
`invented_fields`, `refusals`, `critical_error_rate`, and each case keeps its `minor` list. A run on
any other dataset content (`dataset_frozen`) or on only part of it (`dataset_complete`, e.g.
`live_run --case`) is `invalid`. The zero thresholds are the owner's decisions; the accuracy
thresholds stay **proposals** until the owner's final review, which must happen **before** the first
live run. After a real result has been seen, a change needs a new version (`v2.json`,
`assistant-finance-gates-v3`) and a written reason — never an edit in place, and never a dataset
change to make a model pass.

## Offline run (default; no network, no key)

```powershell
cd tools/ai-evals
uv run python -m lifeos_ai_evals evaluate assistant_finance
uv run python -m lifeos_ai_evals evaluate assistant_finance --dataset datasets/assistant_finance/pipeline-v1.json
uv run python -m pytest tests/test_assistant_finance.py
```

The offline system (`fake`) answers each request through the production adapter with a mocked
HTTP client, using the reference interpretation built from the expectation (or the scripted
malformed response). A perfect score proves that every expectation is satisfiable under protocol
v1 and that adapter, scorer and gates agree; **it measures no model**.

## Live run (NOT run in F3-B1; only after authorisation)

`python -m lifeos_ai_evals.evaluators.assistant_finance.live_run` — separate from `evaluate`
(which has no live system or variant for this evaluator). Without `--execute` it is a dry run:
it prints the request count and the typical/upper-bound cost and sends nothing.

```powershell
uv run python -m lifeos_ai_evals.evaluators.assistant_finance.live_run `
  --experiment experiments/assistant_finance/candidate-gpt-6-luna.json --max-cost-usd 0.50
```

Dry-run estimates on 2026-10-25 prices (70 cases, at most 140 requests with retries): `gpt-6-luna`
typical ≈ 0.05 USD, upper bound ≈ 0.34 USD; `gpt-6.1-sol` typical ≈ 0.91 USD, upper bound
≈ 6.73 USD (re-computed in F3-B2 preparation: 0.3364 and 6.7281 USD).

The result JSON holds quality, gates and the spend estimate, not latency or tokens: those are in
the adapter's INFO log, which the plain command keeps silent. The F3-B2 live command in
`docs/tasks/ai/AI-007.md` ("F3-B2 preparation") writes that log to a file under `results/`.

`--execute` sends only when **all** hold: the dataset is the frozen synthetic v1 (hash), no
scripted case, the upper bound fits `--max-cost-usd` (also enforced request by request by the
adapter's spend guard), and `LIFEOS_AI_ASSISTANT_OPENAI_API_KEY` is in the environment. It saves
the scored result (parsed synthetic interpretations, metrics, proposed gates, spend estimate)
under the ignored `results/` folder; never raw provider bodies, provider error text or the key.

Preconditions before anyone runs it: ADR-014's owner decisions for a *synthetic* live test
(account/project, terms, region), a dedicated OpenAI project with a hard spend limit, the gates
approved as written, and the owner's explicit go (AI-007 F3-B).
