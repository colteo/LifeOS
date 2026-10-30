# Quick Capture Finance specialist contract, v1

> **Status: PROPOSED (AI-009 I0).** Not implemented. Shared conventions: [Quick Capture contracts](../../README.md).

Interprets **one segment** chosen by the router as one expense or income (`finance.transaction.create`), either to
create a draft or to correct one. It reuses the v2 `finance` section **unchanged** (same eight fields, states,
formats and rule codes: the section schema is a verbatim copy of
[`assistant-interpretation/v2`](../../../assistant-interpretation/v2/README.md)'s). It knows nothing about water,
meals, other segments or the drafts' values, and it can do nothing: .NET resolves the account mention and the
category against the user's own data and writes only on the explicit Confirm.

## Request (`request.schema.json`)

| Member | Content |
| --- | --- |
| `mode` | `create` (router action `create`) or `correct` (router action `correct`, after .NET resolved the target draft) |
| `segment` | the router's segment span (1–1000) |
| `context_spans` | the router's context spans for it (≤ 3, each ≤ 200) |
| `context.locale` | `it-IT` |
| `context.draft` | null in `create`; in `correct`, the target draft's `type` (`expense`/`income`/null) and the **names** of its filled and missing fields. Never values, never ids |
| `context.finance` | the user's expense and income category names (≤ 100 each, ≤ 200 code points), as v2 |

Not sent: the message, other segments, other drafts, account names, ids, history. `mode` ↔ `draft` consistency is
in the schema (`if/then`).

## Output (`interpretation.schema.json`)

| Member | Values |
| --- | --- |
| `outcome` | `interpreted`; `unclear` (no expense/income can be read); `wrong_domain` (the segment is not Finance: a router error); `multiple_operations` (more than one transaction in the segment: a router error); `transfer` (a transfer: unsupported, as v2) |
| `transaction_type` | `expense` / `income` in `create` mode (replaces v2's `finance_expense` / `finance_income` intents); null in `correct` mode, where a change of type is `finance.type` stated, as v2 `draft_update` |
| `finance` | the v2 section: `type`, `amount`, `currency`, `account`, `category`, `note`, `date`, `time` |
| `ambiguous_fields` | names of this section's fields only |

## Rules (in order)

| # | Code | Rule |
| --- | --- | --- |
| 1 | `protocol_version` | must be 1 |
| 2 | `ambiguous_fields` | only this section's field names, no duplicates |
| 3 | `outcome_fields` | an outcome other than `interpreted` has every field `absent`, no ambiguous field and `transaction_type` null |
| 4 | `transaction_type_mode` | `interpreted` in `create`: `transaction_type` required; in `correct`: null |
| 5 | `type_contradicts_intent` | `finance.type` stated must not contradict `transaction_type` (v2 code) |
| 6 | `clear_on_create` | no `clear` state in `create` mode |
| 7 | v2 Finance codes | `type_*`, `amount_*`, `currency_*`, `account_*`, `category_*`, `note_*`, `note_not_verbatim`, `date_*`, `time_*`, in v2 order, with "message" read as the **authorised text** (segment + each context span); the note must be verbatim in the segment |

Same field permissions as v2: `type` and `amount` stated only; `currency` stated or cleared; `account` stated or
cleared (never inferred); `category` stated, inferred (from the supplied names) or cleared; `note` stated, inferred or
cleared; `date`/`time` stated or cleared. Amount format `[0-9]{1,15}(\.[0-9]{1,4})?`, currency `[A-Z]{3}`.

## Mapping to the item outcome (.NET, proposed in AI-009)

`interpreted` → the draft is created or corrected (missing fields leave it `Collecting`, G1 R13); `transfer`,
`wrong_domain`, `multiple_operations`, `unclear` → the item ends without a draft change and with a fixed reply;
HTTP/provider failure, `"shape"` or a rule code → the item is `Failed` and retryable for 24 h (G1 D4) while the
other items of the message keep their results (G1 R14).

## Cases

`cases.json`: 11 valid (create with a date from a context span, income, account mention, corrections, clearing,
ambiguity, the four non-interpreted outcomes), 5 shape refusals (an id member, another module's section, a missing
section, an unknown outcome, a numeric amount), 17 rule violations — among them an amount whose span belongs to
**another segment** of the original message, a note taken from a context span and a span crossing context and
segment. `request.cases.json`: 1 accepted, 8 refused.
