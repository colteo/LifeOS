# AUTO-001 — Scheduling and notifications foundation

Status: DESIGN — proposed, not implemented. No code, migration, API, package,
Firebase, Render or cron-job.org change is part of this document.

Enables AUTO-002 (Weekly Review) and AUTO-003 (reminders). Builds on ADR-001
(onion), ADR-002 (.NET is the system of record), ADR-005 (PostgreSQL), ADR-006
(user-owned data, explicit `UserId`), ADR-011 (service-key pattern, Render
hosting) and the [production runbook](../../operations/production-runbook.md)
(Render Free, Neon Free, cron-job.org keepalive).

---

## 1. Problem

LifeOS has no server-side notion of time-driven work. Everything happens because
a user opened the app (Nutrition lazy close is the closest thing to automation,
and it runs only on a request). Upcoming features need LifeOS to act on its own:

- a **weekly review** every Sunday evening, in the user's local time;
- **reminders** (Finance, Nutrition, later hydration and a Todo module);
- future scheduled actions in modules that do not exist yet.

Production constraints make the usual answers unavailable:

- `lifeos-api` is a single Render Free instance that **sleeps after 15 idle
  minutes**; an in-process timer does not fire while it sleeps.
- No paid scheduler, no Render Cron Job, no VPS, no Redis, no broker.
- The only external trigger available is **cron-job.org**, which today only
  pings `/health/live`.
- The current Nutrition convention (`utcOffsetMinutes` per request) gives a
  correct local date for a request but cannot answer "when is next Sunday 20:00
  for this user?" across DST changes.
- There is no push channel to the Android app at all.

## 2. Goals / non-goals

### Goals

1. A periodic, authenticated, parameterless **tick** lets LifeOS decide what is due.
2. A durable **user time zone** (IANA id) and pure, tested local-time resolution
   with defined DST behaviour.
3. **Idempotent** execution: one logical occurrence runs at most once to
   success, enforced by the database.
4. Persisted **execution history/status** with bounded retries.
5. **Android push** via FCM, multiple devices per user, token lifecycle.
6. **Notification delivery** that can be retried without re-running the automation.
7. **Module boundaries**: the foundation never knows Finance/Gym/Nutrition rules.
8. Works for **many users** in bounded batches; nothing hard-coded to one user or
   to Europe/Rome.
9. A scheduler that can be **replaced** (cron-job.org → anything that can send an
   HTTP POST) without touching automation logic.

### Non-goals

- Implementing the weekly review, any reminder, or any automation handler.
- AI integration of any kind (AUTO-001 has none).
- A workflow engine, rules engine, JSON schedule DSL, or plugin framework.
- Hangfire, Quartz, Celery, Redis, a broker, a worker process, pg_cron.
- iOS, web push, email, SMS.
- Per-kind notification preferences, quiet hours, rate limiting, unsubscribe UI
  (identified in §16/§17 as future strengthening).
- Changing the Nutrition `utcOffsetMinutes` convention (it stays correct for
  request-local dates).

## 3. First use case: Sunday 20:00 weekly review (AUTO-002, not implemented here)

What AUTO-001 must make easy:

```text
Sunday 20:00 in the user's IANA time zone
  → tick sees the occurrence (user, WeeklyReview, week ending 2026-10-04) is due
  → WeeklyReviewAutomationHandler builds deterministic WeeklyReviewData
       (Finance + Gym + Nutrition summaries via their Application use cases)
  → optional AI interpretation (AUTO-002 decision; never required for success)
  → persist WeeklyReview (user-facing artifact)
  → mark execution Succeeded + enqueue one NotificationDelivery   (same transaction)
  → a later step of the same or next tick sends FCM:
       title "LifeOS", body "Your weekly review is ready",
       data { type: "weekly_review", id: "<WeeklyReview id>" }
  → user taps → app (authenticated) opens /reviews/weekly/<id>
```

The push contains no amounts, balances, calories, workouts or meal text.

## 4. Proposed architecture

```text
cron-job.org (or any HTTP scheduler)
   │  POST /api/internal/automation/tick
   │  X-LifeOS-Automation-Key: <secret>          (no body, no query string)
   ▼
LifeOS.Api  ── AutomationTickEndpoints (transport + auth only)
   ▼
LifeOS.Application/Automation
   RunAutomationTick (use case; bounded by time + item budget)
     1. NotificationDispatch: send due NotificationDeliveries
     2. Retry: re-claim FailedRetryable / stale Running executions
     3. Discover: for each registered IAutomationHandler → FindDue → claim → Execute
   │                        │
   │                        └─► IAutomationHandler implementations live in their
   │                            module (e.g. Application/WeeklyReview/...), call that
   │                            module's use cases. AUTO-001 ships ZERO handlers.
   ▼
Ports (Application)                       Implementations (Infrastructure)
   IAutomationExecutionStore        ──►    EF Core / PostgreSQL (claims via conditional
   INotificationDeliveryStore       ──►      UPDATE / INSERT … ON CONFLICT / SKIP LOCKED)
   IDeviceRegistrationRepository    ──►    EF Core
   IPushNotificationSender          ──►    FcmPushNotificationSender (HTTP v1)
   TimeProvider (BCL)
```

Principle: **the external scheduler triggers; LifeOS decides.** The tick carries
no user, no automation type, no time. A tick at any moment, any number of times,
only causes LifeOS to do work that is due by LifeOS's own clock.

## 5. Data model proposal

Four schema changes. Names follow existing snake_case conventions; ids are UUID v7
generated in Domain/Application; timestamps are `timestamptz` UTC.

### 5.1 `users.time_zone_id` (new column)

```text
users
+ time_zone_id   varchar(64) NULL    IANA id, e.g. "Europe/Rome"; NULL = unknown
```

NULL means LifeOS does not know the user's zone: **no local-time automation is
scheduled for that user** (never guessed, never defaulted to Europe/Rome).
Index: `ix_users_time_zone_id` (supports `SELECT DISTINCT time_zone_id`).

A column on `User` (not a preferences table) because the zone is a property of
the person used by every module, exactly like `default_currency`.

### 5.2 `automation_executions`

One row per logical occurrence of one automation for one user.

