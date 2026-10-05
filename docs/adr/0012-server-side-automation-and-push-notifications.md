# ADR-012: Server-side automation and push notifications

## Status

Accepted. Recorded with AUTO-001 step 1 (time zone foundation). Later AUTO-001
work packages implement the rest. The full design, with its closed product
decisions (PD-1 to PD-10), is
[AUTO-001](../tasks/automation/AUTO-001.md).

## Context

LifeOS only acts when the app sends a request. Upcoming features (weekly review,
reminders) need LifeOS to act on its own at a user's local time and to reach
the Android app. The production constraints in the runbook rule out the usual
answers. `lifeos-api` runs on one Render Free instance that sleeps when idle, so
an in-process timer is unreliable. Neon Free suspends when idle. There is no paid
scheduler, worker, broker or Redis, and the only external trigger is
cron-job.org.

## Decision

### External tick; LifeOS decides

- An external scheduler sends a parameterless, authenticated
  `POST /api/internal/automation/tick` (header `X-LifeOS-Automation-Key`, never
  `Authorization: Bearer`). The scheduler knows no users, types or times. LifeOS
  evaluates what is due by its own clock (`TimeProvider`).
- The tick is cadence-independent: correctness only requires that at least one
  tick lands in `[DueAtUtc, ExpiresAtUtc)`. The schedule is an operational
  choice, staged by need.
- The anonymous `GET /health/live` keepalive stays separate and unchanged (PD-8).
  The keepalive never carries a key and never touches PostgreSQL.

### State in PostgreSQL

- `automation_executions` uses one row per logical occurrence
  `(user_id, automation_type, occurrence_key)`, enforced UNIQUE. Claim = insert.
  Retries and lease takeovers are conditional updates. Completion is a fenced
  update in the same transaction as the artifact and the delivery rows (an
  in-database outbox). No broker, no in-memory scheduling cache.
- `notification_deliveries` uses one row per (logical notification, device)
  (PD-7). Each row has its own status, attempts and backoff.
- `device_registrations` has at most one Active row per app installation,
  owned by the user signed in on it.
  - *Clarified in AUTO-001 WP3A:* there is one row per (installation, user).
    When another user signs in on the installation, the previous owner's row
    becomes Inactive and keeps its delivery history.
  - The installation is not re-owned in place. That would break the composite
    delivery FK and move one user's history to another (ADR-006, PD-5).
- History is retained for 13 months (PD-5).

### Time zone follows the device

- `users.time_zone_id` holds the IANA id that the device reports through
  `PUT /api/me/time-zone` (PD-2). It is validated with BCL `TimeZoneInfo`
  (`HasIanaId`), so Windows ids and raw offsets are rejected. NULL means no
  local-time automation. There is no "home time zone", NodaTime is not used, and
  past executions keep the zone they were resolved with.
- Local due times are resolved by a pure Application helper
  (`LocalSchedule`):
  - a time in a spring-forward gap shifts forward by the gap length;
  - an ambiguous fall-back time uses its first occurrence;
  - occurrences are keyed by local date.

### Push through FCM

- Push uses Firebase Cloud Messaging HTTP v1 with `Google.Apis.Auth`, not the
  Firebase Admin SDK (PD-6). Firebase is added to the existing LifeOS Google Cloud
  project (PD-1).
- Both live only in Infrastructure, behind an Application port
  (`IPushNotificationSender`).
- Notification copy is fixed English text with no personal data (PD-3). The
  data payload carries only a type and an opaque id.

### Module boundary

- The automation and notification core knows nothing about Finance, Gym or
  Nutrition.
- Business handlers (`IAutomationHandler`) live in their modules, depend on the
  core, and are registered with plain DI.
- AUTO-001 registers no business handler and uses no AI (PD-9).

## Consequences

- Scheduled work survives Render sleep, restarts and duplicate or concurrent
  ticks. The price is ticks wake Neon, and the tick schedule must be managed by
  stage.
- The scheduler can be replaced by anything that can send an HTTP POST with a
  header.
- Delivery to one device can be retried without notifying the user's other
  devices again.
- Two devices of one user in different zones mean the last one to report wins
  (AUTO-001 open question 5).

## Alternatives considered

See AUTO-001 §19. Rejected options include:

- an in-process `BackgroundService`;
- Hangfire or Quartz;
- pg_cron;
- a paid scheduler;
- replacing the keepalive with the tick;
- one delivery per logical notification;
- the Firebase Admin SDK;
- NodaTime;
- a "home time zone" setting.
