# ADR-001: Onion Architecture

## Status

Accepted

## Context

LifeOS is intended to span several areas of personal life:

- personal finance
- training
- nutrition
- device capabilities (for example camera and notifications)
- future AI capabilities

These areas share one application and one datastore, and they will evolve
over a long period. Frameworks, persistence technology, client platforms and
AI tooling are all likely to change during that time. Business rules such as
financial and nutritional invariants must remain correct and testable
regardless of those changes.

LifeOS therefore needs strong boundaries between business rules and
infrastructure or framework details.

## Decision

LifeOS uses Onion Architecture within a single .NET solution. Dependencies
point inward.

- **Domain** (`LifeOS.Domain`) is the innermost layer. It contains entities,
  value objects and business invariants, and references no other LifeOS project.
- **Application** (`LifeOS.Application`) contains use cases and the ports
  (interfaces) they need. It depends only on Domain.
- **Infrastructure** (`LifeOS.Infrastructure`) implements Application ports,
  for example EF Core persistence for PostgreSQL. It depends on Application and Domain.
- **API** (`LifeOS.Api`) and **App** (`LifeOS.App`, .NET MAUI Blazor Hybrid)
  are outer adapters.
  - The API is also the composition root.
  - The App communicates with the system only through the HTTP API.
- **Contracts** (`LifeOS.Contracts`) hold external transport contracts shared
  by the API and clients. They depend on no other LifeOS project.
- Framework, database and provider concerns must not leak into Domain or
  Application. This covers EF Core, ASP.NET Core, MAUI, provider SDKs and AI frameworks.

This is a layering decision inside one deployable application. It does not
imply microservices.

Part of these rules is enforced automatically by architecture tests in
`tests/dotnet/LifeOS.ArchitectureTests`. The remaining rules are enforced
through project references and code review. The full rule set and its
enforcement status are documented in
[`docs/architecture/dependency-rules.md`](../architecture/dependency-rules.md).

## Rationale

- **Framework independence:** business rules do not depend on web, UI or
  persistence frameworks.
- **Testability:** Domain and Application can be unit tested without a
  database, HTTP host or device.
- **Replaceable infrastructure:** persistence and external providers sit
  behind Application ports and can be changed without touching the core.
- **Clear ownership of business rules:** invariants have one obvious home
  (Domain), and use cases have another (Application).
- **Future AI integration:** AI capabilities can be added as outer components
  or infrastructure implementations without coupling the core to AI frameworks
  (see ADR-003 and ADR-004).

## Consequences

Positive consequences:

- clear, reviewable boundaries between business logic and technical detail
- fast, isolated unit tests for Domain and Application
- persistence and providers can be replaced behind ports
- visible architectural discipline, enforced in part by automated tests

Trade-offs:

- more projects and more mapping code than a simple single-project application
- some duplication between Domain types, Application results and Contracts
  DTOs is accepted in exchange for independent layers
- boundaries depend on continued discipline, code review and architecture
  tests; not every rule is machine-enforced yet