```text
automation_executions
- id                    uuid          PK
- user_id               uuid          → users(id) ON DELETE CASCADE
- automation_type       varchar(64)   stable code, e.g. "WeeklyReview"
- occurrence_key        varchar(64)   handler-defined, e.g. "2026-10-04" (local week-ending date)
- time_zone_id          varchar(64)   zone used to resolve this occurrence (audit)
- scheduled_for_utc     timestamptz   resolved due instant
- expires_at_utc        timestamptz   no attempt starts after this (lateness bound)
- status                varchar(16)   Running | Succeeded | FailedRetryable | FailedFinal
- attempt_count         int           ≥ 1; also the fencing token for completion
- lease_expires_at_utc  timestamptz   NULL unless Running
- next_attempt_at_utc   timestamptz   NULL unless FailedRetryable
- last_failure_code     varchar(64)   NULL; stable code, never an exception message
- result_id             uuid          NULL; opaque id of the artifact produced (no FK)
- created_at_utc        timestamptz
- started_at_utc        timestamptz   start of the latest attempt
- completed_at_utc      timestamptz   NULL until Succeeded / FailedFinal

ux_automation_executions_occurrence  UNIQUE (user_id, automation_type, occurrence_key)
ix_automation_executions_retry       (next_attempt_at_utc) WHERE status = 'FailedRetryable'
ix_automation_executions_stale       (lease_expires_at_utc) WHERE status = 'Running'
ck_automation_executions_status      CHECK on status values
```

No "Pending" row is ever written: a row is created at the moment it is claimed
(§8), so "due but not started" is simply "no row yet".

### 5.3 `notification_deliveries`

One row per **logical** notification to a user (not per device).

```text
notification_deliveries
- id                    uuid          PK; also used as the Android notification tag
- user_id               uuid          → users(id) ON DELETE CASCADE
- kind                  varchar(32)   "WeeklyReviewReady" | "Test" | …  (maps to a fixed template in code)
- dedupe_key            varchar(128)  e.g. "automation:<execution id>"
- target_type           varchar(32)   NULL | "weekly_review" …  (deep-link target)
- target_id             uuid          NULL; stable LifeOS id opened on tap
- source_execution_id   uuid          NULL → automation_executions(id) ON DELETE SET NULL
- status                varchar(16)   Pending | Sending | Sent | Skipped | Failed
- attempt_count         int
- next_attempt_at_utc   timestamptz   when Pending: earliest send time
- lease_expires_at_utc  timestamptz   NULL unless Sending
- expires_at_utc        timestamptz   stale notifications are never sent
- last_failure_code     varchar(64)   NULL
- created_at_utc        timestamptz
- sent_at_utc           timestamptz   NULL

ux_notification_deliveries_dedupe   UNIQUE (user_id, dedupe_key)
ix_notification_deliveries_due      (next_attempt_at_utc) WHERE status = 'Pending'
```

Title/body text is **not stored**: `kind` selects a fixed, non-personal template
in code. The table holds no report content.

### 5.4 `device_registrations`

```text
device_registrations
- id                    uuid          PK
- user_id               uuid          → users(id) ON DELETE CASCADE
- installation_id       varchar(64)   random id generated by the app per install
- platform              varchar(16)   Android  (iOS later)
- push_provider         varchar(16)   Fcm      (Apns/other later)
- push_token            varchar(4096) sensitive operational identifier
- notifications_enabled boolean       OS permission granted and user did not opt out
- created_at_utc        timestamptz
- updated_at_utc        timestamptz
- last_seen_at_utc      timestamptz   last upsert from the app
- disabled_reason       varchar(32)   NULL | PermissionDenied | TokenInvalid | SignedOut

ux_device_registrations_installation  UNIQUE (installation_id)
ux_device_registrations_token         UNIQUE (push_provider, push_token)
ix_device_registrations_user          (user_id) WHERE notifications_enabled
```

### 5.5 Not created by AUTO-001

- No generic `automation_preferences` table (§14 decision B).
- No `automation_schedules` / `next_due_at` table (scaling path, §16).
- No per-device delivery attempt table (deferred, §11 trade-off).
- No `weekly_reviews` table (AUTO-002).

## 6. Timezone model

### Storage and source

- `users.time_zone_id` holds an **IANA** id. Fixed offsets are never stored as the
  long-term representation.
- The Android app reports `TimeZoneInfo.Local.Id` (IANA on Android) through
  `PUT /api/me/time-zone { "timeZoneId": "Europe/Rome" }` after sign-in and at app
  start **when it differs from the last value it sent** (cached on device).
- The server validates with `TimeZoneInfo.TryFindSystemTimeZoneById` and requires
  `HasIanaId` (rejects Windows ids and raw offsets like `+02:00`), stores the
  canonical id. Invalid → `400`.
- The API container must contain tzdata. The Debian-based `aspnet:10.0` image is
  expected to (**VERIFY AT IMPLEMENTATION**); when automation is enabled, startup
  resolves a known id (`Europe/Rome`) and refuses to start if it cannot.
- BCL `TimeZoneInfo` is sufficient; NodaTime is not added.

### Resolving a local due time

A pure Application helper (part of the foundation, unit-tested, no I/O):

```text
LocalSchedule.ResolveWeekly(zone, nowUtc, DayOfWeek day, TimeOnly time, TimeSpan maxLateness)
  → Occurrence { OccurrenceDate (local), DueAtUtc, ExpiresAtUtc } | none
```

1. `localNow = ConvertTime(nowUtc, zone)`.
2. Candidate local date = most recent `day` ≤ `localNow.Date` (may be today).
3. `dueLocal = date + time`; convert to UTC with DST rules below.
4. Due iff `DueAtUtc ≤ nowUtc < DueAtUtc + maxLateness`.

A `ResolveDaily` sibling arrives when AUTO-003 needs it — not before.

### DST rules (apply to every local-time automation)

| Situation | Rule | Example (Europe/Rome) |
|---|---|---|
| Local time does not exist (spring-forward gap) | run at the same wall-clock time shifted forward by the gap length | 2026-03-29 02:30 → 03:30 CEST = 01:30Z |
| Local time occurs twice (fall-back) | first occurrence (the earlier instant) | 2026-10-25 02:30 → 02:30 CEST = 00:30Z |
| Normal | plain conversion | 2026-10-04 20:00 CEST = 18:00Z |

