# ADR-013: OpenAI as the external AI provider for Journal RAG

## Status

Accepted on 2026-10-11 with [AI-006.1](../tasks/ai/AI-006.1.md). The evidence is in
[AI-006](../tasks/ai/AI-006.md). This ADR closes the deferred "ADR for the external journal
providers" item (AI-004 §20, AI-006 §10). It amends ADR-011 only by adding a second external
provider to `lifeos-ai`, for Journal Memory only.

## Context

AI-004 built the Journal Memory Layer:

- journal entries are chunked and embedded (1536 dimensions);
- the vectors and a lexical index are stored in LifeOS PostgreSQL (pgvector);
- retrieval is a versioned SQL function with Reciprocal Rank Fusion (RRF);
- an answer model writes grounded answers that cite only the retrieved passages.

The planned providers were Google Gemini for embeddings and Groq for answers.

During release closure:

- The quality of Gemini embeddings was **not** rejected. Gemini passed every AI-005.1 holdout
  quality gate.
- Gemini free-tier HTTP 429 limits repeatedly prevented a complete, reliable validation run
  (AI-005.1 failed `complete_coverage` twice).
- Google billing and project administration became a release-time blocker.
- OpenAI API activation and billing succeeded. In a manual smoke test by the owner,
  `text-embedding-3-small` returned 1536-dimensional embeddings and `gpt-6.1-sol` worked on the
  Responses API.

AI-006 evaluated one frozen OpenAI candidate:

- Retrieval passed every gate on the development set and on the holdout.
- Answer generation passed every gate on the development set and on the holdout.
- End-to-end: 12/13. The one failure, `allergy-drops`, was a conservative over-refusal: the exact
  evidence was retrieved at rank 1 and the model answered `insufficient_evidence`.
- Formal acceptance was FAIL on the `safety_cases` gate. The release decision explicitly accepts the
  known limitation (AI-006 §9).

## Decision

Journal RAG uses OpenAI in production, through the stateless `lifeos-ai` service (plain HTTPS, no
SDK).

**Embeddings**

| Setting | Value |
| --- | --- |
| Provider | OpenAI |
| Model | `text-embedding-3-small` |
| Dimensions | 1536, sent explicitly |

This is the pinned index identity in `JournalMemoryPolicy`. Any other identity is rejected.

**Answer generation**

| Setting | Value |
| --- | --- |
| Provider and model | OpenAI `gpt-6.1-sol` |
| API | Responses API |
| Prompt | `journal-rag-answer-v1` |
| Reasoning effort | `medium` |
| Storage | `store=false` |
| Output | strict structured output over the existing answer schema |
| Tools | none |
| Conversation state | none, and no `previous_response_id` |

**LifeOS keeps retrieval:**

- PostgreSQL with pgvector;
- lexical full-text search;
- `search_journal_memory_v2` (`journal-retrieval-v2`) with RRF;
- index-identity filtering;
- citations that LifeOS validates and maps back to the user's own entries.

**Not delegated to OpenAI:**

- source-of-truth storage: journal entries stay in LifeOS PostgreSQL;
- the retrieval database: there are no OpenAI vector stores, no File Search and no web search;
- authorization and user isolation: the owner comes from the LifeOS access token, and every query is
  scoped by user;
- ranking policy: the SQL function is versioned and owned by LifeOS;
- journal writes: OpenAI never writes a journal entry, and Python never touches the database
  (ADR-002, ADR-003);
- action execution: Journal RAG answers questions and executes nothing.

The Gemini and Groq journal adapters stay in the code. They can be selected explicitly for rollback
or testing, with no automatic fallback. Nutrition, Weekly Review and the Action Agent stay on Groq.

**Privacy.** The owner accepted the external processing on 2026-10-11. The details are in
AI-006.1 §7.1.

## Consequences

Positive:

- One provider serves both Journal embeddings and answers.
- The existing `vector(1536)` schema, indexes and SQL functions are kept unchanged.
- The Onion boundary is preserved: the provider is an outer adapter, and .NET sees only identity
  strings.
- Retrieval stays under LifeOS control in PostgreSQL.
- The final evaluation results were strong (AI-006 §9).

Trade-offs:

- Journal text leaves LifeOS in explicit embedding and answer requests.
- OpenAI API availability and cost become Journal Memory dependencies. An outage gives a safe 503,
  and the journal itself keeps working.
- Changing the embedding provider means rebuilding the index under the new identity. AI-006.1 does
  this with a data-only re-queue and the bounded sync.
- The known medical over-refusal stays accepted for this release.

Rollback:

- **Answer only:** switch to Groq by explicit selection. No re-index is needed, because the answer
  provider is not part of the index identity.
- **Full embedding rollback:** switch to Gemini, re-queue the entries and re-index them under the old
  identity (AI-006.1 §10).

## Alternatives considered

- **Gemini embeddings.** Technically viable. Not selected for this release, because free-tier
  reliability and billing and project friction blocked finalization.
- **Groq / GPT-OSS answers.** Groq stays in use for other LifeOS AI capabilities. Its Journal answer
  quality did not meet the desired final Journal RAG behaviour (AI-005, AI-005.2).
- **Claude / Anthropic.** Not selected. It would still need a separate embedding provider and would
  add another provider during release closure.
- **OpenAI vector stores / File Search.** Explicitly rejected. LifeOS PostgreSQL stays the retrieval
  engine.
