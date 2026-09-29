# LifeOS Architecture Overview

> **Scope of this document.** It describes the architecture of LifeOS **as it
> exists in the repository today**, plus decisions that are explicitly recorded
> in `AGENTS.md` or in the ADRs under `docs/adr/`. Anything not yet implemented
> is labelled **Planned** or **Future**. If this document and the code disagree,
> the code is the current truth and this document should be corrected.

---

## 1. What LifeOS is

LifeOS is a **Personal Intelligence Platform**: a personal application for
tracking and understanding areas of everyday life (finance first; nutrition,
fitness and others later), built primarily with **.NET** and, in future,
**Python** for AI capabilities.

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

| Area | Status |
|---|---|
| Onion-architecture .NET solution | **Implemented** |
| Finance → Accounts: create and list | **Implemented** (Domain, Application, EF Core, API, Android UI) |
| PostgreSQL persistence with EF Core migrations | **Implemented** |
| Android client (MAUI Blazor Hybrid) calling the API | **Implemented** (development configuration only) |
| Architecture tests (NetArchTest) | **Implemented** (partial coverage, see §12) |
| Transactions, balances, other Finance features | Planned |
| Other life areas (nutrition, fitness, …) | Future |
| Python AI / intelligence service | Planned: no Python code exists yet |
| Authentication / authorization | Not implemented |
| Production deployment, managed PostgreSQL | Planned (ADR-005) |
| Object storage for binary assets | Planned (ADR-005) |
| Offline SQLite cache on the device | Future, optional (ADR-005) |

## 4. Technology stack

**In use today**

| Concern | Technology |
|---|---|
| Runtime / language | .NET 10, C# (nullable enabled) |
| HTTP API | ASP.NET Core Minimal APIs, OpenAPI (development only) |
| Persistence | EF Core 10 with Npgsql (`Npgsql.EntityFrameworkCore.PostgreSQL`) |
| Database | PostgreSQL 18, run locally through Docker Compose |
| Migrations tooling | `dotnet-ef` as a local tool (`dotnet-tools.json`) |
| Client | .NET MAUI Blazor Hybrid (`BlazorWebView`), Bootstrap CSS from the template |
| Primary client platform | Android (the project also targets iOS, macCatalyst and Windows) |
| Tests | xUnit, NetArchTest.Rules |
| Local secrets | .NET User Secrets (API connection string), `.env` for Docker Compose |

**Planned** (from `AGENTS.md` and the ADRs)

- Python as the AI/intelligence layer, with FastAPI when the AI service is introduced.
- LangChain / LangGraph / model-provider SDKs as *infrastructure* dependencies only (ADR-004).
- Managed PostgreSQL for the personal/production environment (ADR-005).
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
│   ├── adr/                   architecture decision records
│   ├── architecture/          this document
│   └── development/           environment setup (Android)
├── src/dotnet/
│   ├── LifeOS.slnx            solution
│   ├── LifeOS.Domain/         business model and rules
│   ├── LifeOS.Application/    use cases and ports
│   ├── LifeOS.Contracts/      HTTP request/response DTOs
│   ├── LifeOS.Infrastructure/ EF Core, PostgreSQL, port implementations
│   ├── LifeOS.Api/            ASP.NET Core host (composition root)
│   └── LifeOS.App/            .NET MAUI Blazor Hybrid client
└── tests/dotnet/
    ├── LifeOS.UnitTests/          Domain, Application and endpoint tests
    ├── LifeOS.ArchitectureTests/  dependency-rule tests
    └── LifeOS.IntegrationTests/   placeholder only (no real tests yet)
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
technical `/health/database` check (see §7, LifeOS.Api).

