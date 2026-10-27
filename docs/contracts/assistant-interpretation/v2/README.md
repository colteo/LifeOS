# LifeOS Assistant interpretation contract, v2

The canonical contract between LifeOS (.NET, `AssistantInterpreterClient` + `AssistantInterpretationValidator`)
and `lifeos-ai` (Python, `lifeos_ai/assistant/schema_v2.py`) for `POST /v1/assistant/interpret` with
`protocol_version: 2` (AI-008). Both test suites check themselves against these files; neither
implementation is the reference.

| File | Content | Checked by |
| --- | --- | --- |
| `interpretation.schema.json` | the strict JSON schema of the model's output (every property required, `additionalProperties: false`, nullable instead of optional) | Python: equal to `schema_v2.INTERPRETATION_JSON_SCHEMA`; .NET: names, enum values and nullability equal to the records/enums |
| `protocol.json` | versions, locale, limits (lengths in Unicode code points), HTTP error codes and the .NET failure each maps to | both: constants; Python: the endpoint returns exactly these errors; .NET: the client maps each one |
| `request.example.json` | a minimised request (no id, no value; an open water draft waiting for its quantity) | .NET: what the client sends is this JSON; Python: accepted unchanged |
| `cases.json` | golden interpretations with the expected verdict: `null` (valid), `"shape"` (refused while reading) or a rule code | both: same verdict, same code |

Tests: `src/python/lifeos-ai/tests/test_assistant_contract_v2.py`,
`tests/dotnet/LifeOS.UnitTests/Assistant/AssistantContractTests.cs`.

## What changed from v1

- **Sections.** `finance` is v1's, unchanged (same fields, states and rule codes). New: `hydration`
  (`amount` as stated in `unit` — `ml`, `cl` or `l`; `date`; `time`) and `meal` (`description`, the
  user's own words; `meal_type` — `breakfast`, `lunch`, `dinner`, `snack`, `other`; `date`; `time`).
  Every section is always present; only the intent's section may be filled (`section_mismatch`: a new
  record fills only its own section, a `draft_update` at most one).
- **Intents.** `hydration_intake` and `nutrition_meal` start a record; `finance_draft_update`,
  `finance_draft_confirm` and `finance_draft_cancel` became `draft_update`, `draft_confirm` and
  `draft_cancel`, which act on whatever draft is open. `unsupported_reason` `multiple_transactions`
  became `multiple_operations` (several records of any module in one message).
- **Rules of the new sections.** Nothing is inferred: a quantity, a unit, a description and a meal
  type are only ever stated, with a span found in the message. A stated description is verbatim
  (case included), so no ingredient, quantity or nutrition can be added. A container ("un
  bicchiere") is not a quantity: amount and unit stay absent and LifeOS asks. Hydration and meal
  codes carry their section's prefix (`hydration_amount_span`, `meal_description_not_verbatim`).
- **Request.** `context.open_draft` (with its `kind`: `finance.transaction.create`,
  `hydration.intake.create`, `nutrition.meal.create`) moved to the top of the context;
  `context.finance` keeps only the category names (empty while another module's draft is open). The
  new modules add no context: no history, no target, no settings, no nutrition.

## Compatibility

The service answers `protocol_version: 1` requests exactly as before (v1 schema, the configured v1
prompt) and `protocol_version: 2` requests with the v2 schema and `assistant-interpret-v3`. .NET (AI-008)
sends v2 only: an older service answers it 422 `invalid_request`, which .NET reports as
`assistant_invalid_output` (nothing is applied). Deploy the service first.

Changing the protocol: edit these files first (a new incompatible version goes in `v3/`), then make
both sides pass. Synthetic data only.
