# ADR-005: PostgreSQL as primary datastore

## Status

Accepted

## Context

LifeOS requires a relational data store for financial transactions,
training history, nutrition tracking, analytics, and future AI workloads.

The application must support both local development and a managed
production/personal environment while keeping the mobile client isolated
from direct database access.

## Decision

LifeOS uses PostgreSQL as its primary system of record.

Development environments use PostgreSQL running locally through Docker.

Production/personal environments use managed PostgreSQL.

The mobile application never connects directly to PostgreSQL.
All data access goes through the LifeOS ASP.NET Core API.

Binary assets such as meal photographs are not stored in PostgreSQL.
They are stored in object storage and referenced through metadata.

SQLite may later be introduced as an offline client cache,
but it will not become the system of record.

## Rationale

PostgreSQL provides:

- relational integrity
- transactions
- strong EF Core support
- analytical capabilities
- pgvector support for future AI/RAG workloads
- portability across hosting providers

## Consequences

Positive consequences:

- a single relational source of truth
- strong consistency for financial and tracking data
- compatibility with EF Core and Npgsql
- future support for vector search through pgvector
- easy portability between local Docker and managed PostgreSQL providers

Trade-offs:

- the mobile application requires API connectivity unless an offline cache
  is introduced later
- database migrations must be managed explicitly
- binary files require a separate object storage solution