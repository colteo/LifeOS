# ADR-008: Workout execution snapshots

## Status

Accepted

## Context

GYM-001 lets the user author workout programs: programs contain ordered
workouts (templates), blocks (Single or Superset), exercise prescriptions and
set prescriptions. All of it is mutable: the trainer changes reps, rest and
exercises over time, and workouts or whole programs are deleted.

GYM-002 records what the user actually did when executing a workout: reps,
weight and timestamps per set. That record becomes training history, the input
of later history, performance and analytics slices (GYM-003+).

History must answer "what was prescribed, and what was done, on that day". If
an execution only referenced the template rows, editing Bench Press from 3 × 8
to 4 × 10 would rewrite past workouts, and deleting a program would delete
(or orphan) its history.

## Decision

### An execution owns a snapshot of the prescription

Starting a workout creates a `WorkoutSession` aggregate that **copies** the
workout prescription as it is at that moment:

```text
workout_sessions          user, provenance, names, started/completed, status
└── workout_session_blocks     position, kind, rest_seconds
    └── workout_session_exercises  position (A/B), exercise_id, notes
        └── workout_session_sets       position, target reps, actual reps, weight, completed_at
```

- Execution never reads template rows after the start.
- The workout and program **names** are copied as well.
- Exercises are **referenced by their stable id**, not copied: Exercise is the
  long-lived identity that history and analytics attach to. The reference is
  `ON DELETE RESTRICT`, so an exercise with history cannot disappear.

### Authoring entities never delete execution history

- `workout_program_id` and `workout_template_id` are provenance only
  ("started from"), nullable, with `ON DELETE SET NULL`.
- No foreign key from execution to authoring cascades. Deleting a workout or a
  program clears the provenance and leaves the session intact.
- Snapshot rows cascade only from their own session (discarding an unfinished
  workout).

### Ownership

As in the rest of LifeOS: every session belongs to one user; sessions, blocks
and exercises carry `user_id`, and parent references are composite
`(…_id, user_id)` foreign keys, including the exercise reference.

### Lifecycle

`InProgress` → `Completed`. Completed executions are immutable (Domain and API
refuse changes). At most one `InProgress` session per user, enforced by a
partial unique index. Execution timestamps come from the server clock.

## Consequences

- Past workouts are stable records regardless of later authoring changes.
- Storage grows with every workout (one row per prescribed set). This is small
  for a personal application and keeps history queries simple: they read only
  execution tables.
- Exercise names shown in history are current names (the Exercise is the
  identity). If exercise renaming is introduced, decide then whether history
  should show the name at execution time.
- Exercises with execution history cannot be deleted; an exercise deletion
  feature, if ever needed, must archive instead.
- Later slices (history, performance) build on `workout_session_*` tables and
  `exercise_id`, never on authoring tables.