Sunday 20:00 is never in a gap or overlap in any current zone, but the rule is
defined once so daily reminders inherit it. The **occurrence key is the local
date**, so even an ambiguous time can produce only one occurrence.

Both Europe/Rome DST changes in 2026 fall on Sundays (29 March, 25 October): the
"week" then has 167 or 169 hours. AUTO-002 must aggregate by **local dates**, never
by `nowUtc - 7 days`.

### Time zone changes

- The new zone applies from the next tick. The key `(user, WeeklyReview,
  local Sunday date)` prevents a second run when moving west makes "Sunday 20:00"
  happen again in absolute time.
- Moving east can make the occurrence's due instant already lie in the past: if
  still inside the lateness window it runs on the next tick, otherwise that
  week's occurrence is skipped (no row, no review). Acceptable and documented.
- Executions record `time_zone_id` used, so history is explainable.
- Travel semantics (follow device vs. a fixed "home zone") is an open question
  (§21); the recommendation is follow-device for v1.

### Delayed tick

Lateness is per automation: the tick checks `due ≤ now < due + maxLateness`, so a
tick at 20:03, 20:47 or (after downtime) 07:00 next morning all execute the same
occurrence once, as long as it has not expired.

## 7. Tick/scheduling algorithm

### Cadence

**Every 10 minutes**, the same cadence as today's keepalive.

- Expected delay after the due instant: 0–10 minutes (plus cold start). For a
  weekly review and for evening reminders this is invisible to the user.
- 5 minutes doubles requests and database wake-ups for no user-visible gain;
  1 minute would be wasteful and pressure Neon compute (§18).
- Nothing depends on exact seconds; correctness depends only on
  `due ≤ now < expires`.

### Production window (current hosting)

Phase 1: the tick **replaces** the `lifeos-api` keepalive with the same schedule,
`*/10 7-22 * * *` Europe/Rome, so Render instance-hours do not change. Because the
weekly review has a 24 h lateness window, every zone on Earth is still covered:
e.g. Sunday 20:00 in Los Angeles = 05:00 Monday in Rome → executed at 07:00 Rome =
22:00 Sunday in Los Angeles. A 24/7 or overnight-hourly tick is a deliberate later
decision (§18).

### One tick

```text
POST /api/internal/automation/tick
  authenticate (§13) → 401 on any failure
  if previous tick started < 60 s ago → 200 { "skipped": true }      (cheap abuse guard)
  nowUtc = TimeProvider.GetUtcNow()
  deadline = nowUtc + TickBudget (default 20 s)

  Phase A — deliveries:  claim up to 50 Pending deliveries with next_attempt_at ≤ now
                          (or Sending with expired lease), oldest first; send; record.
  Phase B — retries:     claim FailedRetryable with next_attempt_at ≤ now and stale
                          Running with lease_expires ≤ now (oldest first, cap 25); execute.
  Phase C — discovery:   for each handler (round-robin start rotated per tick):
                            candidates = handler.FindDue(nowUtc, limit = remaining cap)
                            for each candidate: claim (INSERT … ON CONFLICT DO NOTHING)
                                                 → winner executes → complete
                         stop claiming when deadline or item cap is reached.

  200 { "deliveries": n, "executions": m, "more": true|false }   (counts only)
```

- Every phase stops **claiming** new work at `deadline`; work already claimed
  finishes (or its lease expires and a later tick reclaims it).
- Processing is not linked to `HttpContext.RequestAborted`: a scheduler that gives
  up after its own timeout does not abort a half-done item. Atomic completion
  (§8) makes an abort harmless anyway.
- `more: true` means backlog; the next tick continues. No loop inside a tick waits
  for anything.

### How a handler finds due users without loading everyone

For fixed-time automations (weekly review), use **zone buckets**:

```text
zones   = SELECT DISTINCT time_zone_id FROM users WHERE time_zone_id IS NOT NULL   (≤ ~400 rows)
open    = zones where LocalSchedule.ResolveWeekly(zone, now, Sunday, 20:00, 24h) is due
          → set of (zone, occurrence_key, due_utc, expires_utc)
due     = SELECT u.id FROM users u
          WHERE u.time_zone_id = ANY(@openZones)
            AND <module enabled condition>
            AND NOT EXISTS (SELECT 1 FROM automation_executions e
                            WHERE e.user_id = u.id AND e.automation_type = 'WeeklyReview'
                              AND e.occurrence_key = <key for u's zone>)
          ORDER BY u.id LIMIT @limit                                (keyset, ids only)
```

Time-zone arithmetic happens in .NET over a few hundred zones; PostgreSQL only
filters by zone id. Outside the window, `open` is empty and no user query runs.
When per-user times are introduced (AUTO-003), the module materializes
`next_due_at_utc` on its own settings row instead (§16).

## 8. Idempotency/concurrency

### Logical occurrence identity

`(user_id, automation_type, occurrence_key)` — unique in PostgreSQL. The handler
defines the key; it must be derived from **local calendar data**, never from the
tick instant:

| Automation | occurrence_key |
|---|---|
| WeeklyReview | local Sunday (week-ending) date, `2026-10-04` |
| Daily reminder (future) | local date, `2026-10-05` |
| Due-date Todo (future) | `<todo id>:<due local date-time>` |

### Claim = insert

```sql
INSERT INTO automation_executions (..., status, attempt_count, lease_expires_at_utc)
VALUES (..., 'Running', 1, now + lease)
ON CONFLICT (user_id, automation_type, occurrence_key) DO NOTHING
RETURNING id;
```

Committed immediately (own short transaction). One row returned = this tick owns
the attempt; none = someone else did or does.

### Retry claim = conditional update

```sql
UPDATE automation_executions
SET status = 'Running', attempt_count = attempt_count + 1,
    lease_expires_at_utc = @now + @lease, started_at_utc = @now, next_attempt_at_utc = NULL
WHERE id = @id AND now < expires_at_utc
  AND ((status = 'FailedRetryable' AND next_attempt_at_utc <= @now)
    OR (status = 'Running' AND lease_expires_at_utc <= @now))
RETURNING attempt_count;
```

