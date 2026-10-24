# ADR-014: External AI provider and data handling for LifeOS Assistant

## Status

**PROPOSED / NOT APPROVED** — draft written in AI-007 F0 (2026-10-24).

This ADR does not authorise sending real data to any provider. Until it is accepted:

- no LifeOS Assistant request with real user data may be sent to an external provider;
- F3 of [AI-007](../tasks/ai/AI-007.md) may not start;
- development and evaluation use synthetic data only.

If accepted, it would amend [ADR-013](0013-openai-provider-for-journal-rag.md), which approves
OpenAI for Journal Memory only, and apply [ADR-011](0011-production-python-ai-service.md) (stateless
`lifeos-ai`, service-key authentication, provider adapters without SDKs).

## Context

AI-007 introduces LifeOS Assistant: a persistent chat in which the user records financial
transactions in natural language ("Speso 5€ per colazione con Pamela"). Interpreting the text needs
a language model. The models available to LifeOS today are external services reached by `lifeos-ai`:

- **OpenAI** — used by Journal RAG (embeddings and answers, Responses API with `store=false`, strict
  json_schema, no tools, no provider-side conversation). Approved by ADR-013 for Journal Memory
  only; the owner's privacy acceptance (AI-006.1 §7.1) covers Journal RAG only.
- **Groq** — used by Nutrition estimation, Weekly Review insights and the Action Agent
  (`openai/gpt-oss-20b`, strict json_schema, temperature 0) under ADR-011.

The product owner **prefers OpenAI** for the Assistant. That preference is not yet an approval for
financial data.

The data involved is new for an external provider in LifeOS: free-text descriptions of personal
spending and income, amounts, dates, people's names, and the user's category names.

## Problem

Which external provider may interpret Assistant messages, with which data, under which conditions,
and what must be verified before real financial text is sent?

## Options considered

| Option | Summary | Status in this draft |
| --- | --- | --- |
| A. OpenAI (Responses API, `store=false`, strict structured output) | Same transport and settings pattern as Journal RAG; one provider for two features | **Preferred** by the owner; pending verification |
| B. Groq (OpenAI-compatible Chat Completions, strict json_schema) | Already used by three LifeOS AI features | **Alternative**; pending verification and evaluation |
| C. No external provider (deterministic parser only) | No data leaves LifeOS | Rejected for v1: fails the natural-language requirement; remains the fallback when the AI is unavailable (the user edits the draft by hand) |
| D. Self-hosted / local model | Data stays on owned infrastructure | Not considered for v1: no hosting for it today (ADR-011 amendment uses Render Free); can be reconsidered later |

Option A vs B is to be decided on the verifications below **and** on an offline evaluation with the
frozen synthetic AI-007 dataset (AI-003/AI-006 discipline). Cost and quality numbers are not stated
here because none have been measured for this feature.

## Proposed decision (subject to the verifications)

1. **Provider**: OpenAI as the preferred provider, Groq as the documented alternative. Exactly one
   provider is selected by explicit configuration (`LIFEOS_AI_ASSISTANT_PROVIDER`, proposed), with no
   automatic fallback between providers — the same rule as ADR-013.
2. **Memory stays in LifeOS**. Conversations, messages, drafts and their history are stored only in
   LifeOS PostgreSQL (AI-007 §8–9). Every provider call is stateless:
   - OpenAI: `store=false` on every request; no `previous_response_id`; no Conversations or
     Assistants features; the response id is neither read nor kept;
   - Groq: no provider-side state is used;
   - no tools, no file search, no vector stores, no web search, no provider-side memory.
3. **The provider interprets, LifeOS decides**. The model returns a schema-bound interpretation;
   .NET validates it, resolves accounts and categories against the user's own data and writes only
   after the user's explicit confirmation through `CreateTransactionHandler` (ADR-002, ADR-003).
4. **Data minimisation** (what may be sent per request):
   - the current user message;
   - a bounded slice of previous messages of the same conversation (AI-007 P-5);
   - the current draft **without any LifeOS identifier** (amount, type, note, local date, the
     account mention text, the chosen category name);
   - the user's category names (needed to propose a category);
   - today's local date and the locale.

   Never sent: user id, any conversation/message/operation/account/category/transaction id, the list
   of accounts, balances, budgets, other transactions, other modules' data, access tokens, the
   e-mail address. The `lifeos-ai` request model rejects unknown fields (`extra="forbid"`).
5. **No preventive masking of names**. By explicit owner decision, names and other free-text
   details (e.g. "Pamela" in "colazione con Pamela") are sent as written when they are part of the
   message to interpret. No anonymisation or pseudonymisation is introduced. This decision does
   **not** relax any other measure in this ADR, and it is **not** an approval of production use.
