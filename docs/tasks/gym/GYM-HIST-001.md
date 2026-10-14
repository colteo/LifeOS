# GYM-HIST-001 — Explicit historical workout import (analysis)

Status: PROPOSED — not scheduled, not approved. Raised by [QA-001](../qa/QA-001.md).

## Problem

QA-001 provides 24 synthetic historical workouts across three program phases. They cannot be imported:
ADR-008 has the server clock assign workout session instants, and no public API records a past session.
They remain offline-only fixtures. Real users have the same gap when they want to bring in a training
history from before LifeOS.

## Question to answer

Is an explicit, user-initiated "record a past workout" capability worth adding, and how would it coexist
with ADR-008's execution snapshots?

## Constraints

- ADR-008 must not be silently changed. Any change goes through a new ADR that supersedes or amends it
  explicitly.
- Sessions must stay user-owned, authenticated and created through Application use cases. No privileged
  bulk endpoint and no direct database writes.
- Historical sessions must be distinguishable from live ones (for example by source or provenance), so
  analytics and "previous performance" stay honest.

## Options to analyse

1. Status quo: historical workouts stay offline fixtures.
2. A dedicated "log past workout" use case with explicit start/end instants in the past, validated
   (no future, no overlap with existing sessions) and marked as `Recorded` rather than `Executed`.
3. An import format for completed sessions only (no live state machine), through the same use case.

## Out of scope

Implementation, migrations, App UI and any change to ADR-008. This document only frames the decision.
