# LifeOS Architecture Overview

> **Scope of this document.** It describes the architecture of LifeOS **as it
> exists in the repository today**, plus decisions that are explicitly recorded
> in `AGENTS.md` or in the ADRs under `docs/adr/`. Anything not yet implemented
> is labelled **Planned** or **Future**. If this document and the code disagree,
> the code is the current truth and this document should be corrected.
>
> **Last reviewed: 2026-10-27, `main` at `733ac2f`.** Sections 7, 8 (schema) and 10
> use the first slice, Finance → Accounts, as a worked example of the pattern;
> they are not a complete inventory. Production state is not recorded in the
> repository (see [operations](../operations/README.md)).

---

## 1. What LifeOS is

LifeOS is a **Personal Intelligence Platform**: a personal application for
tracking and understanding areas of everyday life (finance, training,
nutrition and hydration, habits, reminders, a personal journal), built
primarily with **.NET**, with **Python** for AI capabilities.

It is simultaneously:

1. a real personal application, used by its author;
2. a vehicle for practising **Applied AI Engineering**;
3. a **senior-level portfolio project**, so architecture and engineering
   discipline are deliberate and visible.

## 2. Current goals

- Build meaningful **end-to-end vertical slices** (domain → API → mobile UI)
  rather than isolated layers.
- Keep **.NET as the authoritative application/business layer** (all business
  rules and all writes go through it) and **PostgreSQL as the authoritative
  persistent datastore** (the single place where data is durably stored).
- Use **Android** (via .NET MAUI Blazor Hybrid) as the first client platform.
- Introduce **AI progressively**, only when it solves a concrete problem, and
  never as a way around the application's rules.
- Keep the design simple: no speculative abstractions, no framework adopted just
  to demonstrate familiarity with it.

## 3. Status at a glance

Status words: **Implemented** (code exists, in `main`), **Planned** (not
implemented). Phone acceptance and test results are tracked per task under
`docs/tasks/`. Every implemented area is in Production since 2026-11-01
(`2b1ecc3`, app 1.8.0; runbook §I9).

| Area | Status |
|---|---|
| Onion-architecture .NET solution, architecture tests | **Implemented** |
| Authentication (external identity, LifeOS-issued sessions) and per-user data ownership | **Implemented** (ADR-006) |
| Finance: accounts, opening balances, transactions, categories, budgets, recurring and planned expenses, reconciliation, portfolio, analytics | **Implemented** (FIN-001…006, ADR-007/009/010) |
| Gym: programs, sessions with snapshots, history | **Implemented** (GYM-001…004, ADR-008) |
| Nutrition: meal journal, AI estimate on request, targets | **Implemented** (NUT-001…003) |
| Hydration: water intakes, daily target, quick-add | **Implemented** (HYD-001) |
| Habits, including Hydration-sourced habits | **Implemented** (HAB-001) |
| Reminders, Finance reminders, push (FCM), automation tick | **Implemented** (REM-001, AUTO-001…003A, ADR-012) |
| Journal and Journal Memory (RAG) | **Implemented** (JRN-001…003, AI-004…006.1, ADR-013) |
| Weekly Review with AI insights | **Implemented** (AUTO-002, AI-001) |
| Action Agent (budget adjustment proposals) | **Implemented** (AI-002, AI-003) |
| LifeOS Assistant, Finance (AI-007) | **Implemented, released** 2026-11-01 with AI-008; ADR-014 still Proposed |
| LifeOS Assistant, water and meals (AI-008) | **Implemented, released** 2026-11-01 (runbook Part I) |
| Python AI service `lifeos-ai` | **Implemented** (ADR-011) |
| Production deployment (Render Free, Neon Free) | **Documented** in the production runbook; executed by hand; latest release recorded in runbook §I9 |
| Object storage for binary assets | Planned (ADR-005) |
| Offline SQLite cache on the device | Future, optional (ADR-005) |
| Cycle Tracker | Candidate specification only (CYC-001) |

## 4. Technology stack

**In use today**