Batch selection uses `FOR UPDATE SKIP LOCKED` so two ticks never block each other.

### Atomic completion with fencing

The handler computes outside any transaction (reads only), then one transaction:

1. write the artifact (e.g. `weekly_reviews`, which has its own unique key);
2. `UPDATE automation_executions SET status='Succeeded', completed_at_utc=…, result_id=…
    WHERE id=@id AND status='Running' AND attempt_count=@myAttempt` — must affect 1 row;
3. insert the `notification_deliveries` row (`ON CONFLICT (user_id, dedupe_key) DO NOTHING`).

If step 2 affects 0 rows (the lease expired and another tick took over), the
transaction rolls back: no duplicate artifact, no duplicate notification. This
is a transactional outbox inside one database — no broker needed.

### Two simultaneous ticks

Both compute the same candidates; both try the INSERT; the unique index lets
exactly one win; the loser skips. Retries and deliveries are claimed with
`SKIP LOCKED` / conditional updates, so each row is processed by one tick. The
60-second in-memory guard (§7) just avoids wasted work on the single instance;
correctness never depends on it (it would not hold with several instances).

### Lease

`lease = 5 minutes` (≫ tick budget, ≪ retry delay). A Running row whose lease
expired is treated as a crashed attempt.

## 9. Execution state machine

Four states — the smallest model that covers crash, retry and give-up.

```text
           claim (INSERT)
   (no row) ──────────────► Running ──── success (atomic completion) ───► Succeeded
                              │   ▲
          handler failure,    │   │  retry claim (next_attempt_at ≤ now, attempt < max,
          attempts < max,     │   │               now < expires)
          now < expires       ▼   │
                         FailedRetryable
                              │
   attempts == max or        │      Running with expired lease (crash) ── retry claim ──► Running
   now ≥ expires or          ▼                                         └─ expired ──────► FailedFinal
   permanent failure ──► FailedFinal
```

- **Running**: claimed, lease held.
- **Succeeded**: artifact persisted, delivery enqueued. Terminal. Notification
  outcome is *not* part of this state.
- **FailedRetryable**: `next_attempt_at_utc` set; a later tick picks it up.
- **FailedFinal**: terminal; `last_failure_code` says why
  (`MaxAttemptsReached`, `Expired`, `Permanent:<code>`).

Handlers return a result, not exceptions, for expected failures:
`Succeeded(resultId, notification?)`, `RetryableFailure(code)`,
`PermanentFailure(code)`, `NotApplicable` (e.g. user disabled the feature since
discovery → recorded as Succeeded with no artifact and no notification, so it is
not rediscovered). Unexpected exceptions are caught by the dispatcher and treated
as retryable with code `Unhandled` (details only in sanitized logs).

## 10. Failure/retry policy

Defaults (per handler overridable, all bounded):

| | Executions | Deliveries |
|---|---|---|
| Max attempts | 3 | 5 |
| Delay after attempt 1 / 2 / 3 / 4 | 10 min / 30 min / — | 10 min / 30 min / 1 h / 3 h |
| Absolute bound | `expires_at_utc` (WeeklyReview: due + 24 h) | `expires_at_utc` (WeeklyReviewReady: created + 24 h) |

Retries are executed **by later ticks**, never by sleeping inside a request.

| Scenario | Behaviour |
|---|---|
| Scheduler request fails (network, 5xx, timeout) | Nothing claimed or partially claimed; next tick does the work. cron-job.org failures are only a monitoring signal. |
| Render asleep when tick fires | The request wakes it. Cold start (~1 min) may exceed the scheduler's timeout; the request may or may not be processed. Either way the **next tick 10 min later** finds the service awake and processes all non-expired work. |
| API restarts during execution | Row stays Running; lease expires; a later tick reclaims it (attempt +1). Nothing was committed because completion is one transaction. |
| Module summary fails (exception/DB error) | `FailedRetryable` → retried up to 3 times within 24 h → `FailedFinal`. No artifact, no notification. |
| Optional AI interpretation fails (AUTO-002) | Not a failure of the automation: the deterministic report is saved without commentary; execution Succeeded; push sent. |
| Push send fails transiently (FCM 5xx/429/timeout) | Execution stays Succeeded. Delivery back to `Pending` with backoff; report is **never regenerated**. |
| Push token invalid (`UNREGISTERED`, invalid token) | That device registration disabled (`TokenInvalid`); other devices still tried; if no device left → delivery `Skipped`. |
| DB write succeeds, notification fails | Exactly the outbox case: delivery row exists, retried independently. |
| FCM accepted but crash before `Sent` is recorded | Lease expiry → resend. Duplicate is collapsed on the phone: the Android notification **tag** = delivery id replaces the first one. Effective at-least-once with no visible duplicate. |
| Same tick called again / two ticks at once | Unique index + conditional claims (§8). |
| Render wakes late (overnight) | Lateness window: due work runs at the first tick before `expires_at_utc`; after that, `Expired`. |
| User deleted | CASCADE removes executions, deliveries, devices. |

## 11. Push notification architecture

### Provider

**Firebase Cloud Messaging (HTTP v1 API)** for Android:

- the standard and only practical push channel for Play-services Android;
- free (Spark plan, no payment method — keeps runbook stop condition S13);
- extends to iOS later (FCM relays to APNs) without changing the server model.

### Flow

```text
Android app (MAUI)
  ├─ obtains FCM registration token (Firebase Messaging binding)
  ├─ requests POST_NOTIFICATIONS (Android 13+)
  └─ PUT /api/devices/{installationId}  (authenticated)  ──► device_registrations
                                                                     │
tick Phase A ──► NotificationDeliveryService (Application)          │
                   load enabled devices for user ◄──────────────────┘
                   build message from kind template (no personal data)
                   IPushNotificationSender.SendAsync(token, message) per device
                        └─► FcmPushNotificationSender (Infrastructure)
                              OAuth2 access token from service account
                              POST https://fcm.googleapis.com/v1/projects/<id>/messages:send
```

### Execution vs. separate delivery record — decision: **separate table**

