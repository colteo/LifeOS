# LifeOS Engineering Guidelines

## Goal

LifeOS is a Personal Intelligence Platform built primarily with
.NET and Python.

The project is both:
- a real personal application
- an Applied AI Engineering portfolio project

## Architecture

LifeOS follows Onion Architecture.

Dependencies MUST point inward.

### Domain

LifeOS.Domain contains:

- entities
- aggregates
- value objects
- domain services
- domain events
- business rules

Domain MUST NOT depend on:

- Application
- Infrastructure
- Entity Framework
- ASP.NET Core
- Azure SDKs
- AI frameworks
- external providers

### Application

LifeOS.Application contains:

- use cases
- commands
- queries
- application services
- interfaces/ports

Application may depend only on Domain.

External integrations must be represented as interfaces.

### Infrastructure

LifeOS.Infrastructure contains concrete implementations for:

- persistence
- external APIs
- notifications
- storage
- AI services
- observability

Infrastructure implements Application ports.

### API

LifeOS.Api is an external adapter.

Business logic MUST NOT live in controllers or endpoints.

## AI Architecture

.NET is the system of record.

Python is the intelligence layer.

Python MUST NOT directly access the LifeOS application database.

AI operations must use application APIs/tools.

LangChain and LangGraph are infrastructure dependencies.

The Domain and Application layers MUST NOT depend on AI frameworks.

## AI Principles

Deterministic software computes.
AI interprets, extracts and orchestrates.

LLMs must not calculate authoritative financial or nutritional totals
when deterministic services can calculate them.

Read operations may execute automatically.

Write operations may require confirmation.

Destructive operations require explicit confirmation.

## SOLID

Apply SOLID principles.

Prefer:

- small cohesive classes
- explicit interfaces
- dependency inversion
- composition
- testable components

Avoid:

- god services
- framework leakage
- speculative abstractions
- unnecessary generic repositories
- unnecessary microservices

## Development workflow

For every feature:

1. understand the existing architecture
2. create a plan
3. implement the smallest vertical slice
4. add unit tests
5. add integration tests when necessary
6. run architecture tests
7. run the full test suite

Do not introduce a major framework or architecture change without an ADR.

## Important

Do not modify architecture boundaries just to make an implementation easier.

If a requested feature conflicts with existing architecture,
stop and explain the trade-off before implementing it.