| Concern | Technology |
|---|---|
| Runtime / language | .NET 10, C# (nullable enabled) |
| HTTP API | ASP.NET Core Minimal APIs, OpenAPI (development only) |
| Persistence | EF Core 10 with Npgsql (`Npgsql.EntityFrameworkCore.PostgreSQL`) |
| Database | PostgreSQL 18 with pgvector, run locally through Docker Compose (Neon in Production) |
| AI service | Python, FastAPI, uv (`src/python/lifeos-ai`); providers Groq and OpenAI over plain HTTPS (no SDK) |
| Push | Firebase Cloud Messaging |
| Migrations tooling | `dotnet-ef` as a local tool (`dotnet-tools.json`) |
| Client | .NET MAUI Blazor Hybrid (`BlazorWebView`), Bootstrap CSS from the template |
| Primary client platform | Android (the project also targets iOS, macCatalyst and Windows) |
| Tests | xUnit, NetArchTest.Rules, Testcontainers (PostgreSQL), pytest |
| Local secrets | .NET User Secrets (API connection string), `.env` for Docker Compose |

**Planned** (from `AGENTS.md` and the ADRs)

- LangChain / LangGraph / model-provider SDKs only if needed, and then as *infrastructure* dependencies only (ADR-004).
- Object storage for binary assets such as meal photographs (ADR-005).

Deliberately **not** used: MediatR, AutoMapper, FluentValidation, Refit,
generic repositories, CQRS frameworks, UI component libraries.

## 5. Repository and solution structure

```text
lifeos/
├── AGENTS.md                  canonical engineering and architecture rules
├── CLAUDE.md                  tool-specific pointer to AGENTS.md
├── docker-compose.yml         local PostgreSQL 18
├── .env.example               documented Docker Compose variables
├── dotnet-tools.json          local dotnet-ef tool
├── docs/
│   ├── adr/                   architecture decision records (index: adr/README.md)
│   ├── architecture/          this document, dependency rules
│   ├── contracts/             .NET ↔ Python contracts (Assistant interpretation v1, v2)
│   ├── development/           local setup, Android, physical device, acceptance checklists
│   ├── operations/            production runbook, backup and restore
│   └── tasks/                 one document per task (design, decisions, status)
├── src/dotnet/
│   ├── LifeOS.slnx            solution
│   ├── LifeOS.Domain/         business model and rules
│   ├── LifeOS.Application/    use cases and ports
│   ├── LifeOS.Contracts/      HTTP request/response DTOs
│   ├── LifeOS.Infrastructure/ EF Core, PostgreSQL, port implementations
│   ├── LifeOS.Api/            ASP.NET Core host (composition root)
│   └── LifeOS.App/            .NET MAUI Blazor Hybrid client
├── src/python/lifeos-ai/      Python AI service (FastAPI)
├── tools/
│   ├── ai-evals/              offline AI evaluation lab
│   └── dev/                   Pixel 9 development launcher
└── tests/dotnet/
    ├── LifeOS.UnitTests/          Domain, Application, App and endpoint tests
    ├── LifeOS.ArchitectureTests/  dependency-rule tests
    └── LifeOS.IntegrationTests/   HTTP and PostgreSQL (Testcontainers) tests
```

Code is organised **by feature inside each layer**, for example
`Finance/Accounts/...` exists in Domain, Application, Contracts, Infrastructure,
Api and App.

## 6. Onion Architecture

LifeOS follows **Onion Architecture**: dependencies point **inward**, and the
business core knows nothing about frameworks, databases, HTTP or UI.

```text
              ┌───────────────────────────────────────────┐
              │   Outer adapters: LifeOS.Api, LifeOS.App  │
              │  ┌─────────────────────────────────────┐  │
              │  │        LifeOS.Infrastructure        │  │
              │  │  ┌───────────────────────────────┐  │  │
              │  │  │      LifeOS.Application       │  │  │
              │  │  │  ┌─────────────────────────┐  │  │  │
              │  │  │  │      LifeOS.Domain      │  │  │  │
              │  │  │  └─────────────────────────┘  │  │  │
              │  │  └───────────────────────────────┘  │  │
              │  └─────────────────────────────────────┘  │
              └───────────────────────────────────────────┘
                     dependencies point toward the centre
```

### Actual project references

The graph below shows **internal LifeOS project references only**
(`<ProjectReference>` between LifeOS projects). It does not list framework or
NuGet package dependencies. For example, `LifeOS.App` also depends on the .NET
MAUI and BlazorWebView packages, `LifeOS.Infrastructure` on EF Core/Npgsql, and
`LifeOS.Api` on ASP.NET Core.

