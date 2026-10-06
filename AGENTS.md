# LifeOS Agent Guidelines

## Project Purpose

LifeOS is a Personal Intelligence Platform built primarily with .NET and Python.

The project has three simultaneous goals:

1. Be genuinely useful as a personal application.
2. Strengthen Applied AI Engineering skills.
3. Serve as a senior-level technical portfolio project.

Implementation decisions should preserve all three goals.

---

## Source of Truth

Before making architectural or cross-cutting changes, read the relevant documentation under:

- `docs/architecture/`
- `docs/adr/`
- `docs/development/`

`AGENTS.md` defines repository-wide agent behavior.

Architecture decisions belong in ADRs and architecture documentation, not in tool-specific instruction files.

Do not silently override documented architectural decisions.

---

## Technology Direction

Current primary stack:

- .NET 10
- ASP.NET Core
- .NET MAUI Blazor Hybrid
- EF Core
- PostgreSQL
- Python
- FastAPI when the AI service is introduced

Architecture direction:

- .NET is the authoritative application and system of record.
- PostgreSQL is the primary datastore.
- Python is the intelligence layer.
- Android is the initial client platform.
- AI capabilities are introduced progressively when they solve concrete problems.

---

# Architecture

LifeOS follows Onion Architecture.

Dependencies must point inward.

Conceptually:

```text
Domain
  ↑
Application
  ↑
Infrastructure

API and App are outer adapters.
```

## Domain

`LifeOS.Domain` contains framework-independent business concepts such as:

- entities
- aggregates
- value objects
- domain services
- domain events
- business rules

Domain MUST NOT depend on:

- Application
- Infrastructure
- EF Core
- ASP.NET Core
- MAUI
- external provider SDKs
- AI frameworks
- persistence frameworks

Business invariants belong here.

---

## Application

`LifeOS.Application` contains:

- use cases
- application services
- commands and queries when useful
- ports/interfaces required by use cases

Application may depend on Domain.

Infrastructure capabilities must be represented through focused abstractions when required.

Examples:

- repositories
- storage
- notifications
- AI capabilities
- external services

Application MUST NOT directly depend on:

- EF Core
- PostgreSQL
- ASP.NET Core
- MAUI
- LangChain
- LangGraph
- model-provider SDKs
- infrastructure SDKs

Prefer existing .NET abstractions such as `TimeProvider` when they already solve the problem.

---

## Infrastructure

`LifeOS.Infrastructure` contains concrete implementations of Application ports.

Examples:

- EF Core repositories
- PostgreSQL persistence
- external API clients
- object storage
- notification providers
- Python AI-service clients

Infrastructure may depend on Application and Domain.

Infrastructure must not contain business rules that belong in Domain or Application.

---

## API

`LifeOS.Api` is an outer adapter.

Responsibilities include:

- HTTP transport
- dependency-injection composition
- endpoint registration
- mapping HTTP input/output
- translating expected application/domain failures to HTTP responses

Business logic must not live in endpoints.

Keep `Program.cs` small.

Prefer feature-oriented endpoint modules when the API grows.

---

## Mobile App

`LifeOS.App` is the .NET MAUI Blazor Hybrid client.

Responsibilities include:

- presentation
- user interaction
- device integrations
- camera
- notifications
- communicating with the LifeOS API

The mobile app MUST NOT:

- connect directly to PostgreSQL
- reference Infrastructure
- contain authoritative business rules
- bypass the API for persistence

---

# Data Architecture

PostgreSQL is the primary system of record.

Development uses PostgreSQL locally through Docker.

Production/personal environments use managed PostgreSQL.

Binary assets such as meal photographs must not be stored directly in PostgreSQL unless a later ADR explicitly changes this decision.

Store binary assets in object storage and reference them through metadata.

SQLite may later be introduced as an offline/mobile cache, but must not become the authoritative system of record.

EF Core belongs only in Infrastructure.

Do not add EF Core attributes to Domain entities.

Prefer explicit EF Core configurations.

---

# AI Architecture

.NET remains the authoritative application layer.

Python provides probabilistic and AI capabilities.

Python and AI agents MUST NOT directly mutate the LifeOS database.

