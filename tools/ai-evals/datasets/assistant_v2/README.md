# `assistant_v2` — synthetic Italian evaluation of the protocol-2 interpreter (AI-008 G1/G2)

Measures how a model interprets Italian messages into protocol v2
(`docs/contracts/assistant-interpretation/v2`: Finance, water intakes, meals) through the **production**
OpenAI adapter (`lifeos_ai/assistant/openai_interpreter.py`, `prompt_version_v2`, prompt
`assistant-interpret-v3`). Every message, name, quantity and food is invented. No real data may be added here.

| Dataset | Content |
| --- | --- |
| G1 `assistant_finance` v1 | The frozen 70 Finance cases of `datasets/assistant_finance/v1.json`, **converted** to protocol 2 by `evaluators/assistant_v2/adapt.py` (same name, version and SHA-256; no file here) |
| G1 `assistant_finance_holdout` v1 | The frozen 68 held-out Finance cases of `datasets/assistant_finance/holdout-v1.json`, converted the same way |
| G2 `hydration-meal-v1.json` | 60 cases (`assistant_hydration_meal` v1, frozen: `gates.HYDRATION_MEAL_SHA256`): 25 water, 25 meals, 10 edge cases |
| G2 `hydration-meal-holdout-v1.json` | 20 **held-out** cases (`assistant_hydration_meal_holdout` v1, frozen: `gates.HYDRATION_MEAL_HOLDOUT_SHA256`), written before any model saw them; never used to write or tune a prompt |

The conversion is mechanical (the v2 contract's "What changed from v1"): the open draft moves to
`context.open_draft`, the category lists stay in `context.finance`, `finance_draft_*` intents become
`draft_*`, `multiple_transactions` becomes `multiple_operations`, and every draft-changing expectation scores the
`finance` section. Messages, fields, values and states are unchanged.

Adapter `assistant-finance-v1-to-v2-2` (2026-10-31, after the first live run, owner approval) replaces one
expectation that protocol 2 makes obsolete: `h_other_capability_meal` logs a meal, `unsupported` in protocol 1
and a `nutrition_meal` draft in protocol 2 (`adapt.PROTOCOL_2_OVERRIDES`). A saved live run is re-scored
offline with `python -m lifeos_ai_evals.evaluators.assistant_v2.rescore <run.json> --dataset <name>`.

G2 tags: `hydration`, `meal`, `edge`, `unit` (ml, cl, l, spelled out), `amount_format` (decimal comma,
"un litro e mezzo", "tre quarti di litro"), `container` (a glass or bottle without a size leaves quantity and unit
absent), `no_unit`, `relative_date`, `absolute_date`, `time`, `telegraphic`, `typo`, `verbatim` (the description
keeps the user's words, capitals and typos), `names`, `other_drink` (coffee, juice, tea are meals),
`multiple_foods`, `missing_field`, `not_invented`, `open_draft`, `draft_update`, `clear`, `confirm`, `cancel`,
`question`, `negation`, `other_capability`, `multiple_operations`, `new_record_with_open_draft`, `wrong_module`,
`injection`. A request with an open water or meal draft carries empty Finance category lists, as .NET sends it.
No G2 message repeats another or quotes an example of prompt v3 (tested).

## Expectations and scoring

Per case: the expected `intent` (plus acceptable alternatives), the `section` it fills (`finance`, `hydration`,
`meal`; null for an intent that changes no draft), the scored `fields` of that section, `must_be_absent`,
optional `ambiguous_fields` and `accept_refusal`. Format: `src/lifeos_ai_evals/evaluators/assistant_v2/model.py`.

Severity (`scoring.py`, `assistant-v2-severity` 1.0.0): the Finance rules of `assistant_finance` unchanged, plus:

- **Critical** — a record of the wrong module (juice recorded as water, a meal as an expense); a new record
  written over another module's open draft; a wrong or invented water quantity, unit, date or time; an invented
  meal description, a wrong or invented meal type, date or time; a stated date lost.
- **Minor** — a missing quantity, unit, description or meal type (LifeOS asks); a description worded
  differently; a different non-draft intent.

## Gates

`assistant-v2-gates-v1` (`gates.py`): the thresholds of `assistant-finance-gates-v2`, unchanged, applied to the
four frozen datasets with the owner's approval of 2026-10-31 (runbook Part I, G1/G2). A partial or changed run is
`invalid`. A failed gate is never fixed by editing a dataset or a threshold in place.

## Commands

```powershell
# Offline (no network, no key): the reference answers through the production adapter.
uv run --offline --locked python -m lifeos_ai_evals evaluate assistant_v2
uv run --offline --locked python -m lifeos_ai_evals evaluate assistant_v2 --dataset datasets/assistant_v2/hydration-meal-holdout-v1.json
uv run --offline --locked python -m lifeos_ai_evals evaluate assistant_v2 --dataset datasets/assistant_finance/v1.json

# Live (owner's explicit go only): a dry run first, then --execute.
uv run python -m lifeos_ai_evals.evaluators.assistant_v2.live_run `
    --experiment experiments/assistant_v2/candidate-gpt-6-luna.json `
    --dataset hydration-meal-v1 --max-cost-usd 0.45
```

The offline fake proves that every case is satisfiable under protocol 2 and that the adapter, scorer and gates
agree; it **measures no model**.
