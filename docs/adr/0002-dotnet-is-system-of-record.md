# ADR-002: .NET as the authoritative application layer

## Status

Accepted

## Context

LifeOS will eventually have several consumers of its data and behaviour:

- the .NET MAUI client (Android first)
- possibly other clients
- a planned Python AI service with agents (see ADR-003)

If each consumer applied business rules or wrote data independently, rules
would be duplicated and could diverge. AI components could also bypass
validation, and state could change in inconsistent ways.

Terminology used in this ADR and the architecture documentation:

- **.NET is the authoritative application/business layer.**
- **PostgreSQL is the authoritative persistent datastore** (see ADR-005).

The file name of this ADR keeps the historical term "system of record". The
decision below is specifically about business authority.

## Decision

- Authoritative business rules live in .NET, in `LifeOS.Domain` and
  `LifeOS.Application`.
- Every authoritative write passes through .NET Application use cases and
  Domain logic, exposed by `LifeOS.Api`.
- PostgreSQL stores the authoritative persistent state. Only .NET
  Infrastructure accesses it on behalf of those use cases.
- The MAUI App, the Python AI service and future agents must not bypass .NET
  to read or mutate business state directly.
- External clients and AI components interact with LifeOS through explicit
  APIs or tools backed by Application use cases.

This prevents:

- business rules being duplicated across clients and services
- AI components bypassing validation
- inconsistent or partial state mutations
- clients becoming coupled to the database schema

## Rationale

- Deterministic business logic stays centralised in one place.
- Integrity of financial and personal data is protected by a single
  validation path.
- API boundaries are explicit and reviewable.
- Auditing and testing are easier when all changes flow through known use cases.
- Python and AI functionality can evolve independently without taking
  ownership of business rules.

## Consequences

Positive consequences:

- one authoritative implementation of each business rule
- consistent validation for every client, including AI agents
- the database schema can evolve behind the API without breaking clients

Trade-offs:

- Python AI services must use HTTP/tool calls even where direct database
  access would be simpler.
- An API endpoint and Application use case must exist for every operation a
  client or agent needs.
- Clients cannot use PostgreSQL directly, not even for reads.
