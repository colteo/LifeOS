# LifeOS Dependency Rules

Quick reference for which LifeOS projects may depend on which. For the
wider picture, see [overview.md](overview.md). The canonical rules are in
[`AGENTS.md`](../../AGENTS.md).

Core rule: **dependencies point inward.** Domain is the centre; Api and App
are outer adapters.

---

## 1. Dependency matrix

Read each row as "**row project** may depend on **column project**".

| From ↓ / To → | Domain | Application | Contracts | Infrastructure | Api | App |
|---|---|---|---|---|---|---|
| **Domain** | — | ⛔ | ⛔ | ⛔ | ⛔ | ⛔ |
| **Application** | ✅ | — | ⛔ | ⛔ | ⛔ | ⛔ |
| **Contracts** | ⛔ | ⛔ | — | ⛔ | ⛔ | ⛔ |
| **Infrastructure** | ✅ | ✅ | ⛔ | — | ⛔ | ⛔ |
| **Api** | ✅ ¹ | ✅ | ✅ | ✅ ² | — | ⛔ |
| **App** | ⛔ | ⛔ | ✅ | ⛔ | ⛔ ³ | — |

✅ Allowed · ⛔ Forbidden · — Not applicable

1. Api may use Domain types for mapping at the boundary. For example, it
   parses the `"type"` string into `AccountType`. It has no direct
   `ProjectReference` to Domain; Domain comes transitively through Application.
   Api must not call domain behaviour itself: that belongs to use cases.
2. Only as the **composition root**: it registers Infrastructure services with
   `AddInfrastructure(...)`. Feature endpoints must not use Infrastructure types.
3. App reaches Api **over HTTP only**, never by project reference.

## 2. Current internal project-reference graph

These are `<ProjectReference>` entries between LifeOS projects only. NuGet and
framework dependencies are not shown.

```text
                 LifeOS.Domain            LifeOS.Contracts
                   ▲      ▲                 ▲        ▲
                   │      │                 │        │
      LifeOS.Application  │                 │        │
            ▲       ▲     │                 │        │
            │       │     │                 │        │
            │   LifeOS.Infrastructure       │        │
            │       ▲                       │        │
            │       │                       │        │
            └── LifeOS.Api ─────────────────┘        │
                                                     │
                                  LifeOS.App ────────┘   (+ HTTP to LifeOS.Api)
```

| Project | References |
|---|---|
| LifeOS.Domain | none |
| LifeOS.Application | Domain |
| LifeOS.Contracts | none |
| LifeOS.Infrastructure | Application, Domain |
| LifeOS.Api | Application, Infrastructure, Contracts |
| LifeOS.App | Contracts |

## 3. Per-project rules

### LifeOS.Domain
- **Holds:** entities, value objects, business invariants (e.g. `Account.Create` validation).
- **May depend on:** nothing except the .NET base class library.
- **Forbidden:**
  - Application, Infrastructure, Contracts, Api, App;
  - EF Core and persistence frameworks;
  - ASP.NET Core;
  - MAUI;
  - AI frameworks and provider SDKs;
  - external provider SDKs.
- No EF Core attributes on domain types.

### LifeOS.Application
- **Holds:** use cases (e.g. `CreateAccountHandler`) and ports (e.g. `IAccountRepository`).
- **May depend on:** Domain, and BCL abstractions such as `TimeProvider`.
- **Forbidden:**
  - Infrastructure, Contracts, Api, App;
  - EF Core, Npgsql/PostgreSQL;
  - ASP.NET Core;
  - MAUI;
  - LangChain, LangGraph, model-provider SDKs, and other infrastructure SDKs.
- External capabilities (persistence, storage, AI) are expressed as **focused
  ports** that Infrastructure implements.

### LifeOS.Contracts
- **Holds:** external transport contracts only, such as HTTP request/response
  DTOs (`CreateAccountRequest`, `AccountResponse`).
- **May depend on:** nothing except the .NET base class library.
- **Forbidden:** Domain, Application, Infrastructure, Api, App, and any
  framework. Contracts must stay usable by any client.
- Enum-like values are exposed as **strings**, not as domain enums.

### LifeOS.Infrastructure
- **Holds:**
  - EF Core `LifeOSDbContext`, mappings, migrations;
  - repository implementations;
  - future external clients (object storage, Python AI service).
- **May depend on:** Application, Domain, EF Core/Npgsql and other infrastructure packages.
- **Implements** Application ports.
- **Forbidden:** Api, App, Contracts, and business rules that belong in Domain or Application.

### LifeOS.Api
- **Role:** outer adapter and **composition root**. It handles HTTP transport,
  DI wiring, endpoint modules, Contracts ↔ Application mapping, and turns
  expected failures into HTTP responses.
- **May reference:** Application, Infrastructure (for DI registration), Contracts.
- **Endpoints must** call Application use cases. They must not contain business logic.
- **Technical exception:** `GET /health/database` (mapped in Development only)
  injects `LifeOSDbContext` directly for a connectivity probe. Treat direct
  DbContext use as a **technical exception**, never as the pattern for feature
  endpoints.
- **Forbidden:** App, exposing EF Core entities or DbContext through HTTP
  contracts, and exposing stack traces or internal exception details.

