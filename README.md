# LifeOS

**Personal Intelligence Platform built with .NET and Python.**

LifeOS is a personal application for tracking and understanding areas of
everyday life: finance, training, nutrition and hydration, habits, reminders and
a personal journal. It is built with deliberate architecture, tests and
documentation. Applied AI capabilities are introduced progressively, only where
they solve concrete problems, and never write data without the user's explicit confirmation.

## Current status (2026-11-01)

Status words used in this repository:

| Word | Meaning |
|---|---|
| **Implemented** | The code exists |
| **Tested** | Automated tests exist and a run was recorded as passing |
| **Validated on Pixel 9** | Exercised manually on the development phone, against a local (Development) API |
| **In `main`** | Merged into the `main` branch |
| **In Production** | Deployed by the [production runbook](docs/operations/production-runbook.md) and verified there |
| **Planned** | Not implemented |

Every area below is **implemented, in `main` and in Production**: `lifeos-api` and
`lifeos-ai` on `2b1ecc3`, every migration applied, Android app 1.8.0 (versionCode
18000), released on 2026-11-01. Releases are manual; the latest one is recorded in
[runbook §I9](docs/operations/production-runbook.md#i9-execution-record-2026-10-31--2026-11-01).
Phone acceptance is tracked in each task document under [`docs/tasks/`](docs/tasks/);
the owner accepted the Production app 1.8.0 on 2026-11-01.

| Area | What exists | Main documents |
|---|---|---|
| Finance | Accounts and opening balances, transactions (income, expense, transfer), categories, monthly budgets, recurring planning with manual confirmation, one-off planned expenses, reconciliation, portfolio and analytics | [FIN-001…006](docs/tasks/finance/), ADR-007, ADR-009, ADR-010 |
| Gym | Programs, workout sessions with snapshots, active workout, history | [GYM-001…004](docs/tasks/gym/), ADR-008 |
| Nutrition | Text meal journal, AI nutrition estimate on request (Groq), nutrition targets | [NUT-001…003](docs/tasks/nutrition/) |
| Hydration | Water intakes, daily target, quick-add, shown in the Food diary and Home | [HYD-001](docs/tasks/nutrition/HYD-001.md) |
| Habits | Manual habits and Hydration-sourced habits | [HAB-001](docs/tasks/habits/HAB-001.md) |
| Reminders | User reminders, conditional habit and water reminders, Finance reminders; push through Firebase Cloud Messaging; server-side automation tick | [REM-001](docs/tasks/automation/REM-001.md), [AUTO-001…003A](docs/tasks/automation/), ADR-012 |
| Journal and RAG | Personal journal; Journal Memory (chunking, embeddings, hybrid retrieval in PostgreSQL/pgvector, grounded answers, "Ask Journal") | [JRN-001…003](docs/tasks/journal/), [AI-004…006.1](docs/tasks/ai/), ADR-013 |
| Weekly Review | Deterministic weekly figures, scheduled generation, AI insights | [AUTO-002](docs/tasks/automation/AUTO-002.md), [AI-001](docs/tasks/ai/AI-001.md) |
| Action Agents | Bounded tool-use agent that proposes budget adjustments for explicit approval | [AI-002](docs/tasks/ai/AI-002.md), [AI-003](docs/tasks/ai/AI-003.md) |
| LifeOS Assistant | Persistent chat that prepares drafts; a record is written only by the Confirm button. Finance (AI-007); water intakes and meals (AI-008) | [AI-007](docs/tasks/ai/AI-007.md), [AI-008](docs/tasks/ai/AI-008.md), ADR-014 |

### LifeOS Assistant in Production

- **AI-007 + AI-008 (Finance, water intakes and meals in one chat)**: released
  together on 2026-11-01 by [runbook Part I](docs/operations/production-runbook.md#part-i--lifeos-assistant-multimodule-ai-008-blocked):
  `gpt-6-luna`, prompt `assistant-interpret-v3` (protocol v2), both module flags
  on. Every suite was green (2026-10-31) and the synthetic live evaluation was
  decided by the owner (G1 accepted with one critical Finance-holdout case, G2
  met).
- [ADR-014](docs/adr/0014-external-ai-provider-for-lifeos-assistant.md) is still
  **PROPOSED**: formal acceptance is open, and the written privacy acceptance for
  water and meal content (G4/G5) was waived, not met.

**Planned (not implemented)**: Cycle Tracker ([CYC-001](docs/tasks/health/CYC-001.md),
candidate specification), vision-assisted meal logging, analytical questions to
the Assistant, Assistant modules beyond Finance, water and meals.

## Architecture

```text
MAUI Blazor Hybrid (Android)
        │  HTTPS
        ▼
ASP.NET Core API (LifeOS.Api) ──HTTP + service key──► lifeos-ai (Python, FastAPI) ──► Groq, OpenAI
        │                                                (stateless: interprets, never writes)
        ▼
   Application ── Domain
        ▲
        │  implements Application ports
 Infrastructure (EF Core)
        │
        ▼
   PostgreSQL (pgvector)
```

- **.NET is the authoritative application/business layer** (ADR-002). All
  business rules and all writes go through it.
- **PostgreSQL is the authoritative persistent datastore** (ADR-005).
- **Python is the intelligence layer** (ADR-003, ADR-011): `src/python/lifeos-ai`
  returns structured proposals; it never accesses PostgreSQL, and the phone never
  calls it.
- AI providers are infrastructure (ADR-004) and each one is approved per
  capability (ADR-011, ADR-013, ADR-014).

More detail:
- [Architecture overview](docs/architecture/overview.md)
- [Dependency rules](docs/architecture/dependency-rules.md)

## Tech stack

- .NET 10, ASP.NET Core (Minimal APIs), .NET MAUI Blazor Hybrid (Android)
- EF Core with Npgsql, PostgreSQL 18 with pgvector
- Python, FastAPI, uv (`src/python/lifeos-ai`); offline evaluation lab in `tools/ai-evals`
- Providers: Groq (nutrition estimate, Weekly Review insights, Action Agent),
  OpenAI (Journal Memory; LifeOS Assistant)
- Firebase Cloud Messaging (push)
- Docker; Render Free and Neon Free in Production
- xUnit, NetArchTest, Testcontainers (PostgreSQL), pytest, ruff

## Repository structure

```text
src/dotnet/
├── LifeOS.Domain           business model and invariants
├── LifeOS.Application      use cases and ports
├── LifeOS.Contracts        HTTP request/response contracts
├── LifeOS.Infrastructure   EF Core, PostgreSQL, migrations, provider clients
├── LifeOS.Api              ASP.NET Core API (composition root)
└── LifeOS.App              .NET MAUI Blazor Hybrid client (Android)
src/python/lifeos-ai/       Python AI service (FastAPI)
tests/dotnet/               unit, architecture and integration (HTTP, PostgreSQL) tests
tools/ai-evals/             offline AI evaluation lab
tools/dev/                  development launcher for the Pixel 9
docs/                       architecture, ADRs, contracts, development, operations, tasks
```

## Run locally

Windows, PowerShell, from the repository root. In short:

1. Copy `.env.example` to `.env` and set the API connection string in .NET User Secrets.
2. Start PostgreSQL: `docker compose up -d postgres`.
3. Apply migrations with the local `dotnet-ef` tool (nothing applies them automatically).
4. Start the API: `dotnet run --project src/dotnet/LifeOS.Api/LifeOS.Api.csproj --launch-profile http` (port 5050).
5. Run the Android app on an emulator or a physical device.

For the Assistant on the Pixel 9, the launcher starts PostgreSQL, the Python
service, the API and the app (synthetic data only; messages go to OpenAI):

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\dev\start-lifeos-dev.ps1 -Repo (Get-Location).Path
powershell -ExecutionPolicy Bypass -File .\tools\dev\start-lifeos-dev.ps1 -Repo (Get-Location).Path -BuildAndInstall
```

The second form also builds the Debug APK and updates it on the phone, keeping
its data.

**→ Full guides: [Local development](docs/development/local-development.md),
[Physical device debugging](docs/development/physical-device-debugging.md),
[Android setup](docs/development/android-setup.md)**

## Testing

```powershell
dotnet test tests/dotnet/LifeOS.UnitTests/LifeOS.UnitTests.csproj
dotnet test tests/dotnet/LifeOS.ArchitectureTests/LifeOS.ArchitectureTests.csproj
dotnet test tests/dotnet/LifeOS.IntegrationTests/LifeOS.IntegrationTests.csproj   # Docker required
```

Python service (offline, no key): from `src/python/lifeos-ai`,
`uv run python -m pytest`, `uv run ruff check .`.

The integration suite starts PostgreSQL containers and needs a lot of memory.
Do not report a suite as passing unless a complete run passed.

## Architectural decisions

Significant decisions are recorded as ADRs: see the
**[ADR index](docs/adr/README.md)**.

## Documentation

- [Architecture overview](docs/architecture/overview.md)
- [Dependency rules](docs/architecture/dependency-rules.md)
- [ADR index](docs/adr/README.md)
- [Local development](docs/development/local-development.md)
- [Android setup](docs/development/android-setup.md)
- [Physical device debugging](docs/development/physical-device-debugging.md)
- [Operations](docs/operations/README.md): production runbook, backup and restore
- [Assistant interpretation contracts](docs/contracts/assistant-interpretation/v2/README.md) (v1 and v2)
- [Python AI service](src/python/lifeos-ai/README.md) and [AI evaluation lab](tools/ai-evals/README.md)
- [Engineering guidelines for contributors and AI agents](AGENTS.md)
