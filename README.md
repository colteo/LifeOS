# LifeOS

**Personal Intelligence Platform built with .NET and Python.**

LifeOS is a personal application for tracking and understanding areas of
everyday life, starting with personal finance. It is also a senior-level
software engineering portfolio project, built with deliberate architecture,
tests and documentation. Applied AI capabilities will be introduced
progressively, only where they solve concrete problems.

## Current status

LifeOS is at an early stage. The first end-to-end vertical slice (Finance →
Accounts) works from the Android app through the API to PostgreSQL.

**Implemented**

- .NET 10 solution structured with Onion Architecture
- PostgreSQL persistence through EF Core, with migrations
- Finance **Accounts** domain model with validated invariants
- `POST /api/accounts` (create account) and `GET /api/accounts` (list accounts)
- Android app (.NET MAUI Blazor Hybrid) with an Accounts page to list and create accounts
- End-to-end flow verified manually on a physical Android device
- Unit tests and architecture tests

**Planned (not implemented yet)**

- Finance transactions, derived balances and statistics
- Training and nutrition tracking
- Device capabilities such as camera and notifications
- Python AI service
- Agents and AI-assisted workflows

## Architecture

```text
MAUI Blazor Hybrid (Android)
        │  HTTP
        ▼
ASP.NET Core API
        │
        ▼
   Application
        │
        ▼
     Domain
        ▲
        │  implements Application ports
 Infrastructure (EF Core)
        │
        ▼
   PostgreSQL
```

- **.NET is the authoritative application/business layer.** All business rules
  and all writes go through it.
- **PostgreSQL is the authoritative persistent datastore.**
- **Python** will be introduced later as the AI/intelligence layer. AI
  components will act through the API and will **not** access PostgreSQL directly.

More detail:
- [Architecture overview](docs/architecture/overview.md)
- [Dependency rules](docs/architecture/dependency-rules.md)

## Tech stack

**Current:**
- .NET 10
- ASP.NET Core (Minimal APIs)
- .NET MAUI Blazor Hybrid
- EF Core with Npgsql
- PostgreSQL 18
- Docker
- xUnit
- NetArchTest

**Planned:**
- Python
- FastAPI
- AI/LLM tooling where justified

## Repository structure

```text
src/dotnet/
├── LifeOS.Domain           business model and invariants
├── LifeOS.Application      use cases and ports
├── LifeOS.Contracts        HTTP request/response contracts
├── LifeOS.Infrastructure   EF Core, PostgreSQL, migrations
├── LifeOS.Api              ASP.NET Core API (composition root)
└── LifeOS.App              .NET MAUI Blazor Hybrid client (Android)
tests/dotnet/               unit and architecture tests
docs/                       architecture, ADRs and development guides
```

## Run locally

Windows, PowerShell. In short:

1. Copy `.env.example` to `.env` and set the API connection string in .NET User Secrets.
2. Start PostgreSQL: `docker compose up -d postgres`.
3. Apply migrations with the local `dotnet-ef` tool.
4. Start the API: `dotnet run --project src/dotnet/LifeOS.Api/LifeOS.Api.csproj --launch-profile http` (port 5050).
5. Run the Android app on an emulator or a physical device.

**→ Full step-by-step guide: [Local development](docs/development/local-development.md)**

Also see:
- [Android setup](docs/development/android-setup.md): SDK, JDK, emulator
- [Physical device debugging](docs/development/physical-device-debugging.md): USB deployment, `adb reverse`, APKs, logcat

## Testing

- **Unit tests:** Domain invariants, Application use cases and API endpoint behaviour.
- **Architecture tests:** automated checks of layer dependency rules (NetArchTest).
- **Manual end-to-end verification:** on a physical Android device (Finance → Accounts against the local API and PostgreSQL).

HTTP-level and PostgreSQL integration tests are not implemented yet.

```powershell
dotnet test tests/dotnet/LifeOS.UnitTests/LifeOS.UnitTests.csproj
dotnet test tests/dotnet/LifeOS.ArchitectureTests/LifeOS.ArchitectureTests.csproj
```

## Architectural decisions

Significant decisions are recorded as ADRs in [`docs/adr/`](docs/adr/):

- Onion Architecture
- .NET as the authoritative application layer
- Python as the AI intelligence layer
- AI frameworks as infrastructure dependencies
- PostgreSQL as the primary datastore

ADR-0004 and ADR-0005 are written. ADRs 0001–0003 are still being completed;
until then, the corresponding rules are documented in [`AGENTS.md`](AGENTS.md)
and the [architecture overview](docs/architecture/overview.md).

## Roadmap

High-level direction, not in a fixed order and without dates:

- Finance transactions: income, expense, transfer
- Derived balances and finance analytics
- Training
- Nutrition
- Device capabilities (camera, notifications)
- Python AI service
- Personal AI agent
- Vision-assisted meal logging

## Documentation

- [Architecture overview](docs/architecture/overview.md)
- [Dependency rules](docs/architecture/dependency-rules.md)
- [Local development](docs/development/local-development.md)
- [Android setup](docs/development/android-setup.md)
- [Physical device debugging](docs/development/physical-device-debugging.md)
- [Architecture decision records](docs/adr/)
- [Engineering guidelines for contributors and AI agents](AGENTS.md)