### LifeOS.App (MAUI Blazor Hybrid)
- **Holds:** presentation, user interaction, device integrations, and API
  clients (e.g. `AccountsApiClient`).
- **May reference:** Contracts only.
- **Must** talk to the system **only through the HTTP API**.
- **Forbidden:**
  - Infrastructure, Application, Domain;
  - EF Core;
  - direct PostgreSQL access;
  - authoritative business logic. Client-side checks are convenience only;
    the API is authoritative.

## 4. How to decide where code belongs

| Code | Project | Why |
|---|---|---|
| Financial validation rule (e.g. "currency is a 3-letter code") | Domain | Business invariant |
| Balance calculation from transactions | Domain / Application | Deterministic business computation, never AI |
| `CreateTransaction` use case | Application | Orchestrates Domain and ports |
| `ITransactionRepository` interface | Application | Port required by a use case |
| Repository implementation, EF Core mapping, migration | Infrastructure | Persistence detail |
| `CreateTransactionRequest` / `TransactionResponse` DTOs | Contracts | External transport contract |
| `POST /api/transactions` endpoint, string → enum parsing, 400 mapping | Api | HTTP adapter |
| DI registration of handlers and `TimeProvider` | Api (`Program.cs`) | Composition root |
| DI registration of DbContext and repositories | Infrastructure (`AddInfrastructure`) | Infrastructure owns its wiring |
| Transaction form and list page | App | Presentation |
| `HttpClient` calls to `/api/transactions` | App (`Services/...ApiClient`) | Client-side API boundary |

Quick test:
- **Is it a business rule?** It goes in Domain.
- **Is it a step in a use case?** Application.
- **Does it touch a database, SDK or external service?** Infrastructure.
- **Is it the shape of an HTTP message?** Contracts.
- **Is it HTTP handling or wiring?** Api.
- **Is it UI?** App.

## 5. Python / AI components

Since NUT-002 one production Python service exists: `src/python/lifeos-ai`
(ADR-011). The evaluation lab `tools/ai-evals` is research tooling, not a runtime
dependency of anything.

- Python/AI services are **external, outer components**. They sit outside the
  .NET onion, like the App.
- They must **not** access or mutate PostgreSQL directly, and receive no LifeOS
  identifiers (user ids, meal ids, dates, tokens).
- Domain and Application must **not** depend on Python services, AI frameworks or
  HTTP. A use case that needs AI defines a focused port (e.g.
  `INutritionEstimationService`); Infrastructure implements it with an HTTP client
  of the Python service (`NutritionEstimationClient`).
- LangChain, LangGraph, provider SDKs and vector-store SDKs are
  infrastructure-level dependencies only; inside the Python service, provider
  adapters stay behind a small provider-neutral protocol.
- Today the API calls the Python service; agents calling the API through tools
  (below) remain the direction for later capabilities.

```text
Python AI service ──HTTP──► LifeOS.Api ──► Application ──► Domain
                                               │
                                               ▼
                              Infrastructure ──► PostgreSQL
Python AI service ──✗──► PostgreSQL            (forbidden)
```

## 6. Enforcement status

Architecture tests live in `tests/dotnet/LifeOS.ArchitectureTests`
(NetArchTest). They check type dependencies in the compiled assemblies.

| Rule | Enforced by |
|---|---|
| Domain ↛ Application | `Domain_Should_Not_Depend_On_Application` |
| Domain ↛ Infrastructure | `Domain_Should_Not_Depend_On_Infrastructure` |
| Domain ↛ EF Core | `Domain_Should_Not_Depend_On_EntityFramework` |
| Application ↛ Infrastructure | `Application_Should_Not_Depend_On_Infrastructure` |
| Application ↛ EF Core | `Application_Should_Not_Depend_On_EntityFramework` |
| Application ↛ ASP.NET Core | `Application_Should_Not_Depend_On_AspNetCore` |
| Domain/Application ↛ AI providers, AI frameworks, `System.Net.Http` | `Core_Should_Not_Depend_On_AiProviders_Frameworks_Or_Http` |
| The AI estimation port is implemented only in Infrastructure | `The_Estimation_Port_Is_Implemented_Only_In_Infrastructure` |

**Documented but not yet machine-enforced.** These rely on project references
and code review:

- Domain ↛ ASP.NET Core, MAUI, Contracts, Npgsql, AI frameworks/provider SDKs
- Application ↛ Contracts, MAUI, Npgsql, AI frameworks/provider SDKs
- Contracts ↛ Domain, Application, Infrastructure, or any framework
- Infrastructure ↛ Api, App, Contracts
- App ↛ Infrastructure, Application, Domain, EF Core. The App targets platform
  frameworks (`net10.0-android`, …), so the plain `net10.0` architecture-test
  project cannot load it today.
- Api endpoints call Application use cases and contain no business logic (code review)
- Direct DbContext use in Api limited to technical exceptions (code review)
- Python service rules are enforced by its own pytest suite (`src/python/lifeos-ai/tests/test_architecture.py`): no database driver, no evaluation-lab or framework/SDK imports, exact dependency list

Domain and Contracts currently have **no project references at all**, so most
of their forbidden dependencies are prevented by the build as it stands. They
would not be caught automatically if someone added a reference.
