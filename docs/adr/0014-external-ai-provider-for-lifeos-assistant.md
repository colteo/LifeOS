# ADR-014: External AI provider and data handling for LifeOS Assistant

## Status

**PROPOSED / NOT APPROVED** — draft written in AI-007 F0 (2026-10-24).

This ADR does not authorise sending real data to any provider. Until it is accepted:

- no LifeOS Assistant request with real user data may be sent to an external provider;
- F3 of [AI-007](../tasks/ai/AI-007.md) may not start any provider traffic (F3-B). F3-A — the offline
  orchestrator, protocol and disabled-by-default plumbing, with no request to any provider, real or
  synthetic — was carried out on 2026-10-25 by the owner's explicit brief; see
  [F3-A review](#f3-a-review-2026-10-25);
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
| V-1 | Are API inputs/outputs used for model training by default? Can it be excluded, and how? | answered: F-1 (2026-10-25) | open |
| V-2 | Retention of inputs/outputs, including abuse-monitoring logs: what, how long, who can access | partly: F-2 (up to 30 days; who can access not checked) | open |
| V-3 | Exact effect of `store=false` on the Responses API (what is still retained) | partly: F-3, F-4 (no general store=false rule stated) | n/a (verify the equivalent, if any) |
| V-4 | Is Zero Data Retention (or equivalent) available for this account, under which requirements, and does it apply to the endpoints and features used? | partly: F-5 (approval required; account status unknown) | open |
| V-5 | Do the endpoint and features used (structured outputs, reasoning models) change the retention or eligibility above? | partly: F-5, F-6 | open |
| V-6 | Data processing location and international transfers; available regions | partly: F-7, F-8 | open |
| V-7 | Applicable terms for this account type (individual/organisation), data processing agreement availability | open: DPA page not readable (403, twice); search excerpts only | open |
| V-8 | Price per request for the expected size (input/output tokens) and rate limits on the current tier | partly: tiers, limits, 429/503 and spend controls documented (consolidation review); price per request to be measured in F3-B evals; account tier unknown | open (to be measured) |
| V-9 | Availability and incident history; behaviour under rate limiting (429) | partly: 429/503 behaviour and headers documented; incident history not read | open |
| V-10 | Model candidates that support strict structured output on the chosen endpoint, and their deprecation schedule | partly: strict json_schema from GPT-4o-class onward; deprecation schedule not read | open |

## F3-A review (2026-10-25)

Review made during AI-007 F3-A. **Nothing was sent to OpenAI** (no real or synthetic data, no API key
used). The status stays **PROPOSED / NOT APPROVED**. Sources were read on 2026-10-25; the OpenAI pages
carry no "last updated" date unless stated, so every fact below must be re-checked at acceptance time.

### Verified facts (official sources, 2026-10-25)

| # | Fact | Source |
| --- | --- | --- |
| F-1 | Data sent to the OpenAI API is not used to train or improve OpenAI models unless the customer explicitly opts in to share data. (V-1, OpenAI) | [Data controls in the OpenAI platform](https://developers.openai.com/api/docs/guides/your-data) |
| F-2 | Abuse-monitoring logs are generated for all API feature usage and retained for **up to 30 days**, longer if required by law or to protect services or third parties. (V-2) | same |
| F-3 | `/v1/responses` keeps **application state 30 days by default** (also with `store=true`). `store` defaults to **true** when omitted, so `store=false` must be sent explicitly on every request. (V-3) | same; [Migrate to the Responses API](https://developers.openai.com/api/docs/guides/migrate-to-responses) |
| F-4 | The page does **not** state a general retention rule for a plain (non-background) `/v1/responses` request with `store=false`; it states only that background requests with `store` false/omitted are deleted after a ~10-minute polling window (projects with Modified Abuse Monitoring) and that server-side compaction keeps nothing with `store=false`. **`store=false` cannot be read as "zero retention"**: the abuse-monitoring logs of F-2 still apply. (V-3) | [Data controls](https://developers.openai.com/api/docs/guides/your-data) |
| F-5 | Zero Data Retention / Modified Abuse Monitoring require **prior approval by OpenAI** and acceptance of additional requirements (through sales). With ZDR, customer content is excluded from abuse-monitoring logs and `store` is always treated as false for `/v1/responses` and `/v1/chat/completions`. Stateful features (Conversations, Assistants, Threads, Vector Stores, Files, Batches, …) are not ZDR-eligible; prompt caching may keep encrypted tensors up to 24 h on GPU-local storage. (V-4, V-5) | same |
| F-6 | Structured Outputs: the JSON schema is treated as system data, not customer content; no separate ZDR restriction is stated for it. (V-5) | same |
| F-7 | Data residency: Europe (EEA + Switzerland, `eu.api.openai.com`) with regional processing exists, but non-US residency requires approval for abuse-monitoring controls and a Modified Retention amendment; residency does not cover system data; endpoints for models released on/after 2026-03-05 carry a 10 % uplift there. (V-6) | same |
| F-8 | Sub-processors: OpenAI publishes a list (last updated 2026-06-02 per search results) including Microsoft and CoreWeave (cloud infrastructure, several countries) and Cloudflare, plus affiliates (e.g. OpenAI Ireland Ltd.) with SCCs as transfer mechanism. (V-6, V-7) — *read through search results, not first-hand* | [Sub-processor list](https://openai.com/policies/sub-processor-list/) |

### Unverified conditions (to be checked first-hand before acceptance)

- **DPA (V-7)**: `openai.com/policies/data-processing-addendum` returned HTTP 403 to the automated fetch.
  Search results indicate that the DPA is incorporated into the OpenAI Services Agreement / Business
  Terms (no separate signature workflow found), that EEA/Swiss customers contract with OpenAI Ireland
  Ltd., that SCCs or adequacy decisions cover onward transfers and that sub-processor changes are
  notified with a 30-day objection window. **Not verified first-hand**; whether the Services Agreement
  and DPA apply to this account type (individual vs organisation) is open.
- Account tier, current rate limits and incident behaviour (V-8, V-9): not read; no traffic was made.
- Price per request (V-8): to be measured in the F3-B offline evaluation on synthetic data.
- Model candidates and deprecation schedule for strict structured output on `/v1/responses` (V-10):
  decided by the F3-B evaluation (D-10).
- Groq column: not reviewed in F3-A (the owner prefers OpenAI).

### Hypotheses (not facts)

- With `store=false`, no previous response id and no stateful features, the only provider-side copy of
  Assistant content is the abuse-monitoring log (F-2), up to 30 days — unless ZDR / Modified Abuse
  Monitoring is approved. Inference from F-2…F-5, not a documented guarantee.
- The personal LifeOS account is not ZDR-approved (no evidence in the repository either way).

### LifeOS-side measures implemented in F3-A (verifiable in code and tests)

- **Default sends nothing**: .NET `AssistantAi:Enabled` defaults to false (`DisabledAssistantInterpreter`,
  no HTTP client built); Python `LIFEOS_AI_ASSISTANT_PROVIDER` accepts only `disabled` (selecting
  `openai` stops the service). Tested on both sides; no OpenAI adapter exists yet.
- **Minimisation stronger than §4**: the request carries the message, the locale, the open draft's
  filled/missing **field names** (no values: no amount, note, account or category name) and the user's
  category names (≤ 100 per type). No conversation history and no account names are sent. Request
  types contain no `Guid`/instant member (architecture test); unknown fields are rejected by Pydantic
  (`extra="forbid"`) and by .NET's strict deserialisation.
- **Logging**: .NET logs status, latency, provider/model/prompt; Python logs outcome code and identity;
  never message, categories or output (tested in Python).
- **Secrets**: the assistant path reads no provider key; the service key is unchanged (ADR-011).
- **Timeouts/retries**: the .NET call is bounded (default 45 s, at most 75 s) inside the 90 s turn lease;
  a timeout fails the turn (`assistant_timeout`) and applies nothing; a retry re-interprets from the
  current draft and can never apply twice (draft change and reply commit together).
- **Personal data**: people's names and financial details **do appear** in the messages that would be
  sent (e.g. "colazione con Pamela", amounts); by owner decision they are not masked (masking would
  alter Finance notes).

### Acceptable risks (proposed; the owner accepts or rejects them)

- Up to 30 days of provider-side abuse-monitoring retention of single messages (amounts, merchants,
  people's names) without ZDR.
- Processing outside the EU unless EU data residency is approved for the account.

### Decisions requiring the owner's explicit approval

1. Provider and account: OpenAI under which account and terms (individual vs organisation; DPA
   applicability).
2. Whether to request ZDR / Modified Abuse Monitoring and/or EU data residency, or accept F-2.
3. Privacy acceptance for Assistant content (financial text, people's names, amounts, category names).
4. D-6 (category names sent: all, as implemented in F3-A, or none), D-5 (templates, as implemented),
   D-10 (model and settings).
5. Whether a chat "annulla" may ever cancel a draft (F3-A: never; only the explicit endpoint).

### F3-A consolidation review (2026-10-25, second reading)

Re-read during the F3-A consolidation. **Nothing was sent to OpenAI** (no API key, no request, no real or
synthetic data). The status stays **PROPOSED / NOT APPROVED**. None of the pages below carries a "last
updated" date; each fact must be re-read at acceptance time.

| Topic | What the official source says | Source |
| --- | --- | --- |
| Training (V-1) | API data is not used to train or improve OpenAI models unless the customer explicitly opts in (policy in force since 2023-03-01). Unchanged from F-1. | [Data controls](https://developers.openai.com/api/docs/guides/your-data) |
| Abuse-monitoring logs (V-2) | Generated for **all** API usage; may contain prompts, responses and derived metadata (e.g. classifier outputs); kept **up to 30 days**, longer if required by law or to protect services or third parties. Independent of `store`. | same |
| Application state (V-3) | `/v1/responses` keeps application state **30 days** by default or with `store=true`. With `store=false`: background responses are deleted after the ~10-minute polling window; server-side compaction keeps nothing. No other `store=false` retention rule is stated for a plain synchronous request — so `store=false` removes the *application state*, **not** the abuse-monitoring log. | same |
| `store=false` (V-3) | Must be sent explicitly on every request (`store` defaults to true). Under ZDR it is always treated as false. | same; [Migrate to Responses](https://developers.openai.com/api/docs/guides/migrate-to-responses) |
| ZDR / Modified Abuse Monitoring (V-4, V-5) | Only with **prior approval by OpenAI** and additional requirements (through sales). MAM excludes customer content from abuse-monitoring logs (rare exceptions for image/file inputs); ZDR does the same and forces `store=false`. Eligible: Responses, Chat Completions and others (Responses with limitations); **not** eligible: Conversations, Threads, Assistants, Vector Stores, Files, Batches, Fine-tuning, Evals, Agents. Some models can be excluded for a specific customer. | [Data controls](https://developers.openai.com/api/docs/guides/your-data) |
| Data residency (V-6) | Regions include the US and Europe (EEA + CH, `eu.api.openai.com`, with regional *processing*); non-US residency needs approval for abuse-monitoring controls and a Modified Retention amendment, via sales; system data (account, usage, billing) is not covered; +10 % for models released on/after 2026-03-05. | same; [Pricing](https://developers.openai.com/api/docs/pricing) |
| DPA and transfers (V-7) | `openai.com/policies/data-processing-addendum` again answered **HTTP 403** to the automated fetch. Search-result excerpts of the official page: EEA/Swiss data processed by OpenAI Ireland Ltd.; transfers outside the EEA/CH on SCCs or an adequacy decision; the customer is responsible for notices/consents and for its retention settings. **Not read first-hand**; applicability to this account type still open. | [DPA](https://openai.com/policies/data-processing-addendum/) (403); search excerpts only |
| Models with strict structured output (V-10) | Strict `json_schema` on `text.format` from GPT-4o / `gpt-4o-mini` snapshots onward and later models; the guide recommends its current flagship for new projects. A refusal is a `refusal` content item that does not follow the schema; `incomplete` with `max_output_tokens` means truncation — both must be handled before parsing (the F3-A protocol maps a refusal to `interpretation_refused`, anything else unparsable to `interpretation_failed`). Every property `required`, `additionalProperties: false` (as in `interpretation.schema.json`). The list of supported JSON Schema keywords was not read in full: the canonical schema deliberately uses only `type`, `enum`, `properties`, `required`, `additionalProperties`, `items` (duplicates in `ambiguous_fields` are a validator rule, not `uniqueItems`). Deprecation schedule of the candidates: not read. | [Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs) |
| Rate limits (V-8, V-9) | Per organisation and project, per model: RPM, RPD, TPM, TPD; tiers Free / Build / Launch / Grow by cumulative purchases (with monthly usage caps); exceeding → **HTTP 429** (`slow_down` for a too-fast ramp), overload → **503** `server_is_overloaded`; `Retry-After` and `x-ratelimit-*` headers; failed requests count towards the limit. | [Rate limits](https://developers.openai.com/api/docs/guides/rate-limits) |
| Cost controls (V-8) | Organisation/project **spend alerts** (notify only) and **hard spend limits** (requests then get 429); a monthly approved usage limit per organisation. Prices per 1M tokens range from cents (nano/mini class) to tens of dollars (flagship); the price per Assistant request is still to be measured (no traffic made). | same; [Pricing](https://developers.openai.com/api/docs/pricing) |

**Still unverified on the concrete account** (cannot be read without logging into it or contacting
OpenAI): organisation vs individual account and which terms/DPA apply; current tier, limits and monthly
usage cap; whether ZDR/MAM or EU residency is (or could be) approved; which models the project may use; the
actual tokens and cost per Assistant request (needs the F3-B synthetic evaluation); incident history.

**Owner decisions required before F3-B** (none is taken here):

1. Accept or reject OpenAI as the Assistant provider, under a named account/organisation and terms
   (DPA applicability recorded first-hand).
2. Accept the up-to-30-day abuse-monitoring retention of single messages (amounts, merchants, people's
   names), or request MAM/ZDR first; accept US processing or request EU residency (approval, +10 %).
3. Record the privacy acceptance for Assistant content (financial text, names, amounts, category names),
   as AI-006.1 §7.1 did for Journal RAG.
4. D-6 (category names: all ≤ 100 per type, as implemented, or none), D-5 (templates only, as implemented),
   D-7 (no history, as implemented), D-10 (model, `store=false`, no tools, temperature/reasoning settings).
5. Budget: a project hard spend limit and alert values for the Assistant project.
6. Whether chat may ever confirm or cancel (F3-A: never; only the explicit endpoints).

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
