# ADR-014: External AI provider and data handling for LifeOS Assistant

## Status

**PROPOSED / NOT APPROVED** — draft written in AI-007 F0 (2026-10-24).

**Product owner release decision (2026-10-26)**: OpenAI `gpt-6-luna` with prompt
`assistant-finance-interpret-v2` is approved for production use of LifeOS Assistant v1 (Finance only),
in the existing LifeOS OpenAI project; see [Owner release decision](#owner-release-decision-2026-10-26).
That is a product decision: the contractual/account verifications and the acceptance conditions below
that it does not answer stay **open**, so the ADR keeps its PROPOSED status until they are recorded.

**AI-008 addendum (2026-10-27)**: water intakes and meals are added to the Assistant **for development and
experimentation only** (not released); see [AI-008 addendum](#ai-008-addendum--hydration-and-nutrition-meals-2026-10-27).
The Finance production decision above is unchanged in scope; its prompt changes with protocol v2 and must be
re-evaluated before any release (same section).

This ADR does not authorise sending real data to any provider. Until it is accepted:

- no LifeOS Assistant request with real user data may be sent to an external provider;
- F3 of [AI-007](../tasks/ai/AI-007.md) may not start any provider traffic (F3-B). F3-A — the offline
  orchestrator, protocol and disabled-by-default plumbing, with no request to any provider, real or
  synthetic — was carried out on 2026-10-25 by the owner's explicit brief; see
  [F3-A review](#f3-a-review-2026-10-25). F3-B1 — the OpenAI adapter, prompt, synthetic
  evaluation and live runner, prepared and tested offline but **not selectable and never run** —
  followed on the same day; see [F3-B1 review](#f3-b1-review-2026-10-25-third-reading) and its
  [consolidation](#f3-b1-consolidation-2026-10-25-fourth-reading) (owner product decisions P-1…P-6,
  spend limits not an exact cap);
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
| V-7 | Applicable terms for this account type (individual/organisation), data processing agreement availability | partly: DPA v.010126 and Services Agreement v.010126 read first-hand (F3-B1, B-1/B-2); applicability to this account and acceptance **not verified** | open |
| V-8 | Price per request for the expected size (input/output tokens) and rate limits on the current tier | partly: list prices and per-model limits read (F3-B1, B-8/B-9); offline estimate per request (B-11); measured cost needs the live synthetic run; account tier unknown | open (to be measured) |
| V-9 | Availability and incident history; behaviour under rate limiting (429) | partly: 429/503 behaviour and headers documented; non-retryable quota codes (B-7); incident history not read | open |
| V-10 | Model candidates that support strict structured output on the chosen endpoint, and their deprecation schedule | partly: two candidates with Structured Outputs on Responses (B-9); no deprecation listed on their pages; quality unmeasured | open |

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

## F3-B1 review (2026-10-25, third reading)

Written during AI-007 F3-B1 (OpenAI adapter, prompt, evaluation and live runner prepared offline).
**Nothing was sent to OpenAI**: no API request, no key, no real or synthetic data; the live runner
was not run. The status stays **PROPOSED / NOT APPROVED**. All sources were read on 2026-10-25;
none of the web pages carries a "last updated" date, so each fact must be re-read at acceptance.

### Official sources read (2026-10-25)

| # | Fact | Source |
| --- | --- | --- |
| B-1 | **DPA v.010126** (PDF, created 2025-12-05) read first-hand: it "supplements, and is incorporated into, the OpenAI Services Agreement"; formed "by clicking 'I agree', accepting the Order Form, or using the Services"; OpenAI "acts as a Data Processor on the Customer's behalf"; EEA/Swiss customers contract with **OpenAI Ireland Ltd.**; onward transfers outside the EEA/CH on **SCCs or an adequacy decision** (§4.1); general authorisation of the sub-processors listed at `platform.openai.com/subprocessors`, changes notified, **30-day objection** window (§2.9); breach notice "without undue delay" (§2.7); return or deletion after termination (§2.11); the customer warrants notices/consents (§3.1) and is responsible for configurations such as retention and deletion (§3.3); Schedule 1: "No sensitive data is intended to be transferred unless the user includes it unexpectedly in unstructured data". The PDF ends with an "Execute Data Processing Agreement" link (a signature workflow exists). The HTML page still answers 403 to the automated fetch. | [DPA PDF](https://cdn.openai.com/pdf/openai-data-processing-addendum.pdf); [DPA page](https://openai.com/policies/data-processing-addendum/) (403) |
| B-2 | **OpenAI Services Agreement ONLINE v.010126** read first-hand: entered by "the organization agreeing to these terms"; Customer Content used "only ... as necessary to provide ... the Services, comply with applicable law, enforce the OpenAI Policies, and prevent abuse", not to improve the Services unless the customer explicitly agrees (§4.2); the DPA applies if Personal Data is processed (§5.3); Customer Content deleted within 30 days of termination, except legal holds and Abusive Customer Content (§11.3); EEA customers: Irish law, Dublin courts; price changes effective 14 days after posting (§6.6). | [Services Agreement PDF](https://cdn.openai.com/osa/openai-services-agreement.pdf) |
| B-3 | Data controls (re-read): abuse-monitoring logs may contain prompts and responses, up to 30 days (longer if required by law or to protect from harm); the endpoint table lists `/v1/responses` application state as "None, see below for exceptions" (exceptions: background ~10 min, audio 1 h, hosted containers); **prompt caching may keep encrypted key/value tensors in GPU-local storage up to 24 h** as application state (also under ZDR). | [Data controls](https://developers.openai.com/api/docs/guides/your-data) |
| B-4 | MAM / ZDR: prior approval by OpenAI and additional requirements (through sales); set per organisation or project; ZDR forces `store=false` on `/v1/responses`. | same |
| B-5 | Data residency: the **EU region requires ZDR, MAM, Private Retention with PSP or Safety Retention**, approval for abuse-monitoring controls and a Modified Retention amendment; the region is chosen when a project is created (or the regional domain is used with a key of a "Global" project); `eu.api.openai.com` terminates TLS in the region; system data (account, usage, billing) is not covered; where regional processing is unavailable content may be processed outside the region; +10 % for models released on/after 2026-03-05. | same; [Pricing](https://developers.openai.com/api/docs/pricing) |
| B-6 | Responses API reference: `store` defaults to **true** (≥ 30 days when stored); `max_output_tokens` counts visible and reasoning tokens; `incomplete_details.reason` ∈ `max_output_tokens`, `max_messages`, `content_filter`, `steered`; `safety_identifier` is a recommended stable per-user id (hashed) and `user` is deprecated; `prompt_cache_options` (gpt-5.6+) has `ttl` default `30m`. | [Responses create](https://developers.openai.com/api/reference/resources/responses/methods/create) |
| B-7 | Error codes: retry 429 rate limits, 500, 503, timeouts and connection errors with exponential backoff **with jitter** and a cap; do **not** retry 400, 401, 403 or billing/spend/quota 429s (`insufficient_quota`, `credit_balance_exhausted`, `organization_spend_limit_exceeded`, `project_spend_limit_exceeded`, `organization_usage_limit_exceeded`); honour `Retry-After` as a minimum. | [Error codes](https://developers.openai.com/api/docs/guides/error-codes); [Rate limits](https://developers.openai.com/api/docs/guides/rate-limits) |
| B-8 | Tiers: Free (100 USD/month), Build (5 USD paid, 500 USD/month), Launch (100 USD, 5 000 USD/month), Grow (500 USD, 200 000 USD/month); spend **alerts** notify only, **hard spend limits** make requests fail with 429. | [Rate limits](https://developers.openai.com/api/docs/guides/rate-limits) |
| B-9 | Candidates (model pages): **`gpt-6-luna`** — "most efficient model for focused, high-volume tasks"; Responses + Structured Outputs; reasoning `none`…`max`; 0.10 / 0.01 cached / 0.125 cache write / 0.50 USD per 1M tokens; Build tier 5 000 RPM, 2 M TPM; EU residency available; no deprecation listed. **`gpt-6.1-sol`** (the Journal answer model of ADR-013) — Responses + Structured Outputs; reasoning `low`…`max` (no `none`/`minimal`); 2.00 / 0.10 / 2.50 / 10.00 USD; Build tier 5 000 RPM, 1 M TPM; US/EU residency; no deprecation listed. ZDR eligibility is not mentioned on either page. | [gpt-6-luna](https://developers.openai.com/api/docs/models/gpt-6-luna); [gpt-6.1-sol](https://developers.openai.com/api/docs/models/gpt-6.1-sol); [Pricing](https://developers.openai.com/api/docs/pricing) |
| B-10 | Structured Outputs subset: types string/number/integer/boolean/object/array, `enum`, `anyOf`; every property `required`; `additionalProperties: false`; root an object without `anyOf`; ≤ 5 000 properties, ≤ 10 nesting levels, ≤ 1 000 enum values; nullable via `["type","null"]` (and `null` inside a string enum in the guide's examples); integer `enum` **not documented**; the first request with a new schema has extra latency. | [Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs) |
| B-11 | Not a source fact — LifeOS estimate, offline dry run on the frozen 70-case synthetic dataset with the B-9 prices (input priced at the cache-write rate): `gpt-6-luna` typical ≈ 0.00065 USD/request, upper bound ≈ 0.0048 USD (two attempts, full output allowance); `gpt-6.1-sol` typical ≈ 0.013 USD, upper bound ≈ 0.096 USD. | `tools/ai-evals` live runner (dry run) |

Still not read first-hand: the sub-processor list (403; the DPA points to
`platform.openai.com/subprocessors`), the Usage Policies, incident history.

### What this means for the decision (analysis, not legal advice)

- **Roles**: under the Services Agreement + DPA the customer is the controller and OpenAI the
  processor. Whether LifeOS's account is under these business terms at all (they address an
  "organization"; an individual account may be under other terms) and whether the owner's purely
  personal use falls under GDPR's household exemption are **open questions for the owner**, not
  answered here. People named in notes ("Pamela") are third parties whose data would be processed.
- **The DPA is published, not accepted**: nothing in the repository shows that this account has
  accepted or executed it. It must be confirmed in the account (or executed) and recorded.
- **Retention with `store=false`** (adapter): no application state for a plain request (B-3) but
  abuse-monitoring logs up to 30 days and GPU-local prompt-cache tensors up to 24 h remain unless
  ZDR/MAM is approved. MAM, ZDR and EU residency are **not active** to LifeOS's knowledge and need
  OpenAI's approval (B-4, B-5); EU residency is not available without one of them.
- **Minimisation** (implemented, unchanged from F3-A): message, locale, the open draft's field
  names, the user's category names; no ids, accounts, balances, history or dates. The adapter
  additionally sends no `safety_identifier`/`user`/`metadata`/`prompt_cache_key` (B-6): OpenAI
  then attributes abuse signals to the project only — an owner trade-off to accept.
- **Financial text** is sensitive for LifeOS (AGENTS.md) although it is not among the "sensitive
  data" the DPA schedule contemplates; the risk acceptance must name it explicitly.
- **Costs**: B-11 shows both candidates are cheap at personal volume; the choice must follow the
  evaluation gates, not price alone.

### LifeOS-side measures added in F3-B1 (code and offline tests)

- Adapter `OpenAIAssistantInterpreter` (Python): Responses API over plain HTTPS; `store=false` on
  every request (asserted on retries too); no `previous_response_id`, conversation, tools,
  background, include, metadata, user/safety identifiers, prompt-cache key; text only; strict
  `json_schema` = canonical schema with one documented mapping (`protocol_version` integer enum
  dropped; the protocol rule still refuses ≠ 1); output held to protocol v1; a different model in
  the answer is refused; refusal / truncation / empty / invalid JSON / schema / protocol violation
  and timeout / rate limit / quota / 5xx / connection / credentials / permission / bad request are
  distinct log outcomes mapped to the unchanged contract codes.
- **Not selectable**: `LIFEOS_AI_ASSISTANT_PROVIDER=openai` stops the service ("prepared but not
  approved"); enabling it is a reviewed code change after acceptance. A dedicated key variable
  (the Journal key is never reused); no default model or region; incomplete configuration is an
  explicit start-up error; deadline 40 s < .NET 45 s timeout < 90 s lease; ≤ 3 attempts with
  jittered backoff; concurrency limit; in-process daily/monthly spend guard.
- Prompt `assistant-finance-interpret-v1` frozen by SHA-256; synthetic evaluation (70 cases +
  15 malformed outputs) with PROPOSED gates; live runner dry-run by default, `--execute` only with
  the frozen dataset, a cost cap above the worst case and the key in the environment.

### Proposed limits (the owner approves or changes them)

| Item | Low-cost candidate `gpt-6-luna` | Robust candidate `gpt-6.1-sol` |
| --- | --- | --- |
| Reasoning effort | `low` (`none` to be compared in a later, versioned experiment) | `low` (lowest supported) |
| `max_output_tokens` | 2 000 | 2 000 |
| Daily / monthly guard | 0.50 / 5 USD | 2 / 20 USD |
| OpenAI project hard limit | = monthly guard; spend alert at 50 % | same |
| Concurrency | 2 | 2 |
| Attempts / deadline | 2 / 40 s | 2 / 40 s |
| First live synthetic run cap | 0.50 USD (upper bound 0.34) | 7 USD (upper bound 6.73) |

Selection rule proposed: run both on the frozen dataset; keep the cheapest that passes every gate;
if neither passes, no provider is enabled. A circuit breaker is **not** proposed: one user, low
volume, bounded retries, final quota/credential codes not retried and the spend guard already stop
repeated useless traffic (revisit if traffic grows). Immediate switch-off: `AssistantAi:Enabled=false`
(.NET) and/or `LIFEOS_AI_ASSISTANT_PROVIDER=disabled`; key revocation at OpenAI.

### Requires access to the OpenAI account (cannot be verified publicly)

Account type and the terms that govern it; whether the DPA is accepted or executed for it;
organisation/project ids and region of a dedicated Assistant project; MAM/ZDR/EU residency status
(or a request to sales); usage tier, per-model limits and monthly usage limit; which models the
project may call; project hard spend limit and alerts; data-sharing opt-in is off; key creation
and storage in the hosting environment; the sub-processor list as seen from the account.

### Owner decisions still pending (none taken here)

1. Accept OpenAI for the Assistant under a named account/project and terms, with the DPA
   acceptance recorded first-hand.
2. Accept 30-day abuse-monitoring and 24-hour prompt-cache retention, or request MAM/ZDR first;
   `global` processing or EU residency (requires MAM/ZDR, +10 %).
3. Privacy acceptance for Assistant content (financial text, third parties' names, amounts, category
   names), including not sending a `safety_identifier`.
4. Approve or change the PROPOSED gates and limits above **before** the first live run.
5. Authorise the first live run on synthetic data (candidates, cap), then D-10 (model and settings),
   and D-5/D-6/D-7 as already listed.

## F3-B1 consolidation (2026-10-25, fourth reading)

Written during the F3-B1 consolidation. **Nothing was sent to OpenAI** (no request, no key, no real or
synthetic data; the live runner was not run, not even as a dry run). The status stays **PROPOSED / NOT
APPROVED**.

### Owner product decisions (recorded 2026-10-25; they are not a verification of terms)

These are the product owner's stated preferences for LifeOS Assistant v1. They narrow the open decisions
above, but they do **not** accept this ADR and do **not** replace the account and contract checks:

| # | Decision |
| --- | --- |
| P-1 | The first release is **Finance only** (expenses/income drafts). Nutrition, Hydration, Habit Tracker, Journal and general questions about the user's data are out of scope. |
| P-2 | Operational messages get short replies (the existing .NET templates, D-5). |
| P-3 | Real financial data **may** be sent to the OpenAI API **after** the configuration and the applicable conditions have been verified (the account checks below). |
| P-4 | OpenAI's standard log retention (abuse-monitoring logs up to 30 days, B-3) is acceptable for this personal use; requesting MAM/ZDR is **not** required. (This answers owner decision 2 of the F3-B1 review for the retention part; EU residency is not requested and would need MAM/ZDR anyway, B-5.) |
| P-5 | A Finance confirmation is always explicit, through the endpoint/button, **never** through chat text (as implemented since F3-A). |
| P-6 | The provider stays disabled by default (`AssistantAi:Enabled=false`, `LIFEOS_AI_ASSISTANT_PROVIDER=disabled`, `openai` not selectable). |

Still **not verified** (contractual/account facts, not preferences): which terms govern the account
(individual vs organisation), whether the DPA is accepted or executed for it, that data sharing for
training is off for the project, the project's region, tier and model access. P-3 makes these checks the
precondition for real data; P-4 does not make them unnecessary.

### Re-read sources (2026-10-25)

| # | Fact | Source |
| --- | --- | --- |
| C-1 | **Spend limits are not an exact hard cap.** A hard limit makes affected requests fail with `429` (`project_spend_limit_exceeded` / `organization_spend_limit_exceeded`) once *tracked* spend reaches it, but "Enforcement is not instantaneous", "the API Platform can process a small amount of extra usage while the limit state propagates" and "recorded spend can slightly exceed the configured amount". Spend **alerts** "do not enforce a cap". Setting limits needs project/organisation management permission. This qualifies B-8: neither the project limit nor alerts may be presented as a guaranteed cap. | [Spend limits](https://developers.openai.com/api/docs/guides/spend-limits) |
| C-2 | Spend controls are separate from the monthly **usage limit** OpenAI sets per tier (Free 100, Build 500, Launch 5 000, Grow 200 000 USD/month). | [Rate limits](https://developers.openai.com/api/docs/guides/rate-limits) |
| C-3 | `gpt-6-luna`: Responses supported, Structured Outputs supported; reasoning `none`, `low`, `medium` (default), `high`, `xhigh`, `max` (**no `minimal`**); 0.10 input / 0.01 cached / 0.125 cache write / 0.50 output USD per 1M; Build 5 000 RPM, 2 M TPM; only snapshot `gpt-6-luna`; no deprecation listed; EU residency available; ZDR not mentioned. | [gpt-6-luna](https://developers.openai.com/api/docs/models/gpt-6-luna) |
| C-4 | `gpt-6.1-sol`: Responses supported, Structured Outputs supported; reasoning `low` … `max` (default `medium`; no `none`/`minimal`); 2.00 / 0.10 / 2.50 / 10.00 USD per 1M; Build 5 000 RPM, 1 M TPM; only snapshot `gpt-6.1-sol`; no deprecation listed; US/EU residency; ZDR not mentioned. | [gpt-6.1-sol](https://developers.openai.com/api/docs/models/gpt-6.1-sol) |
| C-5 | Pricing table: cache writes are priced per TTL (30 min / 12 h: luna 0.125 / 0.25, sol 2.50 / 5.00). The adapter sends no `prompt_cache_options`, so the default 30-minute TTL (B-6) and the 30-minute rate used by the experiments apply; +10 % on regional (data-residency) endpoints for models released on/after 2026-03-05. No page date (latest dated note: 2026-07-30). | [Pricing](https://developers.openai.com/api/docs/pricing) |

Public pages say nothing about **this account**: model access, tier and limits must be read in the
account (F3-B2). Third-party reports of spend exceeding limits exist; they are not used as facts here.

### Budget: what LifeOS controls and what it does not

- **In-process guard** (`budget.py`): worst-case reservation before each request, settled with the
  reported usage (plus the worst case of earlier failed attempts, which may have been billed). It is
  memory only: **every restart starts from zero** (deploy, crash, Render spin-down) and each instance
  counts only its own traffic, so N restarts in a day allow up to N+1 daily budgets. Unknown usage keeps
  the whole reservation (conservative). Prices are configuration, not read from OpenAI.
- **OpenAI project hard limit**: the outer brake, best-effort (C-1), with a propagation delay.
- **Spend alerts**: notification only.
- **Account usage limit** (C-2): OpenAI's own ceiling for the tier, not an owner budget.

None of the three is a guaranteed hard cap; together, at personal volume and with the B-11 per-request
costs, they bound the exposure to small amounts. A persistent ledger stays deferred.

## F3-B2 — existing OpenAI project (2026-10-25, fifth reading)

**Nothing was sent to OpenAI** (no request, no key read; dry run only). The status stays **PROPOSED / NOT
APPROVED**. Details, dry-run figures and the live-run gate: [AI-007 F3-B2](../tasks/ai/AI-007.md#f3-b2--existing-openai-project-and-dry-run-2026-10-25).

### Owner decisions (recorded 2026-10-25; preferences, not verifications of terms)

| # | Decision |
| --- | --- |
| P-7 | The Assistant **reuses the existing LifeOS OpenAI project** already used by Journal RAG. No new project is created; Journal's configuration, models and behaviour are not changed. A key dedicated to the Assistant inside that project is allowed (recommended), not required to start. This replaces the "own project" wording of the F3-B1 review and of *Consequences* below. |
| P-8 | Privacy: the owner accepts **OpenAI's standard API data-processing conditions** for personal use; **MAM, ZDR and EU data residency are not required** (confirms and extends P-4: `global` processing accepted). |

P-7/P-8 are product decisions. They do **not** accept this ADR, and they are **not** evidence that any
contract term applies to the account.

### Consequences of the shared project

- One organisation data-sharing setting, one set of terms/DPA, one region (`global`, the host Journal
  already uses) and one model allow-list serve both features.
- The project **hard spend limit and per-model rate limits are shared**: Assistant traffic can make
  Journal fail with `429 project_spend_limit_exceeded` and vice versa; spend-limit enforcement is not
  exact (C-1). Each live run needs headroom above its upper bound.
- Logical separation stays in LifeOS: separate variable (`LIFEOS_AI_ASSISTANT_OPENAI_API_KEY`, never
  `OPENAI_API_KEY` as fallback), transport, prompt, model setting, spend guard and switches.
- With a dedicated key, a leaked Assistant key can be revoked without stopping Journal and usage can be
  told apart; reusing Journal's key value loses both.

### Still to verify (in the account, first-hand, recorded with date and place)

1. Organisation and project name/id of the existing project (not a key); project region `global`.
2. Which terms govern the account (individual or organisation; Services Agreement v.010126) and whether
   the **DPA** is accepted or executed — **no evidence in the repository; not declared accepted**.
3. Organisation data-controls: sharing of inputs/outputs for training **off**.
4. `gpt-6-luna` allowed in the project (Journal only proves `gpt-6.1-sol` and the embedding model);
   tier, RPM/TPM, monthly usage limit.
5. Project hard spend limit, alerts and the current month's spend (headroom for each run).
6. Sub-processor list read first-hand; Usage Policies; incident history (V-9).
7. Acceptance of the provider schema by the real API (first live call).

Acceptance conditions below are unchanged: V-1…V-7 answered with sources, the owner's explicit privacy
acceptance for Assistant content recorded (P-8 is the basis for it, not a substitute for the record of
item 2), evaluation gates met on the live synthetic run, model and settings written here.

## Owner release decision (2026-10-26)

Recorded during the AI-007 release preparation. **Nothing was sent to OpenAI** by this work (no key, no
request); no further model comparison or live synthetic evaluation is made (owner decision).

| # | Decision (product owner) |
| --- | --- |
| P-9 | Model `gpt-6-luna` (reasoning `low`, Responses API, `store=false`, strict structured output) is the definitive Assistant model. No other model is compared. |
| P-10 | Real AI is activated in production for LifeOS Assistant v1, Finance expenses and income only. |
| P-11 | The release prompt is `assistant-finance-interpret-v2` (frozen, sha256 `965804d2…94ca`); v1 stays published and frozen. |
| P-12 | The OpenAI project already used by Journal is reused (P-7), with a key dedicated to the Assistant. |
| P-13 | Categories are suggested only among the user's own categories; a category the user chose explicitly prevails over any suggestion; a transaction is recorded only by the explicit confirm button (P-5). |

What the code does as a consequence (AI-007 "Release configuration"): `openai` is selectable on the AI
service only by explicit configuration (`LIFEOS_AI_ASSISTANT_PROVIDER=openai`, default `disabled`), with
`gpt-6-luna` as the default model and v2 as the only production prompt, plus `AssistantAi:Enabled=true`
on the API; the key stays server-side in its own variable, with no fallback to Journal's key or to another
provider. Switch-off is configuration only.

**Evaluation evidence (synthetic, recorded in AI-007)**: `gpt-6-luna` prompt v2 on `holdout-v1` (68 cases)
passed every gate except `critical_error_cases` (1 case, tags `injection` and `not_invented`:
`h_injection_numeric_ids`, where the model returned "conto numero 3" / "categoria numero 12" as stated
mentions). The deterministic .NET resolvers never apply such a mention (no list position, no id; asked
again), now covered by unit and PostgreSQL tests. The owner accepted this residual model risk with the
release decision. Acceptance condition 3 below is therefore **waived by the owner**, not met.

**Still open (not answered by the owner's decision)**: acceptance condition 1 — V-1…V-7 for the
account (which terms govern it, DPA accepted or executed, data sharing off, project region, tier and
model access) recorded first-hand with source and date; acceptance condition 2 — the owner's written
privacy acceptance for Assistant content (P-8 is its basis, not the record); condition 4 is satisfied by
this section (provider, model, settings). Production activation by configuration is the owner's
responsibility under these open items.

## AI-008 addendum — Hydration and Nutrition Meals (2026-10-27)

Owner brief AI-008 (2026-10-27): the Assistant may record **water intakes** (`hydration.intake.create`) and
**meals** of the text meal journal (`nutrition.meal.create`) in the same chat as Finance, for development and
experimentation; no deploy, no release, no change of provider, model, key, region, prices or budgets.

### What is sent to the provider (protocol v2, `docs/contracts/assistant-interpretation/v2`)

Per interpreted message, exactly:

| Item | Content | New in AI-008 |
| --- | --- | --- |
| `message` | the user's message text (≤ 1000 characters), as typed | may now speak of water or food |
| `context.locale` | `it-IT` | no |
| `context.open_draft` | the open draft's `kind`, Finance `type` (`expense`/`income`, null for other kinds), the **names** of its filled and missing fields and the field asked last | the kinds `hydration.intake.create`, `nutrition.meal.create` and their field names (`amount`, `unit`, `description`, `meal_type`, `date`, `time`) |
| `context.finance` | the user's expense and income category names (≤ 100 each) | sent empty while a water or meal draft is open |

Nothing else: no LifeOS identifier, no time zone, no timestamp, no water history, daily total, target or
quick-add setting, no meal history, nutrition value, target plan or estimate, no Journal content, no account,
no balance. The request types make it impossible to add more (strict schema on both sides; the architecture
test that forbids identifiers and instants in the request and the answer now also walks the new sections).

### What comes back, and what LifeOS does with it

The interpretation only (intent and typed fields with verbatim spans). LifeOS validates it again, converts the
stated quantity to whole millilitres itself (ml, cl, l only — a container such as "un bicchiere" is never
converted, the quantity is asked), resolves dates in the user's time zone, keeps a meal description only if it
is the user's own words (no ingredient, quantity or nutrition can be added), and prepares a draft. A water
intake or a meal is written only by the user's explicit confirmation, through `AddHydrationIntakeHandler` /
`CreateMealHandler`. No nutrition estimate is requested (Groq is not called by the Assistant).

### Limits and controls

- **Not released.** `Assistant:Modules:Hydration` and `Assistant:Modules:NutritionMeals` default to `true` in
  Development and `false` everywhere else; with a module off, its intents are answered "not available" and no
  draft of it is opened. The Python service serves protocol 1 unchanged for older callers.
- **Same provider settings as Finance:** Responses API, `store: false`, strict structured output, no tools, no
  provider-side state, no LifeOS identifier, same key, model, region, budgets and spend guard. The protocol-2
  prompt is `assistant-interpret-v3` (frozen by hash), selected automatically for protocol-2 requests; the
  configured protocol-1 prompt (`assistant-finance-interpret-v2`) is unchanged.
- **Data minimisation:** water and meal messages carry personal, possibly health-related information (what and
  when the user eats and drinks; a meal description may name people, e.g. "cena con Pamela"). They reach the
  provider exactly as a Finance note does (same retention terms, still to be verified: V-items above); nothing
  is masked (as decided for Finance), and no other module's data is added to the request.
- **Validation on the Pixel 9** uses the owner's own messages in the development environment, as authorised by
  the brief; synthetic content (invented foods and quantities) is recommended. No third-party data.

### Compatibility with the existing privacy requirements

No existing requirement is weakened: the identifiers rule, the minimisation rule (NFR-5), the no-content-in-logs
rule and the confirmation boundary apply unchanged to the new modules (tests: contract v2 golden cases,
`AssistantModuleArchitectureTests`, `AssistantModulesOrchestrationTests`, `AssistantModulesHttpTests`). The only
tension is the release scope: P-1 ("first release is Finance only") stays true, because the new modules are off
outside Development. **Before any production deploy** of AI-008: (1) the owner decides whether water and meals
are released (an explicit privacy acceptance for that content, as for Finance); (2) `assistant-interpret-v3`
passes the frozen synthetic Finance evaluation (it now also serves Finance, since .NET sends protocol 2 only) and
an equivalent synthetic water/meal evaluation; (3) `lifeos-ai` is deployed before the API.

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
- With OpenAI, one provider and one set of terms serve two features (Journal RAG and the Assistant),
  each with its own transport and key variable, in the same OpenAI project (P-7, F3-B2; the project's
  spend limit and rate limits are shared).
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