AI actions must pass through explicit tools/application APIs.

Preferred flow:

```text
LLM / Agent
    ↓
Tool
    ↓
ASP.NET Core API
    ↓
Application
    ↓
Domain
    ↓
Persistence
```

Never:

```text
LLM
 ↓
Database
```

AI frameworks such as LangChain, LangGraph, provider SDKs, vector-store SDKs and similar libraries are infrastructure dependencies.

They must not define the application architecture.

Introduce an AI framework only when it solves a concrete requirement.

---

## Deterministic vs AI Work

Use deterministic software for deterministic work.

Examples:

- financial totals → deterministic code
- balances → deterministic code
- calorie totals from confirmed foods → deterministic code
- detecting food from an image → AI
- interpreting natural-language input → AI
- choosing which tool to invoke → AI

General principle:

> Deterministic software computes. AI interprets and orchestrates.

---

# Agent Safety

Agent capabilities must be explicit.

General policy:

```text
READ
→ may execute automatically

WRITE
→ may require confirmation depending on impact

DESTRUCTIVE
→ requires explicit confirmation

HIGH-RISK
→ dedicated policy
```

Prefer:

```text
Unstructured input
    ↓
AI interpretation
    ↓
Structured proposal
    ↓
Validation
    ↓
Human confirmation when required
    ↓
Domain/Application command
```

---

# Engineering Principles

Apply SOLID pragmatically.

Prefer:

- small cohesive components
- explicit dependencies
- focused interfaces
- dependency inversion
- framework-independent business logic
- meaningful vertical slices
- simple code that satisfies current requirements

Avoid:

- speculative abstractions
- generic repositories unless justified
- unnecessary base classes
- god services
- unnecessary microservices
- unnecessary event buses
- unnecessary CQRS frameworks
- unnecessary MediatR
- unnecessary AutoMapper
- unnecessary FluentValidation
- premature distributed architecture

Do not introduce a framework simply to demonstrate familiarity with it.

---

# Vertical Slice Strategy

Prefer meaningful end-to-end features over tiny implementation tasks.

Example:

```text
Create Account
    ↓
Application use case
    ↓
Repository port
    ↓
EF Core implementation
    ↓
API endpoint
    ↓
Client
    ↓
Tests
```

Keep each slice narrowly scoped to one user-visible capability.

---

# Scope Discipline

Implement only the agreed scope.

Do not add adjacent functionality "while you're here".

Do not perform unrelated refactors.

Do not introduce optional abstractions preemptively.

If useful improvements are discovered but are not required for the task, report them under:

`Deferred improvements`

Do not implement them automatically.

---

# Review Severity

Classify review findings into three levels.

## Blocking

Must be fixed before commit.

Examples:

- build failure
- failing required tests
- clear architecture violation
- incorrect business behavior
- broken persistence
- data-corruption risk
- security issue
- incorrect API behavior
- implementation outside agreed scope

## Important but non-blocking

Report under `Deferred improvements`.

Examples:

- worthwhile refactoring
- stronger abstractions not yet required
- broader integration testing
- additional validation not required by the current feature
- known technical debt without immediate functional impact

Do not implement automatically.

## Cosmetic / marginal

Do not block progress.

Examples:

- stylistic preferences
- slightly cleaner tests
- naming alternatives
- harmless duplication
- formatting preferences

Do not create iteration loops over cosmetic issues.

---

# Development Workflow

Before editing:

1. inspect the repository
2. read `AGENTS.md`
3. read relevant ADRs/documentation
4. inspect existing patterns
5. understand the requested scope
6. produce a concise implementation plan when appropriate
7. list expected files to create or modify

If explicit approval is requested, wait before implementation.

After approval:

1. implement only the requested scope
2. preserve architecture boundaries
3. add relevant tests
4. build affected projects
5. run relevant tests
6. run architecture tests when applicable
7. report migrations/tooling implications when relevant
8. summarize changed files

Do not commit unless explicitly requested.

---

# Testing Strategy

Use the smallest testing level that gives useful confidence.

Prioritize:

- Domain unit tests
- Application tests
- Architecture tests

Add HTTP or persistence integration infrastructure when multiple use cases can benefit from it.

