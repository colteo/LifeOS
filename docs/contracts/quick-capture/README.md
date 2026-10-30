# Quick Capture interpretation contracts (AI-009)

> **Status: PROPOSED (AI-009 increment I0, 2026-10-29).** No code reads or checks these files yet. They are the
> contracts that increments I1+ must implement on both sides (.NET and `lifeos-ai`), as
> [`assistant-interpretation`](../assistant-interpretation/) v1/v2 are for the Assistant today. The v1/v2 contracts
> are **not** changed and stay served (G1 D5).

Design: [AI-009](../../tasks/ai/AI-009.md), [ADR-015](../../adr/0015-quick-capture-router-and-specialist-interpreters.md),
privacy: [ADR-014 Quick Capture addendum](../../adr/0014-external-ai-provider-for-lifeos-assistant.md#quick-capture-addendum-ai-009--proposed-2026-10-29).

## The four contracts

One user message is interpreted in two stages. .NET calls the **router** once, then at most one **specialist**
per segment that needs one. Every call is stateless and independent; the model never sees another call's output,
an identifier or a draft value.

| Directory | Call | Interprets | Returns |
| --- | --- | --- | --- |
| [`router/v1`](router/v1/README.md) | `POST /v1/quick-capture/route` | the whole message | 0…`max_segments` **segments**: module, action, verbatim span, context spans, descriptive reference |
| [`finance/v1`](finance/v1/README.md) | `POST /v1/quick-capture/finance/interpret` | one segment | one expense/income: the v2 `finance` section |
| [`hydration/v1`](hydration/v1/README.md) | `POST /v1/quick-capture/hydration/interpret` | one segment | one water intake: the v2 `hydration` section |
| [`meal/v1`](meal/v1/README.md) | `POST /v1/quick-capture/meal/interpret` | one segment | one meal: the v2 `meal` section |

Endpoint paths are proposals (ADR-015); the public API stays under `/api/assistant` (G1 D6).

## Files of each version

Same layout as `assistant-interpretation`, plus two request files (the v1/v2 request is defined by the Pydantic
model and `request.example.json` only):

| File | Content | Checked by (from I1) |
| --- | --- | --- |
| `interpretation.schema.json` | the strict JSON schema of the **model's output**: every property required, `additionalProperties: false`, nullable instead of optional, only the keywords `type`, `enum`, `properties`, `required`, `additionalProperties`, `items` (the strict structured-output subset used by v1/v2) | Python: equal to the schema the adapter sends; .NET: names, enum values and nullability equal to the records/enums |
| `request.schema.json` | JSON Schema 2020-12 of the **request** .NET sends (full keyword set: lengths, cardinalities, `const`, `oneOf`, `if/then`) | .NET: what the client serialises validates; Python: the request model accepts exactly this |
| `protocol.json` | protocol name and version, locale, limits (lengths in Unicode code points), configurable limits, endpoint, HTTP error codes and the failure each maps to | both: constants |
| `request.example.json` | one minimised request | both |
| `request.cases.json` | requests with the expected verdict: `null` (accepted) or `"invalid_request"` (HTTP 422), plus `rule` naming the broken rule | both |
| `cases.json` | golden outputs with the expected verdict: `null` (valid), `"shape"` (refused while reading) or a rule code | both: same verdict, same code |

`"shape"` means what it means in v1/v2: the strict reader refuses the JSON (unknown or missing member, wrong JSON
type, value outside a closed enum). As in v1/v2, the reader accepts **any integer** `protocol_version` and **any
strings** in `ambiguous_fields`: those are reported by the rule codes `protocol_version` and `ambiguous_fields`. Rule
codes are checked in the order listed in each README, so both sides report the same first code.

Cardinality and length limits of the **output** are rule codes, not schema keywords (`maxItems`, `maxLength` are
not in the strict subset; v1/v2 did the same for duplicates in `ambiguous_fields`).

## Shared rules

### Matching and normalisation (unchanged from v1/v2)

- *match form*: typographic apostrophes (`’ ‘ \``) become `'`, whitespace runs become one space, trimmed,
  **lower-cased**. Used for every "found in" check of a span.
- *verbatim form*: the same without lower-casing. Used for the Finance note and the meal description, which must be
  the user's own words, case included.

### Field states (unchanged from v1/v2)

| State | Meaning | `value` / `span` |
| --- | --- | --- |
| `absent` | not said | both null |
| `stated` | said in the authorised text | value non-blank; span non-blank and found in the authorised text |
| `inferred` | proposed by the model, never stated | value non-blank, span null; allowed only where v2 allows it (Finance `category`, Finance `note`) |
| `clear` | the user asks to empty the field | both null; allowed only where v2 allows it **and only in `correct` mode** (`clear_on_create`) |

### Verbatim spans and authorised text

- **Router.** Every segment `span` is a verbatim excerpt of the message (match form). Segments are listed in message
  order and **do not overlap**: each span is searched from the end of the previous segment's match
  (`segment_order`). `context_spans` are other verbatim excerpts of the same message (e.g. "Ieri" in "Ieri ho speso
  12 euro e ho bevuto mezzo litro") that the segment's specialist may also use. `reference_span` is a verbatim
  excerpt **of the segment** that describes which draft is meant ("il caffè", "la spesa del giornale").
- **Specialists.** A specialist never receives the message. Its **authorised text** is the segment plus the
  segment's context spans, each taken separately: a stated span must be found inside one of them
  (`<field>_span_not_in_message`, same code as v2 with "message" read as "authorised text"); a span that crosses
  from a context span into the segment is not found. The Finance note and the meal description must be verbatim in
  the **segment itself** (`note_not_verbatim`, `meal_description_not_verbatim`): a context span carries dates,
  times or shared details, never the description of another operation.

### Identifiers and draft values

No request or output member carries a LifeOS identifier (user, conversation, message, item, operation, account,
category, transaction, intake, meal), a timestamp, a time zone, a list position or a draft value. The open drafts
reach the router only as **per-kind counts and missing field names**; a correction reaches a specialist only with
the target draft's **field names** (filled / missing) — the same minimisation as v2 `context.open_draft`. Draft
identity is resolved by .NET, locally (ADR-015 §References). Every request and output object forbids unknown members,
so an `id`, `draft_id`, `endpoint`, `history` or `values` member is a 422 (request) or `"shape"` (output); see the
cases.

### What the outputs can do

Nothing. A router output is a list of descriptions; a specialist output is a typed proposal of field values. .NET maps
`module` + `action` through a fixed table to its own use cases; no output names an endpoint, a table, a command, SQL
or an identifier, and no output confirms, cancels or writes anything (G1 R16, D1).

## Limits

| Limit | Value | Where | Configurable |
| --- | --- | --- | --- |
| Message length | 1000 code points | router request schema | no (as v2) |
| Segments per message | 5 | router `max_segments` (request) and rule `segments_count` | yes, 1–5 (`Assistant:QuickCapture:MaxSegmentsPerMessage`, proposed name) |
| Open drafts per conversation | 10 | router request rule `open_drafts_total`; enforced by .NET at T2 | yes, 1–10 (`Assistant:QuickCapture:MaxOpenDraftsPerConversation`, proposed name) |
| Context spans per segment | 3, each ≤ 200 | router rules; specialist request schemas | no |
| Reference span | ≤ 200 | router rule | no |
| Value / note / span / days ago / category names | as v2 (200 / 1000 / 1000 / 366 / 100 × 200) | specialist protocols | no |

The handling strategy for each limit (what the user sees, what is stored) is in AI-009 §Limits. The values come
from G1 D3; the contracts make them part of the request so a lower configured value is visible to the model and
verifiable in tests.

## Changing these contracts

Edit these files first (an incompatible change is a new `v2/` directory of that contract), then make both sides
pass. Synthetic data only (AGENTS.md).