`LifeOS.Contracts` is deliberately independent of Domain, so the external HTTP
contract can evolve separately from the internal model (for example,
`AccountType` is a C# enum internally but a **string** in the API).

## 7. Project responsibilities

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
- Migrations live in `Persistence/Migrations` (currently `CreateAccounts`).
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
  is defined inline in `Program.cs` and injects `LifeOSDbContext` directly to
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
- Development uses PostgreSQL 18 in Docker (`docker-compose.yml`). The planned
  personal/production environment is managed PostgreSQL.
- Only `LifeOS.Infrastructure` touches the database, through EF Core.
- Schema changes go through **EF Core migrations**
  (`dotnet ef migrations add ... --project src/dotnet/LifeOS.Infrastructure --startup-project src/dotnet/LifeOS.Api`).
- Timestamps are stored in UTC (`timestamptz`), and ids are UUID v7 generated by the domain.
- **Planned:** binary assets such as meal photographs go to object storage and
  are referenced by metadata. They are not stored in PostgreSQL.
- **Future:** SQLite may be added as an offline cache on the device, but it
  must never become the authoritative datastore.

Current schema:

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
- A production API URL does not exist yet (**planned**).

The API is started for these scenarios with:

```powershell
dotnet run --project src/dotnet/LifeOS.Api --launch-profile http --urls http://localhost:5050
```

## 10. Request and data flow

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

## 11. Finance: the first implemented area

Finance is the first functional area of LifeOS and the template for future
vertical slices. Only **Accounts** exists today.

| Capability | Endpoint | Status |
|---|---|---|
| Create account | `POST /api/accounts` → 201 / 400 | Implemented |
| List accounts | `GET /api/accounts` → 200 | Implemented |
| Android Accounts page | `/finance/accounts` | Implemented |
| Transactions | | Planned |
| Balances | | Planned: will be **derived from transactions by deterministic code**, not stored on `Account` and not computed by AI |
| Get by id, edit, delete, pagination, sorting | | Not implemented |

Each slice follows the same path, which new features should copy:

```text
Domain model → Application use case + minimal port → EF Core mapping/repository
  → migration (if schema changes) → Contracts → API endpoint module → client → tests
```

## 12. Testing and architecture enforcement

- **Unit tests** (`LifeOS.UnitTests`) cover:
  - Domain invariants;
  - Application handlers, using a hand-written `FixedTimeProvider` and an in-memory repository fake;
  - API endpoint methods, called directly without an HTTP host.
- **Architecture tests** (`LifeOS.ArchitectureTests`, NetArchTest) currently enforce:
  - Domain must not depend on Application, Infrastructure or EF Core;
  - Application must not depend on Infrastructure, EF Core or ASP.NET Core.
- **Not yet covered:**
  - HTTP-level integration tests;
  - PostgreSQL persistence tests (`LifeOS.IntegrationTests` is a placeholder);
  - automated tests for the MAUI client.

  These are planned for when several use cases can share the infrastructure.

## 13. AI architecture (planned)

No AI or Python code exists yet. The following are **recorded decisions** for
when it is introduced (`AGENTS.md`, ADR-003 title, ADR-004).

- **.NET remains the authoritative application/business layer.** Python is the
  intelligence layer: it provides probabilistic capabilities such as
  interpretation, extraction and orchestration.
- **Python and AI agents must not access or mutate the LifeOS database
  directly.** Every AI action goes through explicit tools that call the
  ASP.NET Core API, and so passes through Application use cases and Domain rules.
- AI frameworks (LangChain, LangGraph, provider SDKs, vector stores) are
  **infrastructure dependencies** and must never leak into Domain or
  Application (ADR-004).
- **Deterministic software computes; AI interprets.** Financial totals,
  balances and nutrition totals come from deterministic code, never from an LLM.
- Agent capability policy: **read** operations may run automatically, **write**
  operations may require confirmation, and **destructive** operations require
  explicit confirmation.

Intended flow (**planned**):

```text
User input (text, image, ...)
      │
      ▼
Python AI service (planned, e.g. FastAPI)
  LLM / agent interprets → structured proposal
      │  tool call over HTTP
      ▼
LifeOS.Api  →  Application  →  Domain  →  Infrastructure  →  PostgreSQL
      ▲
      └── validation and, when required, human confirmation happen here
```

Never:

```text
LLM / Python ──► PostgreSQL
```

## 14. Architectural decisions (ADRs)

| ADR | Decision | Recorded content |
|---|---|---|
| 0001 | Use Onion Architecture | Title only (file is empty); rules are in `AGENTS.md` |
| 0002 | .NET is the system of record | Title only (file is empty) |
| 0003 | Python is the AI intelligence layer | Title only (file is empty) |
| 0004 | AI frameworks are infrastructure | Short decision and rationale |
| 0005 | PostgreSQL as primary datastore | Full ADR (context, decision, consequences) |

`AGENTS.md` is the canonical source for repository-wide engineering and
architecture rules. `CLAUDE.md` points to it.

## 15. Known gaps and current limitations

These are known, intentionally deferred items, not hidden defects:

- **Domain validation:** it uses `ArgumentException`. The API returns those
  messages as-is, including .NET's `(Parameter '...')` suffix. A dedicated
  domain validation exception is deferred.
- **Error responses:** there is no global ProblemDetails / exception handler.
  Unexpected errors in non-development environments return an empty 500.
- **`Location` header:** `POST /api/accounts` returns `/api/accounts/{id}`,
  but no GET-by-id endpoint exists yet.
- **List order:** `GET /api/accounts` has no defined ordering.
- **Template leftovers:**
  - the API still has the template `/weatherforecast` endpoint;
  - the app still has the template Counter and Weather pages, the default app title and `com.companyname.lifeos.app`.
- **API port:** the API launch profile uses port 5091, while the client's
  development configuration expects 5050 (passed with `--urls`).
- **Account types in the client:** the list of account type options is
  duplicated in the client UI. The backend remains authoritative.
- **No authentication or authorization yet.**

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
