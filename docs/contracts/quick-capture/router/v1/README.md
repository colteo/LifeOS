# Quick Capture router contract, v1

> **Status: PROPOSED (AI-009 I0).** Not implemented. Shared conventions: [Quick Capture contracts](../../README.md).

The router reads **one user message** and splits it into **segments**: one per operation the user asked for, in
message order. It decides only *what kind of thing* each segment is (module + action) and *which words* belong to
it. It does not extract field values (that is the specialists' job), does not see draft values or identifiers, and
cannot choose an endpoint, a command or a query: .NET maps `module` + `action` through a fixed table.

## Request (`request.schema.json`)

```json
{
  "protocol_version": 1,
  "message": "Ieri ho speso 12 euro per la pizza, ho bevuto mezzo litro d'acqua e a cena ho mangiato pasta al pomodoro",
  "context": {
    "locale": "it-IT",
    "max_segments": 5,
    "open_drafts": [{ "kind": "finance.transaction.create", "count": 2, "missing_fields": ["account"] }],
    "awaiting": null
  }
}
```

| Member | Content |
| --- | --- |
| `message` | the user's message as typed, 1–1000 code points, not blank |
| `context.max_segments` | the configured limit (1–5): the router lists at most this many segments |
| `context.open_drafts` | at most one entry **per kind** of open draft in the conversation: `count` (1–10) and the **union** of the missing field names of the drafts of that kind. No identifier, no value, no order. Sum of counts ≤ 10 |
| `context.awaiting` | `{kind, field}` when the last assistant reply asked exactly one question about exactly one draft (e.g. the quantity of a water draft), else null. The kind must be open and the field among its missing fields |

Request rules beyond the schema (HTTP 422 `invalid_request`): `message_blank`, `open_drafts_kind_unique`,
`open_drafts_total`, `awaiting_not_open`, `awaiting_not_missing`. See `request.cases.json`.

## Output (`interpretation.schema.json`)

```json
{
  "protocol_version": 1,
  "segments": [
    { "module": "finance",   "action": "create", "span": "ho speso 12 euro per la pizza",        "context_spans": ["Ieri"], "reference_span": null, "out_of_scope_reason": null },
    { "module": "hydration", "action": "create", "span": "ho bevuto mezzo litro d'acqua",        "context_spans": ["Ieri"], "reference_span": null, "out_of_scope_reason": null },
    { "module": "meal",      "action": "create", "span": "a cena ho mangiato pasta al pomodoro", "context_spans": ["Ieri"], "reference_span": null, "out_of_scope_reason": null }
  ],
  "truncated": false
}
```

### Segment members

| Member | Values | Meaning |
| --- | --- | --- |
| `module` | `finance`, `hydration`, `meal`, null | the domain. Required for `create` and `correct`; may be null for `cancel`, `confirm_request`, `out_of_scope`, `unclear` |
| `action` | `create` | a new record of `module` (an expense or income, a water intake, a meal) |
| | `correct` | a change of **absolute** field values of an open draft ("il caffè era 1,50", "300 ml" after "Quanta acqua?") |
| | `cancel` | the user asks to cancel a draft by text. **Never executed**: .NET points to the draft's Cancel control (G1 D1) |
| | `confirm_request` | the user asks to confirm by text. **Never executed**: .NET shows the draft's Confirm control (G1 R16) |
| | `out_of_scope` | something Quick Capture does not do; `out_of_scope_reason` says what |
| | `unclear` | an operation-like request the router cannot classify |
| `span` | string | the segment's words, verbatim; non-overlapping, in message order |
| `context_spans` | ≤ 3 strings | other verbatim excerpts of the message that apply to this segment (shared date, time, place). Only for `create` and `correct` |
| `reference_span` | string, null | words **inside the segment** that describe the target draft ("il caffè", "la spesa del giornale"). Only for `correct`, `cancel`, `confirm_request`; null when the user names no draft. Never a number or position to be used as an index (ADR-015 §References) |
| `out_of_scope_reason` | `analytics`, `general_question`, `other_capability`, `transfer`, `relative_correction`, `other`, null | required iff `action` is `out_of_scope` |

`out_of_scope_reason` values: `analytics` — questions about the user's data ("quanto ho speso questa settimana?",
G1 R17); `general_question` — anything generalist; `other_capability` — another LifeOS module (reminders, gym,
journal…); `transfer` — a Finance transfer (unsupported, as v1/v2); `relative_correction` — a correction relative
to the current value ("aggiungi 2 euro", "metà"), out of scope for v1 (G1 D11); `other` — anything else, including
instructions addressed to the system.

`truncated`: true when the message contains **more** operations than `max_segments`; the router then lists exactly
`max_segments` segments. .NET's handling is in AI-009 §Limits (proposed: nothing is created, the user is asked to
split the message).

Zero segments is valid (e.g. "Grazie!"): nothing to record.

## Rules (checked in this order; first failure is the code)

| # | Code | Rule |
| --- | --- | --- |
| 1 | `protocol_version` | must be 1 |
| 2 | `segments_count` | at most `context.max_segments` segments |
| 3 | `truncated_count` | `truncated: true` requires exactly `max_segments` segments |
| | | *for each segment, in order:* |
| 4 | `module_required` | `create` and `correct` need a module |
| 5 | `out_of_scope_reason` | a reason iff `action` is `out_of_scope` |
| 6 | `segment_span` | span non-blank, ≤ 1000 |
| 7 | `segment_span_not_in_message` | span found in the message (match form) |
| 8 | `segment_order` | span found **after the end of the previous segment's match** (in order, no overlap; a repeated phrase needs to appear twice) |
| 9 | `reference_span_unexpected` | a reference only for `correct`, `cancel`, `confirm_request` |
| 10 | `reference_span` | reference non-blank, ≤ 200 |
| 11 | `reference_span_not_in_segment` | reference found inside the segment's span |
| 12 | `context_spans_unexpected` | context spans only for `create` and `correct` |
| 13 | `context_spans_count` | at most 3 |
| 14 | `context_span` | each non-blank, ≤ 200 |
| 15 | `context_span_not_in_message` | each found in the message |
| 16 | `context_span_duplicate` | no two equal (match form) |

Any violation — and any `"shape"` refusal, provider failure, timeout or refusal — fails the **whole turn**: no item
and no draft is created, the message stays stored and can be retried (G1 R12). The router is not repaired or
partially used.

## What the rules cannot check

The rules guarantee that every segment is grounded in the message, ordered and well-formed. They cannot guarantee
that the router listed **every** operation: an omitted intention violates nothing (critical point 4). That is
measured by the router evaluation (AI-009 §Evaluation: segment recall/precision, module and action accuracy,
omission rate), with gates frozen before any live run.

## Cases

`cases.json` (`default_max_segments: 5`; a case may set `max_segments`): 19 valid outputs (single and multi-module
messages, shared context date, repeated text, correction with and without reference, textual confirm/cancel
requests, analytics/transfer/relative correction/other capability, zero segments, injection text, truncation at the
default and at a configured lower limit, typographic apostrophes), 10 refused while reading (unknown action or
module, `endpoint` / `draft_id` members, missing or mistyped members), 21 rule violations (every code above at
least once). `request.cases.json`: 3 accepted requests, 11 refused (an id or a draft value in the context, history,
limit outside the contract, fields of another kind, duplicate kinds, more than 10 drafts, inconsistent `awaiting`,
length).
