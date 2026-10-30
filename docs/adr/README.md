# Architecture decision records

Significant decisions of LifeOS. An ADR is not rewritten when it is superseded or amended: later decisions
are added as dated sections or new ADRs, so the history stays readable.

| ADR | Decision | Status (2026-10-27) |
|---|---|---|
| [ADR-001](0001-use-onion-architecture.md) | Onion Architecture | Accepted |
| [ADR-002](0002-dotnet-is-system-of-record.md) | .NET as the authoritative application layer (system of record) | Accepted |
| [ADR-003](0003-python-is-ai-intelligence-layer.md) | Python as the AI intelligence layer | Accepted (service implemented since NUT-002, see ADR-011) |
| [ADR-004](0004-ai-frameworks-are-infrastructure.md) | AI frameworks and provider SDKs are infrastructure dependencies | Status not recorded; applied through `AGENTS.md` |
| [ADR-005](0005-use-postgresql-as-primary-database.md) | PostgreSQL as the primary datastore | Accepted |
| [ADR-006](0006-external-identity-authentication-and-user-owned-data.md) | External identity authentication, LifeOS-issued sessions, per-user data ownership | Accepted |
| [ADR-007](0007-opening-balances-and-derived-account-balances.md) | Opening balances and derived account balances | Accepted |
| [ADR-008](0008-workout-execution-snapshots.md) | Workout execution snapshots | Accepted |
| [ADR-009](0009-monthly-recurring-planning-and-manual-confirmation.md) | Monthly recurring planning and manual confirmation | Accepted (FIN-004) |
| [ADR-010](0010-one-off-planned-expenses.md) | One-off planned expenses and manual confirmation | Accepted (FIN-005) |
| [ADR-011](0011-production-python-ai-service.md) | First production Python AI service (`lifeos-ai`) | Accepted (NUT-002); amended by PROD-AI-001 |
| [ADR-012](0012-server-side-automation-and-push-notifications.md) | Server-side automation and push notifications | Accepted (AUTO-001) |
| [ADR-013](0013-openai-provider-for-journal-rag.md) | OpenAI as the external provider for Journal RAG | Accepted 2026-10-11 (AI-006.1) |
| [ADR-014](0014-external-ai-provider-for-lifeos-assistant.md) | External AI provider and data handling for LifeOS Assistant | **Proposed**. Owner product decision for Finance v1 (2026-10-26); AI-008 water and meals for Development only, release blocked; formal acceptance conditions 1–2 open |
| [ADR-015](0015-quick-capture-router-and-specialist-interpreters.md) | Quick Capture: LLM router, specialist interpreters, deterministic .NET orchestration (AI-009) | **Proposed** (2026-10-29). Architecture approved at Gate G1; realisation proposed; not implemented. Privacy: proposed [ADR-014 Quick Capture addendum](0014-external-ai-provider-for-lifeos-assistant.md#quick-capture-addendum-ai-009--proposed-2026-10-29) |

Related: [architecture overview](../architecture/overview.md), [dependency rules](../architecture/dependency-rules.md),
[`AGENTS.md`](../../AGENTS.md).