| | Columns on `automation_executions` | Separate `notification_deliveries` (chosen) |
|---|---|---|
| Tables | 1 | 2 |
| Retry push without re-running automation | possible, but mixes two state machines in one row | natural: own status, attempts, lease |
| Notifications not caused by an automation (test push, future Todo due, account events) | impossible | supported (`source_execution_id` NULL) |
| Execution state meaning | "ran" and "notified" tangled | "artifact exists" only |
| Outbox in one transaction | yes | yes |

The second table is small and removes a real future dead end (any non-automation
notification). It is **one row per logical notification, not per device**: fan-out
results are applied immediately (invalid tokens disabled), and the delivery is
`Sent` when at least one device accepted it. Per-device attempt rows are deferred
until there is a concrete need (e.g. iOS + Android with different failure modes,
or delivery analytics).

### Fan-out rule

For each enabled device: send. Outcome per device: Accepted / TokenInvalid
(disable registration) / Transient. Then:

- ≥ 1 Accepted → `Sent` (transient failures on other devices are **not** retried,
  to avoid re-notifying devices that already got it);
- 0 Accepted, ≥ 1 Transient → `Pending` with backoff;
- no enabled device (or all invalid) → `Skipped` (`NoEnabledDevices`). The
  artifact is still available in-app.

### Message shape

```json
{
  "message": {
    "token": "<device token>",
    "notification": { "title": "LifeOS", "body": "Your weekly review is ready" },
    "data": { "type": "weekly_review", "id": "0192f0c3-…", "delivery": "0192f0c4-…" },
    "android": {
      "priority": "normal",
      "notification": { "tag": "<delivery id>", "channel_id": "lifeos_general" }
    }
  }
}
```

- `notification` + `data` message: when the app is in background the system shows
  it and passes `data` to the launched activity; in foreground the app's
  messaging service shows it as a local notification with the same tag.
- `data` contains only a type and opaque ids. The app navigates after normal
  authentication; if the session is gone, sign-in first, then navigate; if the id
  belongs to another user / does not exist, the normal 404 page.
- Templates are fixed strings per `kind`. Localization is an open question (§21).

## 12. FCM device lifecycle

| Event | App | Server |
|---|---|---|
| First launch after sign-in | create `installationId` (random UUID, app Preferences), get FCM token, ask permission | `PUT /api/devices/{installationId}` upsert: `user_id` from access token, token, platform, `notifications_enabled` |
| Every app start (signed in) | re-send registration (cheap) | upsert, `last_seen_at_utc = now` |
| Token rotation (`OnNewToken`) | re-send registration | update token on that installation |
| Permission denied / revoked | send `notificationsEnabled: false` | keep row, `disabled_reason = PermissionDenied`; never sent to |
| Permission later granted | send `true` | re-enable |
| Same token arrives for another installation/user (shared device, reinstall, user switch) | — | in one transaction: delete any other row with that `(push_provider, push_token)`, then upsert. A token always belongs to exactly one user |
| Another user signs in on the same install | same `installationId`, new access token | upsert moves the row to the new user |
| Sign-out | best-effort `DELETE /api/devices/{installationId}` **before** revoking the session; delete the local FCM token (`deleteToken`) | delete row |
| Sign-out while offline | — | row survives; the next sign-in on that device re-registers (row moves), or FCM later reports `UNREGISTERED` |
| Reinstall | new `installationId`, new token | new row; old row is disabled on first `UNREGISTERED` |
| Invalid/expired token from FCM | — | `notifications_enabled = false`, `disabled_reason = TokenInvalid` |
| Device inactive for a long time | — | deferred cleanup: disable rows with `last_seen_at_utc` > 60 days (FCM treats tokens inactive ~270 days as expired) |
| Multiple devices | each its own row | all enabled rows receive the notification |
| Future iOS | `platform = iOS`, provider still `Fcm` (or `Apns` directly later) | no schema change |

Endpoints (authenticated with the normal LifeOS access token; `UserId` never from
the payload, per ADR-006):

```text
PUT    /api/devices/{installationId}   { platform, pushProvider, pushToken, notificationsEnabled }
DELETE /api/devices/{installationId}
POST   /api/devices/{installationId}/test-notification     (AUTO-001 acceptance; rate-limited 1/min per user)
PUT    /api/me/time-zone               { timeZoneId }
```

Operations on another user's `installationId` are reported as 404 (ownership
rule of ADR-006), except the upsert, which re-owns the installation — that is
the intended "another user signed in on this device" behaviour.

## 13. Internal tick authentication

**Recommendation: a dedicated header `X-LifeOS-Automation-Key: <secret>`.**

Why not `Authorization: Bearer` (used for `lifeos-ai`)? In `lifeos-api`,
`Authorization: Bearer` already means *a LifeOS user access token (JWT)* and the
JWT handler runs on every request. Reusing the header would have the JWT handler
parse the scheduler secret on every tick and blur two different identities. A
separate header keeps the user-auth pipeline untouched (no `Authorization`
header → JWT yields "no result") and makes it impossible to confuse a user token
with the scheduler credential.

Rules:

- Secret: ≥ 32 random bytes, Base64 (e.g. `[Convert]::ToBase64String(RandomNumberGenerator 32 bytes)`),
  stored only in Render env `Automation__TickKey` and in the cron-job.org job's
  header configuration. Never in the repository, a URL, a query string or logs.
- Comparison: `CryptographicOperations.FixedTimeEquals` over the UTF-8 bytes
  (hash both sides with SHA-256 first so lengths are equal).
- Implemented as a dedicated authentication scheme/policy on the one endpoint
  (not `AllowAnonymous` + ad-hoc check), consistent with ADR-006 "endpoint by
  endpoint". The policy requires this scheme only: a user JWT can never satisfy it.
- Any failure: `401` with an empty body, logged as a warning **without** the
  presented value.
- Not configured (`Automation__TickKey` absent): the endpoint is **not mapped**
  (404) and automation is disabled — the rest of LifeOS works (same pattern as
  `NutritionAi:BaseUrl`). Configured but shorter than 32 visible characters:
  startup failure.
- No body accepted (any body → 400), no parameters: a leaked key lets an attacker
  only make LifeOS process work that is **already due**, and the 60 s guard caps
  the cost. Response contains counts only (cron-job.org stores response history).