6. **Input and output handling**:
   - user text is placed in the request as data under a fixed, versioned system prompt
     (`assistant-finance-interpret-v1`, proposed) that treats it as untrusted;
   - the output must match a strict schema; anything else is an invalid answer, never repaired by
     guessing;
   - the output is never shown to the user verbatim in v1: replies are .NET templates rendered from
     the validated draft (AI-007 D-5);
   - the output is never executed: it can only change a draft, which the user confirms.
7. **Logging** (LifeOS side, .NET and Python): outcome code, provider, model, prompt version,
   attempts, latency, token counts, LifeOS-internal ids. Never: message text, history, draft
   fields, amounts, notes, names, category or account names, model output, provider error bodies,
   API keys, service key.
8. **Security**:
   - app → API: HTTPS, LifeOS access token, owner-scoped queries;
   - API → `lifeos-ai`: HTTPS and the shared service key (ADR-011 amendment);
   - `lifeos-ai` → provider: HTTPS, API key only from the environment, never logged or committed;
   - key rotation as in the production runbook; a leaked provider key is revoked at the provider.
9. **Availability**: a provider outage gives a fixed "assistente non disponibile" reply (HTTP 503 to
   the app); the user message stays stored and can be retried; the draft can still be edited and
   confirmed by hand; Finance is unaffected.

## Provider-side retention and processing (unverified)

What LifeOS controls is what it sends and that it asks for no stored state. What the provider does
with received data is governed by the provider's own terms and policies, which this repository does
not contain. AI-006.1 §7.1 already notes for Journal RAG that `store=false` only asks OpenAI not to
store the response for later retrieval, and that the repository holds no evidence of a stronger
guarantee such as zero data retention.

This ADR therefore makes **no claim** about: retention periods, abuse-monitoring logs, use of data
for training, Zero Data Retention eligibility, data location, sub-processors, or legal/contractual
compliance (including GDPR) — for either provider.

## Verifications required before acceptance

Each item must be answered from the provider's current official documentation or contract, with the
source and date recorded in this ADR.

| # | Question | OpenAI | Groq |
| --- | --- | --- | --- |
| V-1 | Are API inputs/outputs used for model training by default? Can it be excluded, and how? | open | open |
| V-2 | Retention of inputs/outputs, including abuse-monitoring logs: what, how long, who can access | open | open |
| V-3 | Exact effect of `store=false` on the Responses API (what is still retained) | open | n/a (verify the equivalent, if any) |
| V-4 | Is Zero Data Retention (or equivalent) available for this account, under which requirements, and does it apply to the endpoints and features used? | open | open |
| V-5 | Do the endpoint and features used (structured outputs, reasoning models) change the retention or eligibility above? | open | open |
| V-6 | Data processing location and international transfers; available regions | open | open |
| V-7 | Applicable terms for this account type (individual/organisation), data processing agreement availability | open | open |
| V-8 | Price per request for the expected size (input/output tokens) and rate limits on the current tier | open (to be measured in F3 evals) | open (to be measured) |
| V-9 | Availability and incident history; behaviour under rate limiting (429) | open | open |
| V-10 | Model candidates that support strict structured output on the chosen endpoint, and their deprecation schedule | open | open |

## Acceptance conditions

The ADR can move to **Accepted** only when:

1. V-1 … V-7 are answered with sources for the selected provider;
2. the owner records an explicit privacy acceptance for LifeOS Assistant (financial text, names,
   amounts, dates, category names), as AI-006.1 §7.1 did for Journal RAG;
3. the F3 offline evaluation on synthetic data meets frozen gates;
4. the selected provider, model and settings are written into this ADR.

## Consequences (if accepted as proposed)

Positive:

- Natural-language recording of transactions without giving the model write access.
- Memory, authorisation and validation stay entirely in LifeOS.
- With OpenAI, one transport (`openai_http.OpenAITransport`) and one key serve two features.
- The provider can be switched by configuration (no fallback), with no change to Domain,
  Application, contracts or tables.

Trade-offs:

- Personal financial text, including people's names, leaves LifeOS for each interpreted message.
- A provider outage or price change becomes an Assistant dependency (not a Finance dependency).
- Provider-side retention is outside LifeOS control and must be re-checked when terms change.

## Alternatives rejected in this draft

- **Preventive masking/pseudonymisation of names** — rejected by the owner for v1.
- **Provider-side conversation state** (stored responses, `previous_response_id`, provider
  threads) — rejected: memory belongs to LifeOS PostgreSQL.
- **Letting the model call write tools** — rejected: AGENTS.md agent-safety flow and ADR-002.
- **Automatic fallback between providers** — rejected: data would reach a provider the user did not
  select for that request.
