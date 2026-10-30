# Quick Capture Hydration specialist contract, v1

> **Status: PROPOSED (AI-009 I0).** Not implemented. Shared conventions: [Quick Capture contracts](../../README.md).

Interprets **one segment** as one water intake (`hydration.intake.create`), to create or correct a draft. The
`hydration` section is a verbatim copy of [`assistant-interpretation/v2`](../../../assistant-interpretation/v2/README.md)'s
(`amount` as stated, `unit` ∈ `ml`/`cl`/`l`, `date`, `time`); the rule codes keep their v2 `hydration_` prefix.
.NET converts to whole millilitres (1–4000) itself and never converts a container ("un bicchiere"): the quantity is
asked (AI-008 decisions, unchanged).

## Request

`mode` (`create` | `correct`), `segment`, `context_spans` (≤ 3), `context.locale`, `context.draft` (null in
`create`; filled/missing **field names** of the target draft in `correct`). No Finance categories, no water history,
daily total, target or quick-add settings (as v2).

## Output

`outcome` ∈ `interpreted`, `unclear`, `wrong_domain`, `multiple_operations`; `hydration` (v2 section);
`ambiguous_fields` (`amount`, `unit`, `date`, `time`).

## Rules (in order)

| # | Code | Rule |
| --- | --- | --- |
| 1 | `protocol_version` | must be 1 |
| 2 | `ambiguous_fields` | only this section's field names, no duplicates |
| 3 | `outcome_fields` | an outcome other than `interpreted` has every field `absent` and no ambiguous field |
| 4 | `clear_on_create` | no `clear` state in `create` mode |
| 5 | v2 Hydration codes | `hydration_amount_*` (stated only, never cleared, decimal format), `hydration_unit_*` (stated only, `ml`/`cl`/`l`), `hydration_date_*`, `hydration_time_*`, with "message" read as the authorised text (segment + each context span) |

## Cases

`cases.json`: 6 valid (ml, half litre with a context date, container left absent, correction of the awaited
quantity, clearing the time, wrong domain), 3 shape refusals (a Finance section, a `target` member, `transfer` as
outcome), 6 rule violations (quantity inferred from a container, a container as unit, a quantity taken from another
segment, `clear` on create, fields on `unclear`, time without minutes). `request.cases.json`: 1 accepted, 8 refused.