- The Android app never knows the key; the route lives under `/api/internal/`,
  which the app never calls.
- No IP allowlist (cron-job.org IPs are not a stable contract).
- Rotation: set the new key in Render and cron-job.org together; a short failure
  window only delays work by one tick. Dual-key support is deferred.

## 14. Module boundary

### Foundation (Application/Automation, Application/Notifications)

Knows: users, time zones, executions, deliveries, devices, handler registry,
local-time resolution. Knows **nothing** about Finance, Gym, Nutrition.

```csharp
// shape only, not an implementation
public interface IAutomationHandler
{
    string AutomationType { get; }                 // "WeeklyReview"
    TimeSpan MaxLateness { get; }
    Task<IReadOnlyList<DueOccurrence>> FindDueAsync(DateTimeOffset nowUtc, int limit, CancellationToken ct);
    Task<AutomationResult> ExecuteAsync(AutomationOccurrence occurrence, CancellationToken ct);
}
```

- Handlers are plain DI registrations (`IEnumerable<IAutomationHandler>`); no
  reflection scanning, no plugin loading, no MediatR.
- Handlers live **in their module's Application folder**
  (`Application/WeeklyReview/WeeklyReviewAutomationHandler`) and call that
  module's use cases. A handler may depend on the foundation; the foundation
  never depends on a handler.
- Architecture test: `LifeOS.Application.Automation` / `.Notifications` do not
  reference `LifeOS.Application.Finance|Gym|Nutrition` or the matching Domain
  namespaces.

### Preferences: approach A vs. B — decision: **B**

| | A. Generic `AutomationPreference(type, schedule)` | B. Module-owned settings + shared scheduling helpers (chosen) |
|---|---|---|
| Fits weekly review (day + time) | yes | yes |
| Fits hydration (interval + window) | needs JSON or many nullable columns | hydration module owns its own shape |
| Fits Todo (due-date driven, no schedule) | does not fit | handler queries Todo items directly |
| Validation | generic, weak | typed, in the owning Domain |
| Risk | drifts into a rules engine | small duplication of `enabled` flags |

AUTO-001 creates **no preferences table**. Shared pieces are code, not data:
`LocalSchedule` helpers, the execution store, the delivery outbox. AUTO-002 adds
its own setting (likely just `enabled`, fixed Sunday 20:00 at first).

## 15. AI boundary

- AUTO-001 has **no AI**: no call to `lifeos-ai`, no AI port used, no Groq.
- The tick never wakes `lifeos-ai`.
- For AUTO-002 the order is fixed: deterministic `WeeklyReviewData` → persisted
  → *optional* interpretation. AI unavailable/slow/invalid → report saved without
  commentary, execution Succeeded, push still sent.
- AI latency (a cold `lifeos-ai` can need > 60 s, ADR-011 uses 120 s timeouts)
  does not fit a 20 s tick budget. Recommended for AUTO-002: ship v1 without AI;
  add commentary later as a separate, best-effort enrichment step that updates the
  saved report and never gates the notification.

## 16. Multi-user scaling path

What v1 already does:

- tick processes **bounded batches** (≤ 25 executions, ≤ 50 deliveries, ≤ 20 s);
- discovery reads ids only, keyset-ordered, `LIMIT`ed; zone math over distinct zones;
- oldest-first claims and rotated handler order give basic fairness; leftovers
  continue next tick (`more: true`);
- lateness windows bound backlog: work that cannot run in time expires instead of
  accumulating forever.

Capacity estimate: 25 executions/tick × 6 ticks/hour = 150/hour → with the
16-hour tick window, a weekly review for roughly 2,000–3,000 users inside their 24 h
lateness windows on a single Free instance, if each review takes < 1 s. Far beyond
personal use.

Next steps, only when measured need appears:

1. raise batch sizes / shorten cadence to 5 min;
2. per-module `next_due_at_utc` column on the module's settings row (indexed) when
   per-user times exist, replacing zone buckets for that handler;
3. tick only *enqueues* (inserts executions) and a separate worker process drains
   them (`SKIP LOCKED` already makes this safe with N workers);
4. move the in-memory 60 s guard to a PostgreSQL advisory lock when there is more
   than one API instance;
5. a real scheduler (or 24/7 paid instance with an in-process timer) replaces
   cron-job.org — the tick endpoint and everything behind it stay the same.

## 17. Security/privacy

### Trust boundaries

```text
[cron-job.org] ──(X-LifeOS-Automation-Key)──► [lifeos-api]
   trusted only to say "now is a good time"; knows no users, no data.
   Leak impact: early/extra processing of already-due work; bounded by 60 s guard.

[Android app] ──(LifeOS access token)──► [lifeos-api]
   registers its own devices, reads its own reports. Never sees the tick key or FCM credentials.

[lifeos-api] ──(Google service-account OAuth)──► [FCM] ──► [device]
   FCM (Google) receives: device token, "LifeOS", "Your weekly review is ready",
   type + opaque ids. Never report contents, amounts, user email or names.

[lifeos-api] ◄──► [Neon PostgreSQL]
   reports, executions, deliveries, device tokens. System of record.

[lifeos-api] ──► [lifeos-ai]     (AUTO-002+ only, optional; not touched by AUTO-001)
```

### Rules

- **Secrets** (Render env, Secret class): `Automation__TickKey`,
  `Notifications__Fcm__ServiceAccountJson` (Base64 of the service-account JSON),
  plus non-secret `Notifications__Fcm__ProjectId`. `google-services.json` in the app
  is client configuration, not a secret, but is kept out of the repository for
  Production builds alongside other environment config (decision at implementation).
- **Device tokens** are sensitive: never logged (log the registration id), never
  returned by any API, deleted on sign-out, cascade-deleted with the user.
- **Logs** record tick counts, execution/delivery ids, automation type, status,
  failure codes, FCM error *codes*. Never tokens, keys, report bodies, FCM
  request/response bodies, emails.
- **Notification text** is a fixed generic template; lock-screen visibility can
  stay default because nothing sensitive is shown.
- **Deep link** carries only an opaque id; authorization happens in the API on open.
- **Failure codes** stored in the database are stable codes, never exception text
  (which could contain personal data).