```text
LifeOS.Domain          → (nothing)
LifeOS.Application     → Domain
LifeOS.Infrastructure  → Application, Domain
LifeOS.Contracts       → (nothing)
LifeOS.Api             → Application, Infrastructure, Contracts
LifeOS.App             → Contracts            (never Infrastructure or Domain)
```

`LifeOS.Api` references Infrastructure only because it is the **composition
root**: it wires Infrastructure implementations to Application ports through
dependency injection (`AddInfrastructure(...)`). Feature endpoints talk to
Application handlers, not to repositories. The one temporary exception is the
technical, Development-only `/health/database` check (see §7, LifeOS.Api).

`LifeOS.Contracts` is deliberately independent of Domain, so the external HTTP
contract can evolve separately from the internal model (for example,
`AccountType` is a C# enum internally but a **string** in the API).

## 7. Project responsibilities

> The bullets naming specific types describe the first slice (Finance →
> Accounts) as it was introduced; every later feature follows the same
> responsibilities. They are not an inventory of the current code.

### LifeOS.Domain
Framework-independent business concepts and invariants.

- Today: `Finance/Accounts/Account` (entity) and `AccountType` (enum).
- `Account.Create(...)` is the only way to create an account. It enforces:
  - the name is required and trimmed;
  - the currency is a 3-letter code (A–Z), normalised to uppercase;
  - the account type must be a defined value;
  - the creation timestamp is normalised to UTC.
- Ids are generated in the domain with `Guid.CreateVersion7()` (time-ordered UUIDs).
- The creation time is **supplied by the caller**; the domain never reads the clock.
- It has no dependencies: no EF Core attributes, no ASP.NET, no AI frameworks.
- Validation failures currently throw standard .NET `ArgumentException` types.

### LifeOS.Application
Use cases and the ports (interfaces) they need.

- `IAccountRepository` is a deliberately minimal port with `AddAsync` and
  `GetAllAsync`. There is no generic repository.
- `CreateAccountHandler` gets the current time from `TimeProvider`, calls
  `Account.Create`, persists through the port and returns a `CreateAccountResult`.
- `GetAccountsHandler` reads through the port and returns `AccountSummary` records.
- Handlers are plain classes. There is no mediator or pipeline framework.
- Depends only on Domain, plus BCL types such as `TimeProvider`.

### LifeOS.Contracts
Plain HTTP data-transfer records shared by the API and the client.

- `CreateAccountRequest(Name, Type, Currency)`
- `AccountResponse(Id, Name, Type, Currency, CreatedAtUtc)`

It has no dependencies, which lets the MAUI client reuse the same types
without seeing the domain.

### LifeOS.Infrastructure
Concrete implementations of Application ports.

- `LifeOSDbContext` (EF Core) with `DbSet<Account>`.
- `AccountConfiguration` is an explicit EF Core mapping:
  - table `accounts` with snake_case columns;
  - the account type is stored as its **string name**;
  - `currency` is `character(3)` and `created_at_utc` is `timestamptz`.
- `AccountRepository` implements `IAccountRepository`. Writes call `SaveChangesAsync`; reads use `AsNoTracking()`.
- Migrations live in `Persistence/Migrations` (one per schema change; latest on 2026-10-27: `GeneralizeAssistantOperations`, AI-008). Nothing applies them at start-up.
- `DependencyInjection.AddInfrastructure(connectionString)` registers the DbContext (Npgsql) and repositories.
- EF Core appears **only** in this project (and as the design-time package in the Api host).

### LifeOS.Api
The ASP.NET Core host and HTTP adapter.

- `Program.cs` composes the application:
  - `AddInfrastructure`
  - `TimeProvider.System`
  - Application handlers
  - endpoint modules
- `Finance/AccountEndpoints.cs` is a feature endpoint module (`MapAccountEndpoints`). It:
  - parses the account type string strictly: case-insensitive names only, never numbers;
  - maps Contracts ↔ Application types;
  - translates domain validation failures into **HTTP 400 validation problems**.
- There is no business logic in endpoints.
- **Temporary exception: `GET /health/database`.** This technical health check
  is mapped **in Development only** (anonymous polling in production would keep
  the database awake; there is no production health endpoint). It is defined
  inline in `Program.cs` and injects `LifeOSDbContext` directly to
  call `Database.CanConnectAsync()`. It bypasses the normal
  `endpoint → Application handler → port → Infrastructure` flow. It is accepted
  only as a temporary, technical connectivity probe: it contains no business
  logic and no domain data. It is not a pattern for feature endpoints.
- It reads the connection string `ConnectionStrings:PostgreSQL` from configuration (User Secrets in development).

### LifeOS.App
The .NET MAUI Blazor Hybrid client. Android is the primary target.

- The UI is Razor components rendered inside a `BlazorWebView` hosted by a MAUI `ContentPage`.
- `Components/Pages/Finance/Accounts.razor` (`/finance/accounts`) lists accounts and creates new ones.
- `Services/Finance/AccountsApiClient` is the only place that talks HTTP. It:
  - calls `GET` and `POST /api/accounts`;
  - turns failures into an `ApiResult<T>` with readable errors;
  - never exposes `HttpClient` to components.
- `Services/ApiSettings.ForDevelopment()` holds the development API base URL.
- It references **only Contracts**. It never talks to PostgreSQL and holds no authoritative business rules: the backend validates everything.

## 8. Data architecture: PostgreSQL as the authoritative datastore

(ADR-002 title, ADR-005, `AGENTS.md`)

Terminology used in this document:

- **.NET (`LifeOS.Api` → Application → Domain) is the authoritative
  application/business layer.** It owns the business rules and is the only
  path through which data is created or changed.
- **PostgreSQL is the authoritative persistent datastore.** It is the single
  place where LifeOS data is durably stored.

The ADRs use "system of record" for both ideas: ADR-002's title refers to
.NET, and ADR-005 refers to PostgreSQL. Together they mean that data lives in
PostgreSQL and is changed only through .NET.

- **PostgreSQL is the single, authoritative persistent datastore.**
- Development uses PostgreSQL 18 with pgvector in Docker (`docker-compose.yml`).
  Production uses Neon Free (managed PostgreSQL), see the production runbook.
- Only `LifeOS.Infrastructure` touches the database, through EF Core.
- Schema changes go through **EF Core migrations**
  (`dotnet ef migrations add ... --project src/dotnet/LifeOS.Infrastructure --startup-project src/dotnet/LifeOS.Api`).
- Timestamps are stored in UTC (`timestamptz`), and ids are UUID v7 generated by the domain.
- **Planned:** binary assets such as meal photographs go to object storage and
  are referenced by metadata. They are not stored in PostgreSQL.
- **Future:** SQLite may be added as an offline cache on the device, but it
  must never become the authoritative datastore.

Example: the first table (the schema today has many more; see the EF Core
model snapshot in `Persistence/Migrations`):

```text
accounts
├── id              uuid          PK   (UUID v7, generated in Domain)
├── name            text          NOT NULL
├── account_type    varchar(32)   NOT NULL   ('BankAccount', 'Cash', ...)
├── currency        character(3)  NOT NULL   ('EUR', ...)
└── created_at_utc  timestamptz   NOT NULL
```

## 9. Client: MAUI Blazor Hybrid on Android

```text
┌──────────────── Android device / emulator ────────────────┐
│  MAUI Window → MainPage (ContentPage, SafeAreaEdges)      │
│      └── BlazorWebView                                    │
│            └── Razor components (Accounts.razor, ...)     │
│                  └── AccountsApiClient (HttpClient)       │
└──────────────────────────────┬────────────────────────────┘
                               │ HTTP + JSON (LifeOS.Contracts)
                               ▼
                     LifeOS.Api (ASP.NET Core)
```

The client **never** connects to PostgreSQL. All reads and writes go through
the API (ADR-005, `AGENTS.md`).

**Development connectivity** (development only):

| Where the app runs | Base URL | How it reaches the host API |
|---|---|---|
| Android emulator | `http://10.0.2.2:5050/` | the emulator's alias for the host machine |
| Physical Android device | `http://127.0.0.1:5050/` | `adb reverse tcp:5050 tcp:5050` |
| Other platforms (Windows) | `http://localhost:5050/` | direct |

- Plain HTTP to these hosts is allowed **only in Debug builds**. An Android
  network-security config is attached through `#if DEBUG` in `MainApplication.cs`.
- Release builds keep Android's default of blocking plain HTTP.
- Release builds call the Production API over HTTPS (runbook A11).

The API is started for these scenarios with:

```powershell
dotnet run --project src/dotnet/LifeOS.Api --launch-profile http --urls http://localhost:5050
```

## 10. Request and data flow

Worked example on the first slice; authentication (ADR-006) now applies to
every feature endpoint.

### Create account (write path)

```text
Accounts.razor
   │  CreateAccountRequest { name, type: "Cash", currency: "eur" }
   ▼
AccountsApiClient ── POST /api/accounts ──►  AccountEndpoints.CreateAccountAsync   (Api)
                                               │ parse "Cash" → AccountType.Cash
                                               │   (unknown or numeric → 400)
                                               ▼
                                             CreateAccountHandler                  (Application)
                                               │ now = TimeProvider.GetUtcNow()
                                               ▼
                                             Account.Create(...)                   (Domain)
                                               │ validate, trim, uppercase, UUID v7
                                               │   (invalid → ArgumentException → 400)
                                               ▼
                                             IAccountRepository.AddAsync           (Application port)
                                               ▼
                                             AccountRepository → LifeOSDbContext   (Infrastructure)
                                               ▼
                                             PostgreSQL  INSERT INTO accounts
   ◄── 201 Created + AccountResponse ─────────┘
```

### List accounts (read path)

```text
Accounts.razor → AccountsApiClient ── GET /api/accounts ──► AccountEndpoints.GetAccountsAsync
   → GetAccountsHandler → IAccountRepository.GetAllAsync
   → AccountRepository (AsNoTracking) → PostgreSQL SELECT
   ◄── 200 OK + AccountResponse[]   ([] when there are no accounts)
```

**Mapping at each boundary:**

```text
Domain Account ↔ Application result/summary ↔ Contracts DTO ↔ JSON
```

EF Core entities and the DbContext are never exposed through the API.

## 11. Functional areas

Every area is a vertical slice through Domain, Application, Infrastructure,
Contracts, Api and App. The task documents hold the design and status.

| Area | Domain folder(s) | Tasks |
|---|---|---|
| Finance | `Finance/` (Accounts, Transactions, Categories, Budgets, Recurring, PlannedExpenses, Assistant) | FIN-001…006 |
| Gym | `Gym/` | GYM-001…004 |
| Nutrition and Hydration | `Nutrition/` (meals, targets, `Hydration/`, `Assistant/`) | NUT-001…003, HYD-001 |
| Habits | `Habits/` | HAB-001 |
| Reminders, notifications, automation | `Reminders/`, `Notifications/`, `Automation/` | REM-001, AUTO-001…003A |
| Journal | `Journal/` | JRN-001…003, AI-004…006.1 |
| Weekly Review | `WeeklyReviews/` | AUTO-002, AI-001 |
| Action Agent | `ActionAgent/` | AI-002, AI-003 |
| LifeOS Assistant | `Assistant/` plus each module's `Assistant/` folder | AI-007, AI-008 |
| Users | `Users/` | ADR-006 |

Each slice follows the same path, which new features should copy:

```text
Domain model → Application use case + minimal port → EF Core mapping/repository
  → migration (if schema changes) → Contracts → API endpoint module → client → tests
```

## 12. Testing and architecture enforcement

- **Unit tests** (`LifeOS.UnitTests`): Domain invariants, Application handlers
  (with `TimeProvider` fakes), API endpoint methods, App services and flows,
  .NET side of the Assistant contracts.
- **Architecture tests** (`LifeOS.ArchitectureTests`, NetArchTest): layer rules,
  plus feature rules such as the Assistant boundaries (no identifiers sent to
  providers, turn handlers not reachable from HTTP, module isolation).
- **Integration tests** (`LifeOS.IntegrationTests`): HTTP tests over the real
  host and PostgreSQL persistence tests on Testcontainers. They need Docker and
  a lot of memory; the full AI-008 run of 2026-10-27 was interrupted for
  insufficient memory and is not green.
- **Python** (`src/python/lifeos-ai/tests`, `tools/ai-evals`): offline pytest
  suites, contract tests shared with .NET (`docs/contracts/`), frozen
  evaluation datasets and gates.
- **Not covered:** automated UI tests of the MAUI client on a device; phone
  acceptance is manual and recorded per task.

## 13. AI architecture

`lifeos-ai` (`src/python/lifeos-ai`, ADR-011) is a stateless FastAPI service
called only by the API (Infrastructure clients behind Application ports), with
a shared service key. Capabilities and providers:

| Capability | Provider | Decision |
|---|---|---|
| Meal nutrition estimate (NUT-002) | Groq | ADR-011 |
| Weekly Review insights (AI-001) | Groq | ADR-011 |
| Action Agent steps (AI-002) | Groq | ADR-011 |
| Journal Memory embeddings and answers (AI-004, AI-006.1) | OpenAI | ADR-013 (Accepted) |
| LifeOS Assistant interpretation (AI-007, AI-008) | OpenAI, only by explicit configuration (default `disabled`) | ADR-014 (Proposed) |

Rules (`AGENTS.md`, ADR-003, ADR-004):

- **.NET remains the authoritative application/business layer.** Python
  interprets and proposes; it never accesses PostgreSQL.
- **Deterministic software computes; AI interprets.** Totals, balances, unit
  conversions and dates are computed by .NET.
- **Every write is confirmed.** AI output becomes a proposal or a draft;
  Application commands run only after validation and, for writes, the user's
  explicit confirmation.
- Providers are called over plain HTTPS adapters; provider SDKs and AI
  frameworks would be infrastructure only (ADR-004).

LifeOS Assistant flow (AI-007, AI-008):

```text
App chat ─► LifeOS.Api ─► AssistantTurnProcessor ─► lifeos-ai /v1/assistant/interpret
                                │                     (protocol v2, prompt assistant-interpret-v3;
                                │                      returns an interpretation only)
                                ▼
             AssistantCapabilityRouter ─► Finance | Hydration | Meals module
                                │           (Hydration and Meals: Assistant:Modules:* flags,
                                │            on in Development only)
                                ▼
                 draft in assistant_operations ─► user taps Confirm
                                ▼
       CreateTransactionHandler | AddHydrationIntakeHandler | CreateMealHandler
```

The AI-007 build used protocol v1 with prompt `assistant-finance-interpret-v2`.
Since AI-008 (`733ac2f`) the API sends protocol v2 only, so Finance is also
interpreted by `assistant-interpret-v3`, which is **not evaluated and not
released** (runbook Part I).

Never:

```text
LLM / Python ──► PostgreSQL
```

## 14. Architectural decisions (ADRs)

See the [ADR index](../adr/README.md) for every ADR and its status.
`AGENTS.md` is the canonical source for repository-wide engineering and
architecture rules. `CLAUDE.md` points to it.

## 15. Known gaps and current limitations

Re-checked on 2026-11-01 against the code:

- **Domain validation:** Domain still throws `ArgumentException` types; the
  endpoints translate them into 400 validation problems. A dedicated domain
  validation exception is deferred.
- **Error responses:** there is no global ProblemDetails / exception handler.
- **Launch profiles:** the `http` profile listens on port 5050 (the port the
  app expects); the `https` profile uses 7065/5091 and is not used for phone
  development.
- **Migrations:** not applied automatically in any environment.
- **Local sign-in setup** is not yet documented step by step in
  [local development](../development/local-development.md).
- **Production state** is recorded only per release, by hand (latest: runbook
  §I9); there is no automatic deployment log.

Resolved since the first version of this document: authentication (ADR-006),
transactions and derived balances, account update and delete, the template
pages and package id, the integration-test placeholder.

## 16. Notes for AI coding agents

- Read `AGENTS.md` first. It overrides tool-specific files.
- Respect the reference graph in §6. Never add a reference from Domain or
  Application to EF Core, ASP.NET Core, MAUI or AI frameworks, and never from
  `LifeOS.App` to Infrastructure or Domain.
- Add features as vertical slices following §11, with the **minimum** port
  surface a use case needs.
- Supply time via `TimeProvider`, use UUID v7 ids and store UTC.
- Keep enum-like values as strings in Contracts, and keep EF Core
  configuration explicit in Infrastructure.
- Implement only the agreed scope. Report other ideas as "Deferred improvements".
- Do not commit unless asked. Never commit secrets or real personal data.
