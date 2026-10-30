# Quick Capture Meal specialist contract, v1

> **Status: PROPOSED (AI-009 I0).** Not implemented. Shared conventions: [Quick Capture contracts](../../README.md).

Interprets **one segment** as one meal of the text meal journal (`nutrition.meal.create`), to create or correct a
draft. The `meal` section is a verbatim copy of [`assistant-interpretation/v2`](../../../assistant-interpretation/v2/README.md)'s
(`description` — the user's own words; `meal_type` ∈ `breakfast`, `lunch`, `dinner`, `snack`, `other`; `date`;
`time`); rule codes keep their v2 `meal_` prefix. No ingredient, quantity or nutrition value can be added, and no
nutrition estimate is requested (as AI-008).

## Request

`mode` (`create` | `correct`), `segment`, `context_spans` (≤ 3), `context.locale`, `context.draft` (null in
`create`; filled/missing **field names** of the target draft in `correct`). No meal history, target plan, estimate
or Finance categories.

## Output

`outcome` ∈ `interpreted`, `unclear`, `wrong_domain`, `multiple_operations`; `meal` (v2 section);
`ambiguous_fields` (`description`, `meal_type`, `date`, `time`).

## Rules (in order)

| # | Code | Rule |
| --- | --- | --- |
| 1 | `protocol_version` | must be 1 |
| 2 | `ambiguous_fields` | only this section's field names, no duplicates |
| 3 | `outcome_fields` | an outcome other than `interpreted` has every field `absent` and no ambiguous field |
| 4 | `clear_on_create` | no `clear` state in `create` mode |
| 5 | v2 Meal codes | `meal_description_*` (stated only, never cleared), `meal_description_not_verbatim` (verbatim **in the segment**, case included), `meal_meal_type_*` (stated or cleared, closed values), `meal_date_*`, `meal_time_*`, with "message" read as the authorised text (segment + each context span) |

A description found only in a context span is refused (`meal_description_not_verbatim`): context spans carry shared
dates and times, not the content of another operation.

## Cases

`cases.json`: 5 valid (type and context date, description only, type correction, clearing the type, multiple
operations), 2 shape refusals (a `calories` member, an unknown state), 7 rule violations (case changed, description
from a context span, inferred description, meal type inferred from the time, Italian meal type value, a date from
another segment, `clear` on create). `request.cases.json`: 1 accepted, 8 refused.