- Runbook additions (implementation phase): stop conditions "tick answers anything
  but 401 without the key", "logs contain a push token or tick key", "push body
  contains personal data".

### Public-product strengthening (not built now)

| Area | Later need |
|---|---|
| Rate limits | per-user notification caps per day/kind; tick abuse protection beyond the 60 s guard |
| Preferences | per-kind opt-out, quiet hours, global mute, in-app notification settings |
| Unsubscribe | one-tap disable from the notification / settings page |
| Workers | separate worker from web, more than one instance, advisory locks |
| Scheduler | redundant trigger (second scheduler) or managed scheduler; SLA |
| Observability | metrics (due, executed, late, expired, failed, push accepted/invalid), alerts on FailedFinal rate |
| GDPR | export includes reports, executions, deliveries, devices; deletion cascades (already modelled); retention policy for executions/deliveries (e.g. 13 months) |
| Credentials | FCM key rotation, Workload Identity instead of a JSON key, dual tick keys |
| Cost/abuse | per-user caps on automations and devices, Neon/Render budget monitoring |

## 18. Render/cron-job.org production flow

### Target jobs

| Job | Request | Schedule (Europe/Rome) |
|---|---|---|
| `lifeos-api automation tick` | `POST https://<ACTUAL_RENDER_DOMAIN>/api/internal/automation/tick`, header `X-LifeOS-Automation-Key`, no body | `*/10 7-22 * * *` |
| `lifeos-ai keepalive` | unchanged `GET /health/live` | unchanged |

The tick **replaces** the current `lifeos-api keepalive` (any request keeps Render
awake). Runbook S19 ("a keepalive job carries credentials") must be amended so
that this one job is explicitly allowed its header; the `/health/live` keepalive
rules stay for `lifeos-ai`.

**VERIFY AT EXECUTION**: cron-job.org supports POST with custom headers, its
request timeout (expected ~30 s), and that response history stores only our
count-only body.

### Day in production

```text
07:00  tick wakes lifeos-api (cold; may time out on cron-job.org's side)
07:10  tick: service warm → deliveries, retries, discovery (e.g. US users' overdue Sunday reviews)
...    every 10 min
20:00  tick: Europe/Rome users' weekly reviews due → executed, deliveries enqueued
20:00  same tick, next run of Phase A (or 20:10): push sent
22:50  last tick; service sleeps ≈ 23:05
night  nothing runs; due work waits for 07:00 within its lateness window
```

### Cost risks to check before enabling

- **Render instance hours**: unchanged by Phase 1 (same window as keepalive). A
  24/7 tick ≈ 744 h/month — essentially the whole Free allowance with no margin;
  not recommended. If overnight coverage is needed: hourly pairs `0,5 23-6 * * *`
  (the :00 request wakes, :05 processes) ≈ 8 × ~20 min/day ≈ +80 h/month.
- **Neon compute**: today's keepalive deliberately touches no database. A tick
  that queries PostgreSQL every 10 minutes keeps Neon compute awake most of the
  waking day (Neon auto-suspends after ~5 idle minutes). **VERIFY current Neon
  Free compute allowance** and measure for a week. Mitigation designed in:
  the tick can skip the database entirely when an in-memory "next possible work
  at" hint (earliest open zone window, earliest `next_attempt_at_utc`) has not
  been reached; the hint is recomputed on startup, after each DB-touching tick, and
  invalidated when a time zone, device test or delivery is written in-process.
  Recommended to implement in AUTO-001 only if the measurement shows a risk.

## 19. Alternatives considered

| Alternative | Verdict |
|---|---|
| One cron-job.org job per user / per automation type | Rejected: scheduler would know users and logic; not scalable; secrets multiply. |
| In-process `BackgroundService`/`PeriodicTimer` | Rejected: does not run while Render sleeps; would need the external wake-up anyway; duplicates with >1 instance. |
| Hangfire / Quartz.NET | Rejected: same sleep problem; adds packages, their own tables and dashboards for what four tables and one endpoint do. Not justified. |
| Render Cron Job / paid scheduler / VPS | Rejected: paid or new infrastructure. |
| pg_cron on Neon | Rejected: business scheduling in the database, availability on Neon Free uncertain, cannot send push. |
| GitHub Actions `schedule` | Viable **backup trigger** later (same endpoint, same key); delays of many minutes and a secret in another system make it a poor primary. |
| Generic workflow/rules engine (JSON schedules) | Rejected: speculative; module-owned settings (§14 B). |
| Per-user `automation_schedules(next_due_at)` table now | Deferred: zone buckets suffice for fixed-time automations; adopt per module when per-user times exist. |
| Notification state as columns on executions | Rejected (§11): blocks non-automation notifications, tangles two state machines. |
| Per-device delivery rows | Deferred (§11). |
| FCM data-only messages | Rejected for v1: not shown if the app process is restricted; notification+data is shown by the system reliably. |
| `FirebaseAdmin` SDK vs. direct HTTP v1 | Recommend direct HTTP v1 with `HttpClient` + service-account OAuth token (`Google.Apis.Auth`, or a small BCL RS256 JWT exchange to avoid any package). Decide at implementation; either stays in Infrastructure. |
| UnifiedPush / ntfy / self-hosted push | Rejected for v1: extra infrastructure; possible later for de-Googled devices. |
| Device-local scheduled notifications (Android AlarmManager/WorkManager) | Not for the weekly review (needs server data). **Worth considering for hydration-style reminders** that need no server state — AUTO-003 should evaluate it. |
| `Authorization: Bearer` for the tick | Rejected in favour of a dedicated header (§13). |
| NodaTime | Not added; BCL `TimeZoneInfo` covers IANA + DST with the explicit rules in §6. |

## 20. Proposed implementation phases

AUTO-001 is delivered as one branch in four reviewable steps; each builds and has
tests. Write an **ADR-012 "Server-side automation: external tick, PostgreSQL
state, FCM push"** with step 1.

1. **Time zone** — `users.time_zone_id`, `User.SetTimeZone`, `PUT /api/me/time-zone`,
   app sends `TimeZoneInfo.Local.Id`; `LocalSchedule.ResolveWeekly` + DST unit tests
   (gap, overlap, Sunday DST changes, zone change, lateness edges).