Do not introduce heavy testing infrastructure for one simple feature unless the risk justifies it.

Do not delete or weaken tests simply to make a build pass.

## Validation cadence

Do **not** use the complete test suites as the development inner loop.

During implementation:

1. Run only tests directly relevant to the changed feature/module and its immediate dependencies.
2. Prefer `dotnet test --filter FullyQualifiedName~...` using existing namespaces/test classes.
3. Use `--no-restore` and `--no-build` when valid after the first successful restore/build.
4. Run Architecture tests when layer boundaries, DI registrations, endpoint composition, or persistence registrations change.
5. Run the Android build only when App/Android changes have reached a stable state.
6. For EF/persistence changes, run the new/affected PostgreSQL integration tests plus model/schema checks during development.

Before pushing a completed implementation, run **one complete regression gate**:

- full Unit suite;
- full Integration suite;
- full Architecture suite;
- Android Debug build when the task touches the App or shared code used by it;
- `git diff --check`;
- `dotnet ef migrations has-pending-model-changes` when EF model/migrations are involved.

If the final full regression gate finds failures:

1. diagnose and fix;
2. rerun only the failing/relevant tests until green;
3. run one final complete regression gate;
4. push only when that final gate is green.

Do not repeatedly run the full suites after every fix. Full suites are a pre-push regression gate, not the normal implementation loop.

Architecture boundaries should be machine-enforced where practical.

Important rules include:

- Domain must not depend on Application
- Domain must not depend on Infrastructure
- Application must not depend on Infrastructure
- Domain/Application must not depend on EF Core
- Domain/Application must not depend on AI frameworks

---

# API Rules

API contracts must be separate from persistence models.

Do not expose EF Core entities directly.

Prefer readable external contracts.

Use explicit string representations for enum-like values when this improves API clarity.

Translate expected validation failures into appropriate HTTP responses.

Do not expose:

- stack traces
- internal exception details
- secrets

---

# Time and IDs

Persist timestamps in UTC.

Prefer `DateTimeOffset` for timestamps representing real instants.

Creation timestamps should normally be supplied by the Application layer rather than implicitly generated inside Domain entities.

Use `TimeProvider` when time needs to be testable.

Do not introduce custom clock abstractions when existing .NET abstractions are sufficient.

For GUID-based entities, prefer UUID/GUID v7 where appropriate.

Do not introduce custom ID abstraction layers without a concrete need.

---

# Security and Personal Data

Never commit:

- passwords
- API keys
- tokens
- certificates
- production connection strings
- real personal data

Use:

- .NET User Secrets
- environment variables
- ignored `.env` files
- `.env.example` for documented configuration

LifeOS may contain sensitive information such as:

- financial transactions
- account balances
- nutrition history
- fitness/body data
- meal photographs

Use synthetic data in:

- source control
- tests
- public fixtures
- documentation examples

Always respect `.gitignore`.

---

# ADR Rules

Document significant architectural decisions as ADRs.

Typical ADR candidates:

- major frameworks
- database/storage technologies
- deployment architecture
- significant cross-cutting abstractions
- irreversible or expensive-to-change decisions

Do not create ADRs for minor implementation details.

---

# Agent Behavior

When working in this repository:

- do not widen task scope
- do not refactor unrelated code
- do not add packages unnecessarily
- do not modify architecture boundaries for convenience
- do not introduce speculative patterns
- do not silently change ADR decisions
- do not commit automatically
- do not add secrets
- do not remove tests to make builds pass

If a requested implementation conflicts with documented architecture:

1. stop
2. explain the conflict
3. propose the smallest compliant alternative

---

# Completion Report

After implementation report:

## Implemented

What changed.

## Tests

What was built/tested and the results.

## Blocking issues

Only issues that genuinely prevent completion.

## Deferred improvements

Useful non-blocking improvements that were deliberately not implemented.

---

# Definition of Done

A task is done when:

- requested behavior is implemented
- architecture boundaries are respected
- affected projects build
- relevant tests pass
- no blocking issue remains
- changed files are summarized
- optional improvements remain deferred unless explicitly requested

Do not keep iterating over non-blocking cosmetic improvements once the task satisfies this Definition of Done.