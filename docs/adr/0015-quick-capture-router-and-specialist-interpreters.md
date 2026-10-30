# ADR-015: Quick Capture — LLM router, specialist interpreters and deterministic .NET orchestration

## Status

**PROPOSED** (2026-10-29, AI-009 increment I0). Not accepted, not implemented.

| Kind | What | Record |
| --- | --- | --- |
| **Approved by the owner (Gate G1)** | The architecture *Router LLM → specialist AI interpreters → deterministic .NET orchestration → independent drafts → individual confirmations*; requirements R1–R17 and technical decisions D1–D13 | [AI-009 §2](../tasks/ai/AI-009.md#2-approved-inputs-gate-g1) |
| **Proposed here** | How the architecture is realised: the `QuickCaptureItem` model, the transaction and retry design, the reference resolver, the limit handling, the migration order | this ADR |
| **Not decided** | The items in [Open decisions](#open-decisions) | — |

This ADR does not change [ADR-014](0014-external-ai-provider-for-lifeos-assistant.md) (still PROPOSED). Its data
handling is proposed in the [ADR-014 Quick Capture addendum](0014-external-ai-provider-for-lifeos-assistant.md#quick-capture-addendum-ai-009--proposed-2026-10-29).
It builds on ADR-002 (.NET is the system of record), ADR-003/004/011 (Python intelligence layer, frameworks as
infrastructure, stateless `lifeos-ai`) and on the AI-007/AI-008 Assistant core, which stays in place.

## Context

The LifeOS Assistant (AI-007, extended by AI-008 at `733ac2f`, **not released**) interprets one message with **one**
model call (protocol v2) and keeps **one open draft per conversation**:

- one intent per message; a second operation in the same message is answered `multiple_operations`;
- a partial unique index `ux_assistant_operations_conversation_open` allows one open operation per conversation,
  and the insert uses it as the arbiter of `INSERT … ON CONFLICT (conversation_id) WHERE status IN
  ('Collecting','AwaitingConfirmation') DO NOTHING` (`AssistantOperationRepository.TryAddAsync`);
- both "open operation" reads (`GetOpenAsync` of the typed repository and of `IAssistantOperationReader`) use
  `SingleOrDefaultAsync`, i.e. they assume at most one open row;
- the turn is T1 (accept, lease) → interpret outside any transaction → validate → T2 (one unit of work: lock the
  turn and the conversation, apply through the module, store the one reply, complete). Fencing: the turn lease and
  attempt, the "latest message" condition of `TryLockTurnAsync`, and the open draft's id + `DraftVersion` snapshot.

Quick Capture (AI-009) asks for: several operations in one message, of different modules, each becoming an
independent draft confirmed individually; partial failures that keep the valid results; selective retry without
duplicates; conservative handling of ambiguous references (G1 R1–R17).

## Problem

How to interpret a message that contains up to five operations of three modules, and to turn each into its own draft,
**without** giving any model control over writes, identifiers or flow, **without** losing the guarantees of the
existing core (T1/T2, lease, fencing, `DraftVersion`, idempotent confirmation) and **without** an agent framework?

## Options considered

| Option | Summary | Verdict |
| --- | --- | --- |
| A. One call, v2 extended with arrays | One schema returns a list of operations, each with all three sections | **Rejected.** The output size grows with every module (each element carries every section), one invalid element invalidates the whole answer (no partial success, R14), one prompt must hold every module's rules, and every module change re-tests every module |
| B. Deterministic splitter + v2 per segment | Split by punctuation/conjunctions in .NET, then one v2 call per piece | **Rejected.** Italian coordination is not reliably split by rules ("caffè e cornetto 3 euro" is one expense; "caffè 1 euro e giornale 2" are two); shared context ("Ieri …") would be lost |
| C. **Router LLM + specialist LLMs, orchestrated by .NET** | One call segments and classifies; one call per segment extracts fields with the v2 section of its module | **Chosen** (G1). Each call is small and single-purpose; a specialist failure affects one segment; specialists reuse the evaluated v2 sections; modules evolve independently |
| D. Tool-calling agent (model chooses tools in a loop) or LangChain / LangGraph | The model calls `create_expense`, `log_water`… | **Rejected.** Hands flow control and write intent to the model (AGENTS.md agent safety, ADR-002); unbounded call count; a framework would define the architecture (ADR-004). LangChain/LangGraph excluded by G1 |
| E. Router only, .NET parses values | The router returns segments; deterministic code parses amounts, dates, units | **Rejected for v1.** Duplicates what v2 already does well with spans; natural-language dates, meal descriptions and category guesses need the model |

## Decision (proposed)

### 1. Pipeline

```text
POST /api/assistant/conversations/{id}/messages            (unchanged endpoint, G1 D6)
 T1   accept the message, lease, attempt                   (unchanged)
 R    router call (no DB transaction)                       → segments | failure
        invalid / unavailable / refused / timeout           → turn Failed, nothing stored besides the turn status (R12)
 P    prepare (reads only): open drafts snapshot (id + DraftVersion, local), reference resolution,
      capacity pre-check, item plan
 S    specialist calls, in parallel with bounded concurrency (D9), one per segment that needs one
        (create, or correct with a resolved target); each result or failure is per segment
 T2   one unit of work: lock turn + conversation (existing fence), re-count open drafts, insert the items,
      apply each item through its module's lifecycle (create with source_item_id / conditional update at
      the snapshot DraftVersion), store ONE assistant reply rendered from the item outcomes, complete the turn
```

- **.NET is the orchestrator.** The router's `module` + `action` are looked up in a fixed table of Application use
  cases. No model output names an endpoint, a command, SQL, a table or an identifier (contracts:
  `docs/contracts/quick-capture`).
- **Same model for router and specialists initially** (G1 D8); optimisation only after evaluation.
- **Protocols v1/v2 are kept** (G1 D5): the Quick Capture endpoints are new (`/v1/quick-capture/*`), the
  `/v1/assistant/interpret` endpoint and its contracts are untouched.
- **Feature flag** `Assistant:QuickCapture:Enabled` (proposed name), default off everywhere; when off, the API
  behaves as AI-008 (subject to the compatibility rules in §7).

### 2. `QuickCaptureItem`

A new Domain concept and table, `assistant_quick_capture_items` (proposed name). One item = one router segment of one
user message.

| Column | Notes |
| --- | --- |
| `id` | uuid v7 |
| `user_id`, `conversation_id` | owner; composite FK to the conversation, ON DELETE CASCADE (same pattern as `assistant_operations`) |
| `message_id` | the **user** message; FK, ON DELETE CASCADE |
| `ordinal` | 1…5, segment order; **unique `(message_id, ordinal)`** |
| `module`, `action`, `out_of_scope_reason` | as returned by the router (closed values) |
| `segment_text`, `context_spans`, `reference_text` | the verbatim spans, kept to retry the specialist without calling the router again. Same sensitivity as the message text; deleted with the conversation; never logged |
| `status` | `Applied` · `Answered` · `Failed` · `Retrying` · `Expired` (below) |
| `outcome_code` / `failure_code` | stable codes (AI-009 §9) |
| `operation_id` | the draft created (create) or targeted (correct). Informational; the authority is `assistant_operations.source_item_id` |
| `attempts`, `lease_until_utc`, `retry_until_utc` | selective retry (§5); `retry_until_utc = created_at + 24 h` (G1 D4) |
| `created_at_utc`, `updated_at_utc` | |

| Status | Meaning | Final |
| --- | --- | --- |
| `Applied` | the item created or corrected a draft | yes |
| `Answered` | completed **without** draft change by design: out of scope, unclear, textual confirm/cancel request, ambiguous or unknown reference, limit reached, module not enabled, specialist outcome other than `interpreted` | yes |
| `Failed` | its specialist call failed (unavailable, timeout, refused, invalid output) or its target draft moved (`draft_changed`); **no draft change**; retryable until `retry_until_utc` | no |
| `Retrying` | a retry attempt holds the item under a lease | no |
| `Expired` | a `Failed` item past its window (lazy, on read) | yes |

`assistant_operations` gets `source_item_id uuid NULL` with a **unique partial index** (`WHERE source_item_id IS NOT
NULL`): a draft is created by at most one item and an item creates at most one draft, across replays and retries.

**Relations.** Conversation 1—N user messages; user message 1—0..5 items; item 0..1—1 created draft
(`source_item_id`); item N—0..1 targeted draft (corrections); draft 1—0..1 module record (`result_id`, unchanged).
The turn state stays on the user message (AI-007 §9.2). A turn is `Completed` when T2 committed its items — whatever
their individual statuses — and `Failed` when the router failed or T2 did not commit.

### 3. Transaction constraints

1. **No database transaction is held during any model call** (router or specialist), as AI-007.
2. **One T2 per turn attempt**, all-or-nothing: items, draft changes, reply and turn completion commit together.
   A reply therefore never claims a draft that was not stored, and a stored draft always has its reply.
3. **Inside T2, an expected conflict is never an exception.** A failed statement aborts the whole PostgreSQL
   transaction (AI-007 R-2), which would take every other item down with it. So: creation uses `INSERT … ON CONFLICT
   (source_item_id) WHERE source_item_id IS NOT NULL DO NOTHING`; corrections use the existing conditional
   `UPDATE … WHERE id, user, kind, status, draft_version` and read the row count; capacity is a count under lock.
   Per-item outcomes are values; only an unexpected error is an exception, and it rolls back the attempt.
4. **Capacity replaces the unique index as the open-draft guard.** Every path that opens a draft (the T2 of a turn,
   the item retry, the manual `POST …/operations`) first locks the conversation row (`FOR UPDATE`, as
   `TryLockTurnAsync` already does) and counts open drafts in the same transaction.
5. **Existing fences are kept unchanged**: lease + attempt + latest-message condition for turns; `DraftVersion` +
   status for every draft write; row lock + execution + `Executed` in one unit of work for confirmation.

### 4. Idempotency

| Situation | Guarantee | Mechanism |
| --- | --- | --- |
| Same `clientMessageId` resent | the stored turn, items and drafts are returned; nothing is reprocessed | existing message replay |
| Turn retry (`Failed` turn: router failure, interrupted, T2 not committed) | re-runs router and specialists; no duplicate, because a failed attempt committed nothing | existing turn retry; §3.2 |
| Two item sets for one message | impossible | unique `(message_id, ordinal)`; a completed turn is replayed, never reprocessed |
| Item replay / retry creating a second draft | impossible | unique `source_item_id` + `ON CONFLICT DO NOTHING` + item status/attempt fence |
| Second record for one draft | impossible | unchanged confirmation (`Idempotency-Key`, conditional status, unique `result_id`) |

### 5. Selective retry (G1 R15, D4)

Only `Failed` items are retried, only within 24 h, never by calling the router again, one item per request:

```text
POST /api/assistant/items/{id}/retry                      (proposed; under /api/assistant, G1 D6)
 R1  lock conversation; item Failed, window open, attempts < cap, no live Processing turn
     → Retrying, attempts+1, lease                         (else: return the item as it is / 409 turn_in_progress)
 S   (correct only) resolve the reference again against the current drafts; call the item's specialist
     with the stored segment and context spans
 R2  lock conversation + item (Retrying, same attempt, lease live) → apply as in T2 → Applied / Answered / Failed
```

The retry stores **no assistant message**: the existing unique index "one reply per user message"
(`ux_assistant_messages_conversation_reply`) and the latest-message condition of the turn fence both forbid
replying to an older message. The outcome is shown on the item (AI-009 §10). A `Retrying` item whose lease ended
becomes `Failed` again on the next read (lazy, like turns).

### 6. References (G1 D10, R11)

The router returns **descriptive words** (`reference_span`), never ids or positions. .NET resolves them locally
against the user's open drafts — whose values never leave .NET — conservatively:

1. candidates = open drafts of the segment's module (all modules for `cancel` / `confirm_request` without module),
   as of the turn's snapshot (drafts created by the same message are not candidates);
2. no reference: the draft the last reply asked about (`awaiting`) if it is a candidate; otherwise the only
   candidate; otherwise **ambiguous**;
3. a reference: deterministic token match (match form, stop words removed) against the candidate's own text
   (Finance note, category and account names; meal description and type label; for water, only the module words).
   Exactly one match → that draft. Several → **ambiguous**. None → the only candidate if the reference contains
   nothing but module words ("la spesa", "l'acqua"), else **not found**. Numbers and ordinals ("la seconda",
   "numero 3") are never used as positions;
4. two correction segments resolving to the same draft → neither is applied;
5. ambiguous / not found → item `Answered`, nothing modified, no specialist call; the reply asks to use the draft's
   card (manual correction remains possible, G1 R10).

### 7. Migrations and compatibility (expand → switch → contract)

The one-open index cannot simply be dropped: the AI-008 insert names it as its `ON CONFLICT` arbiter. Without a
matching unique index PostgreSQL rejects that statement (*"there is no unique or exclusion constraint matching the
ON CONFLICT specification"*), so **every draft creation of an AI-008 build would fail**, and its `SingleOrDefault`
reads would throw on any conversation with two open drafts.

| Step | Change | Old code (AI-008) still works? | Rollback |
| --- | --- | --- | --- |
| **M1** (additive migration) | create `assistant_quick_capture_items`; add `assistant_operations.source_item_id` + its unique partial index | yes (new nullable column, new table) | `Down` drops them; safe while no item exists |
| **C1** (code, flag off) | insert no longer uses the one-open arbiter (arbiter `source_item_id`, or plain insert + capacity under lock); every open-draft read is multi-draft aware; a unique violation of the still-present one-open index is mapped to the existing "draft already open" outcome; when the flag is off, draft intents of the v2 path in a conversation with more than one open draft are answered "use the draft cards" instead of guessing | n/a — this *is* the new code; with the index still present the limit is effectively 1 | redeploy AI-008 build: safe, schema unchanged for it |
| **M2** (contract migration) | drop `ux_assistant_operations_conversation_open`; create a non-unique index on `(conversation_id) WHERE status IN (open)` for the count | **no** — must run only when every running API is C1+ | recreating the unique index fails while any conversation has > 1 open draft |
| **F** (flag on) | Quick Capture enabled | — | flag off: C1 behaviour; drafts already open stay valid and confirmable |

Required checks: before M2, no AI-008 instance is running; after M2, a smoke test creates two drafts in one
conversation. **Before any code rollback past C1 after M2**: `SELECT conversation_id FROM assistant_operations WHERE
status IN ('Collecting','AwaitingConfirmation') GROUP BY conversation_id HAVING count(*) > 1` must be empty, then the
unique index is recreated, then the old build is deployed. If it is not empty, the only ways back close or expire
user drafts — a data decision for the owner, not an automatic step. **Recommended rollback after M2: flag off, never a
code rollback past C1.**

AI-008 itself is not released (`20261027090331_GeneralizeAssistantOperations` not applied outside Development; runbook
Part I). M1/M2 come after it; the AI-008 release gates stay in force (G1 D12).

API compatibility: endpoints unchanged; responses gain members (`items`, a list of open drafts) and keep the existing
single-draft members, populated when exactly one draft is concerned and null otherwise. An app build without Quick
Capture never breaks a conversation it opens; it may not show every draft of a multi-draft conversation.

## Consequences

Positive:

- Several operations of several modules per message, each an independent draft, confirmed one by one.
- A failing specialist costs one segment, not the message; the failed segment can be retried alone.
- Specialists reuse the v2 sections and their rule codes: the AI-008 field semantics and their tests carry over.
- The model's role stays "interpret"; flow, identity, validation and writes stay in .NET.

Trade-offs:

- Up to **1 + 5 model calls per message** (plus retries): more latency, cost and provider-side copies of personal
  text (ADR-014 addendum).
- More state: a new table, item statuses, a retry endpoint, a three-step migration.
- The 90 s turn lease must now cover two sequential model stages (§Risks).

## Risks

| # | Risk | Mitigation |
| --- | --- | --- |
| R-1 | The router omits an intention without violating the contract (silent loss) | Evaluation gates on segment recall/omission (AI-009 §18); the reply lists what was captured, so the user sees omissions |
| R-2 | A resolver that is too eager modifies the wrong draft | Conservative rules (§6), "nothing modified" on doubt, golden tests per rule; values never reach the model |
| R-3 | Migration order mistake (M2 before every instance is C1) breaks all draft creation | Ordered runbook, pre-M2 check, M2 as a separate deploy step; flag-off rollback |
| R-4 | A DB error in T2 leaves the turn `Processing` until its lease ends (current behaviour, see AI-009 §9) | No fictitious success (nothing committed, no reply); proposed improvement: fail the turn immediately after rollback (not implemented) |
| R-5 | Latency: router + parallel specialists must fit the lease (90 s, 15 s completion margin) while today one `lifeos-ai` call may take up to 40 s | Per-stage deadlines (proposed router ≤ 25 s, specialist stage ≤ 35 s) — requires per-call deadlines in `lifeos-ai`; measured in evaluation |
| R-6 | Cost and rate limits ×6 per message; shared OpenAI project with Journal (ADR-014 P-7) | Bounded concurrency, spend guard per call, cost measured on the synthetic set before release |
| R-7 | Scope creep toward a general agent | Closed module/action enums, analytics and generalist requests out of scope (R17), no tool loop |

## Open decisions

Listed for the I0 checkpoint in [AI-009 §20](../tasks/ai/AI-009.md#20-decisions-for-the-i0-checkpoint).

## Alternatives rejected

- Options A, B, D, E above.
- **Keeping one open draft and queueing the others** — contradicts R7 (several open drafts).
- **A "confirm all" action** — contradicts R8 (individual confirmations only).
- **Sending draft ids or values to the router so the model picks the target** — contradicts D10 and repeats the
  `h_injection_numeric_ids` risk recorded in ADR-014.