2. **Automation core** — `automation_executions`, `IAutomationHandler`,
   `RunAutomationTick`, claim/retry/complete store (PostgreSQL), tick endpoint +
   scheme/policy + startup validation; zero production handlers; tests with a
   test-only handler (idempotency, concurrent claims, lease expiry, fencing,
   retry bounds, expiry).
3. **Notifications** — `device_registrations`, `notification_deliveries`, device
   endpoints, `NotificationDeliveryService`, `IPushNotificationSender` +
   `FcmPushNotificationSender`, test-notification endpoint; MAUI: Firebase
   Messaging binding, permission, token registration, `OnNewToken`, tap → route.
4. **Production** — Firebase project + Android apps (Release and Debug ids),
   service account, Render env vars, cron-job.org tick job replacing the API
   keepalive, runbook part E + stop conditions, phone acceptance.

Migration ordering: NUT-003 (nutrition targets) is being built in parallel and
will add its own migration. AUTO-001's migration must be generated **after**
NUT-003 is merged to avoid a conflicting model snapshot.

## 21. Open questions

1. **Travel**: should `time_zone_id` follow the device automatically (recommended
   v1) or be a user-chosen "home zone" with the device value only as a suggestion?
2. **Firebase project**: add Firebase to the existing Google Cloud project (today
   OAuth-only per runbook) or create a separate project? Separate keeps the
   runbook's "OAuth only" statement true; same project is fewer consoles.
3. **Language** of notification text (English as written here vs. Italian /
   device locale via FCM `title_loc_key`/`body_loc_key`).
4. **Weekly review late delivery**: is a notification at, say, 07:00 Monday Rome =
   01:00 Monday in New York acceptable, or do we need quiet hours before public use?
5. **Neon compute budget** with DB-touching ticks: measure first or implement the
   in-memory skip hint up front?
6. **Test-notification endpoint**: keep permanently (useful in settings) or only
   for acceptance?
7. **FCM auth**: `Google.Apis.Auth` package vs. hand-written RS256 JWT exchange.
8. **Retention** for executions and deliveries (proposal: 13 months, cleanup later).
9. Should a delivery be **created** when the user has no enabled device
   (record `Skipped`, proposed) or not created at all?

## 22. Suggested AUTO-001 implementation scope

In scope:

- `users.time_zone_id` + `PUT /api/me/time-zone` + app synchronization.
- `LocalSchedule.ResolveWeekly` with the DST rules of §6.
- Tables `automation_executions`, `notification_deliveries`, `device_registrations`
  (one migration, after NUT-003).
- `POST /api/internal/automation/tick` with `X-LifeOS-Automation-Key` scheme, 60 s
  guard, time/item budget, phases A–C, counts-only response; disabled when not
  configured.
- `IAutomationHandler` registry and dispatcher with **no production handler**.
- Device endpoints (`PUT`/`DELETE`), token reassignment rules, sign-out cleanup.
- Notification outbox processing, FCM HTTP v1 sender, invalid-token handling,
  Android tag = delivery id.
- `POST /api/devices/{installationId}/test-notification` for acceptance.
- MAUI: FCM token, Android 13 permission, registration on start/rotation,
  notification channel, tap handling with an authenticated generic route.
- Architecture tests (foundation does not reference module namespaces; FCM only in
  Infrastructure), Application tests, PostgreSQL-level tests for claims if the
  existing test setup allows.
- ADR-012, runbook part E (Firebase, secrets, cron job, stop conditions).

Out of scope: any handler, weekly review, reminders, AI, preferences tables,
quiet hours, rate limits, cleanup jobs, iOS.

## 23. Suggested AUTO-002 weekly-review scope

- `weekly_reviews` table: `id`, `user_id`, `week_start_date`, `week_end_date`
  (local Monday–Sunday), `time_zone_id`, `generated_at_utc`, versioned deterministic
  snapshot (`data_version` + JSON document, like ADR-008 snapshots: the report must
  not change when history is edited later), optional `ai_commentary` columns later;
  `UNIQUE (user_id, week_end_date)`.
- Module summary queries exposed by Finance, Gym and Nutrition Application layers
  (read-only, per user, by local date range); the review use case composes them.
- `WeeklyReviewAutomationHandler`: Sunday 20:00 local, 24 h lateness, key = local
  Sunday date, enabled flag (module-owned setting; default on).
- Decide whether Sunday is included up to 20:00 or the review covers the full
  Mon–Sun with Sunday partial; interaction with Nutrition lazy close of Sunday.
- API: `GET /api/weekly-reviews`, `GET /api/weekly-reviews/{id}`; app page and deep
  link target `weekly_review`.
- Notification `WeeklyReviewReady` with the fixed generic text.
- No AI in v1 (recommended); AI commentary as a later, non-blocking enrichment.

## 24. Suggested AUTO-003 reminders scope

- `LocalSchedule.ResolveDaily` (+ time windows if hydration needs them).
- Quiet hours and per-kind enable/disable (first real need for notification
  preferences; still module-owned or one small `notification_preferences` table
  keyed by `(user_id, kind)` — decide then).
- First reminders, each a handler in its own module:
  - **Finance**: monthly recurring items awaiting manual confirmation (ADR-009) /
    planned expenses due — text without amounts ("You have Finance items to review").
  - **Nutrition**: "No meals logged today" in the evening.
- Hydration only after a hydration tracking feature exists; evaluate device-local
  notifications for it (§19).
- Todo/reminders module: due-date driven handler with occurrence key per item.
- Per-module `next_due_at_utc` if per-user reminder times are configurable (§16).

---

## Minimum database changes for AUTO-001

| Change | Why it cannot be avoided |
|---|---|
| `users.time_zone_id` (column) | local-time scheduling needs a durable IANA zone per user |
| `automation_executions` | idempotency (unique occurrence), state, leases, bounded retries, history |
| `notification_deliveries` | push retry without regenerating artifacts; non-automation notifications; outbox atomicity |
| `device_registrations` | multiple devices per user, token rotation, invalid-token disabling |

Three new tables and one column. Nothing else.
