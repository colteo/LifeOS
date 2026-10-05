# AUTO-001 — Scheduling and notifications foundation

Status: DESIGN v2 — product decisions recorded; not implemented. No code,
migration, API, package, Firebase, Google Cloud, Render or cron-job.org change is
part of this document. Implementation progress is tracked in
[Implementation status](#implementation-status), at the end of this document.

Enables AUTO-002 (Weekly Review) and AUTO-003 (reminders). Builds on ADR-001
(onion), ADR-002 (.NET is the system of record), ADR-005 (PostgreSQL), ADR-006
(user-owned data, explicit `UserId`, composite ownership keys), ADR-011
(service-key pattern, Render hosting) and the
[production runbook](../../operations/production-runbook.md) (Render Free, Neon
Free, cron-job.org keepalive).

Revision history:

- v1 (`bc1e23b`): initial design.
- v2: product decisions closed; one notification delivery **per device**;
  keepalive and automation tick kept **separate**; staged Neon wake strategy
  replaces the in-memory skip hint; Weekly Review v1 without AI.

---

## Product decisions (closed)

These are **decisions**, not alternatives. The rest of the document applies them.

| # | Decision |
|---|---|
| PD-1 | **Firebase project**: FCM is added to the **same existing Google Cloud project** that LifeOS uses for Google OAuth. No second Google/Firebase project. Done when AUTO-001 is implemented. |
| PD-2 | **Time zone follows the device** automatically. The app reports its current IANA zone; LifeOS stores it; future occurrences use it; history is not rewritten. No "home time zone". No time zone ⇒ no scheduled automation. |
| PD-3 | **Notification copy is English** in v1 (`LifeOS` / `Your weekly review is ready`). Localization deferred; no localization infrastructure in AUTO-001. |
| PD-4 | **Quiet hours are not part of AUTO-001.** The only planned automation (Sunday 20:00 local) does not need them. They belong to AUTO-003. |
| PD-5 | **Retention**: automation execution and notification delivery history are kept **13 months**. Cleanup implementation may be deferred; the period is fixed. |
| PD-6 | **FCM server auth**: **FCM HTTP v1 + `Google.Apis.Auth`**. Not the Firebase Admin SDK (unless a concrete need appears), not a hand-written OAuth/JWT exchange. |
| PD-7 | **One notification delivery row per device**, not per logical notification. |
| PD-8 | **Keepalive ≠ automation tick.** The anonymous `/health/live` keepalive stays exactly as it is. The authenticated tick is a separate job. |
| PD-9 | **Weekly Review v1 (AUTO-002) has no AI.** AI commentary is a later AUTO-002.1. |
| PD-10 | **AutomationExecution ≠ WeeklyReview.** Execution metadata says "scheduled work ran"; the WeeklyReview (AUTO-002) is the saved user-facing report. |

---

> ### Keepalive and automation tick are two different things
>
> | | Keepalive (unchanged) | Automation tick (new) |
> |---|---|---|
> | Request | `GET /health/live` | `POST /api/internal/automation/tick` |
> | Auth | anonymous, no headers | `X-LifeOS-Automation-Key` secret |
> | Touches PostgreSQL / wakes Neon | **never** | yes |
> | Logic | none | evaluates and runs due work |
> | Purpose | keep Render warm in waking hours | let LifeOS decide what is due |
> | Schedule | every 10 min, 07:00–22:50 Europe/Rome | only when operationally useful (§18) |
> | cron-job.org job | existing `lifeos-api keepalive` | separate `lifeos-api automation tick` |
>
> The tick never replaces the keepalive, and the keepalive never carries a key
> (runbook stop condition S19 stays as written).

---

## 1. Problem

LifeOS has no server-side notion of time-driven work. Everything happens because
a user opened the app (Nutrition lazy close runs only on a request). Upcoming
features need LifeOS to act on its own:

- a **weekly review** every Sunday evening, in the user's local time;
- **reminders** (Finance, Nutrition, a future Todo module);
- future scheduled actions in modules that do not exist yet.

Production constraints rule out the usual answers:

- `lifeos-api` is one Render Free instance that **sleeps after 15 idle minutes**;
  an in-process timer does not fire while it sleeps.
- Neon Free compute auto-suspends when idle; the keepalive deliberately avoids
  the database so Neon can sleep. Anything that queries PostgreSQL wakes it.
- No paid scheduler, Render Cron Job, VPS, Redis or broker.
- The only external trigger is **cron-job.org**.
- The Nutrition `utcOffsetMinutes` convention is correct for request-local dates
  but cannot answer "when is next Sunday 20:00 for this user?" across DST.
- There is no push channel to the Android app.

## 2. Goals / non-goals

### Goals

1. A periodic, authenticated, **parameterless tick** lets LifeOS decide what is due.
2. A durable **IANA time zone per user**, following the device, with defined DST
   behaviour.
3. **Idempotent** execution enforced by PostgreSQL; at most one success per
   logical occurrence.
4. Persisted **execution history** with leases and bounded retries.
5. **Android push** via FCM; many devices per user; full token lifecycle.
6. **Per-device notification delivery** retried independently of the automation
   and of other devices.
7. **Module boundaries**: the automation core knows no Finance/Gym/Nutrition rules.
8. **Many users**, bounded batches; nothing hard-coded to one user or Europe/Rome.
9. **Replaceable scheduler**: anything that can send an HTTP POST with a header.
10. **Cadence-independent** tick: correctness never depends on how often or when
    exactly the scheduler fires, so the schedule can evolve (§18).

### Non-goals

- Any real automation handler (weekly review, reminders).
- AI of any kind.
- A workflow/rules engine, JSON schedule DSL, plugin framework.
- Hangfire, Quartz, Celery, Redis, broker, worker process, pg_cron.
- An in-memory scheduling cache to avoid database wake-ups.
- Quiet hours, localization, per-type notification preferences, rate limiting
  beyond simple guards, unsubscribe UI.
- iOS (kept possible, not built), web push, email, SMS.
- Changing the Nutrition `utcOffsetMinutes` convention.

## 3. First use case: Sunday 20:00 weekly review (AUTO-002, not implemented here)

```text
Sunday 20:00 in the user's IANA time zone
  → a tick inside the Sunday/Monday window sees occurrence
       (user, WeeklyReview, week ending 2026-10-04) is due
  → WeeklyReviewAutomationHandler builds deterministic data:
       Finance weekly summary + Gym weekly summary + Nutrition weekly summary
  → one transaction:
       persist WeeklyReview (user-facing artifact, AUTO-002 table)
       mark execution Succeeded (result_id = review id)
       insert one notification_delivery per currently enabled device
  → Phase A of the same or next tick sends each device's push:
       title "LifeOS", body "Your weekly review is ready",
       data { type: "weekly_review", id: "<WeeklyReview id>" }
  → tap → app (authenticated) opens the saved report
```

No amounts, balances, calories, workouts or meal text in the push. No AI in v1
(PD-9).

## 4. Proposed architecture

```text
cron-job.org: "lifeos-api keepalive"      GET /health/live          (anonymous, no DB)  ── unchanged
cron-job.org: "lifeos-api automation tick"
   │  POST /api/internal/automation/tick
   │  X-LifeOS-Automation-Key: <secret>     (no body, no query string)
   ▼
LifeOS.Api ── AutomationTickEndpoints (transport + auth only)
   ▼
LifeOS.Application/Automation ── RunAutomationTick (bounded: time + item caps)
     Phase A  NotificationDispatch  send due per-device deliveries
     Phase B  Retry                 re-claim FailedRetryable / lease-expired executions
     Phase C  Discovery             for each registered IAutomationHandler:
                                      FindDue → claim → Execute → atomic completion
                                    (AUTO-001 registers ZERO business handlers)
   ▼
Ports (Application)                      Implementations (Infrastructure)
   IAutomationExecutionStore       ──►   EF Core / PostgreSQL claims
   INotificationDeliveryStore      ──►     (INSERT … ON CONFLICT, conditional UPDATE,
   IDeviceRegistrationRepository   ──►      FOR UPDATE SKIP LOCKED)
   IPushNotificationSender         ──►   FcmPushNotificationSender
                                           (HTTP v1 + Google.Apis.Auth)
   TimeProvider (BCL)

Android app ── PUT /api/me/time-zone, PUT/DELETE /api/devices/{installationId},
               POST /api/notifications/test        (LifeOS user access token)
```

Principle: **the external scheduler triggers; LifeOS decides.** The tick carries
no user, no automation type, no time. A tick at any moment, any number of times,
only makes LifeOS do work that is due by LifeOS's own clock.

## 5. Data model proposal

One column and three tables. snake_case, UUID v7 ids from Domain/Application,
`timestamptz` UTC. Exact column types are finalized at implementation.

### 5.1 `users.time_zone_id` — "where is this user in time"

```text
users
+ time_zone_id   varchar(64) NULL    IANA id reported by the user's device; NULL = unknown
```

Responsibility: the single current zone used to resolve **future** local
occurrences for every module. NULL ⇒ the user has no local-time automation (never
defaulted). A column on `User` (like `default_currency`), not a preferences
table. Index `ix_users_time_zone_id` supports `SELECT DISTINCT time_zone_id`.

### 5.2 `device_registrations` — "where can this user be reached"

```text
device_registrations
- id                     uuid          PK
- user_id                uuid          → users(id) ON DELETE CASCADE
- installation_id        varchar(64)   random id generated by the app per installation
- platform               varchar(16)   Android  (iOS later)
- push_provider          varchar(16)   Fcm
- push_token             varchar(4096) NULL when inactive; sensitive
- status                 varchar(16)   Active | Inactive
- inactive_reason        varchar(32)   NULL | PermissionDenied | SignedOut | TokenInvalid
- created_at_utc         timestamptz
- updated_at_utc         timestamptz
- last_seen_at_utc       timestamptz   last upsert from the app

ux_device_registrations_installation  UNIQUE (installation_id)
ux_device_registrations_token         UNIQUE (push_provider, push_token) WHERE push_token IS NOT NULL
ux_device_registrations_id_user       UNIQUE (id, user_id)        -- target of composite FK (ADR-006)
ix_device_registrations_user_active   (user_id) WHERE status = 'Active'
```

Responsibility: one row per app installation, owned by the user currently signed
in on it. **Active** ⇔ token present, OS permission granted, user signed in.
Inactive rows keep no token (cleared) and remain only as the target of delivery
history until retention removes them.

### 5.3 `automation_executions` — "did this scheduled occurrence run"

```text
automation_executions
- id                    uuid          PK
- user_id               uuid          → users(id) ON DELETE CASCADE
- automation_type       varchar(64)   stable code, e.g. "WeeklyReview"
- occurrence_key        varchar(64)   handler-defined, from local calendar data ("2026-10-04")
- time_zone_id          varchar(64)   zone used to resolve this occurrence (never rewritten)
- scheduled_for_utc     timestamptz   resolved due instant
- expires_at_utc        timestamptz   no attempt starts at/after this
- status                varchar(16)   Running | Succeeded | FailedRetryable | FailedFinal
- attempt_count         int           ≥ 1; fencing token for completion
- lease_expires_at_utc  timestamptz   NULL unless Running
- next_attempt_at_utc   timestamptz   NULL unless FailedRetryable
- last_failure_code     varchar(64)   NULL; stable code, never exception text
- result_id             uuid          NULL; opaque id of the artifact (no FK)
- created_at_utc        timestamptz
- started_at_utc        timestamptz   start of latest attempt
- completed_at_utc      timestamptz   NULL until terminal

ux_automation_executions_occurrence  UNIQUE (user_id, automation_type, occurrence_key)
ix_automation_executions_retry       (next_attempt_at_utc) WHERE status = 'FailedRetryable'
ix_automation_executions_stale       (lease_expires_at_utc) WHERE status = 'Running'
ck_automation_executions_status      CHECK on status values
```

Responsibility: idempotency, state, leases, bounded retries, history. It says
"scheduled work ran" — it never holds the report (PD-10). No "Pending" row: a row
is created at the moment it is claimed (§8), so "due but not started" is "no row".

### 5.4 `notification_deliveries` — "did this notification reach this device"

One row **per (logical notification, device)** (PD-7).

```text
notification_deliveries
- id                      uuid          PK
- user_id                 uuid          → users(id) ON DELETE CASCADE
- device_registration_id  uuid          composite FK (device_registration_id, user_id)
                                        → device_registrations(id, user_id) ON DELETE CASCADE
- notification_key        varchar(128)  logical notification identity, deterministic:
                                          "automation:<execution id>" | "test:<uuid>"
- notification_type       varchar(32)   WeeklyReviewReady | Test | …  (selects fixed English copy)
- source_execution_id     uuid          NULL → automation_executions(id) ON DELETE SET NULL
- resource_type           varchar(32)   NULL | "weekly_review" …   (deep-link target)
- resource_id             uuid          NULL; stable LifeOS id opened on tap
- status                  varchar(16)   Pending | Sending | Sent | Failed
- attempt_count           int
- next_attempt_at_utc     timestamptz   when Pending: earliest send
- lease_expires_at_utc    timestamptz   NULL unless Sending
- expires_at_utc          timestamptz   never sent at/after this
- last_error_code         varchar(32)   NULL | Transient | TokenInvalid | DeviceInactive |
                                        MaxAttempts | Expired | Rejected
- created_at_utc          timestamptz
- sent_at_utc             timestamptz   NULL until accepted by FCM

ux_notification_deliveries_device   UNIQUE (notification_key, device_registration_id)
ix_notification_deliveries_due      (next_attempt_at_utc) WHERE status = 'Pending'
ix_notification_deliveries_stale    (lease_expires_at_utc) WHERE status = 'Sending'
```

Responsibility: independent delivery state per device. No title/body text is
stored (the type selects fixed copy in code), no push token (it is read from the
registration at send time), no provider message.

### 5.5 Not created by AUTO-001

- No generic `automation_preferences` (§14: module-owned settings later).
- No `automation_schedules` / `next_due_at` table (scaling path, §16).
- No `weekly_reviews` (AUTO-002).
- No quiet-hours or notification-preference tables (AUTO-003).

## 6. Timezone model

### Decision (PD-2)

The zone follows the device. The app reports its current IANA zone; when it
changes, LifeOS updates `users.time_zone_id`; future occurrences use the new zone;
`automation_executions.time_zone_id` of past runs is never rewritten. No "home
time zone". NULL ⇒ nothing scheduled. Fixed UTC offsets are never the stored
representation.

### Obtaining an IANA id on the client (design notes, not implemented)

- **.NET for Android (MAUI)**: `TimeZoneInfo.Local.Id` is expected to return the
  Android system zone id, which is IANA (`Europe/Rome`). The Android API
  `Java.Util.TimeZone.Default.ID` returns the same id and is the fallback if the
  .NET value is ever not IANA. **VERIFY on a physical device.**
- **Stale cache**: .NET caches `TimeZoneInfo.Local`. After the device zone changes
  while the process lives, the app must call `TimeZoneInfo.ClearCachedData()`
  before reading it (or read the Android API directly).
- **When to send**: after sign-in completes, on app start and on resume, only if
  the value differs from the last value the server acknowledged (cached on the
  device). Listening to Android's `ACTION_TIMEZONE_CHANGED` is optional; resume is
  sufficient for scheduling purposes.
- **Windows/desktop** (development only, not a shipped client): `TimeZoneInfo.Local.Id`
  is a Windows id (`W. Europe Standard Time`); a client there would convert with
  `TimeZoneInfo.TryConvertWindowsIdToIanaId`. The server never accepts Windows ids.
- **Future iOS**: `TimeZoneInfo.Local.Id` / `NSTimeZone.LocalTimeZone.Name` are IANA.

### Server-side validation and normalization

`PUT /api/me/time-zone { "timeZoneId": "Europe/Rome" }` (idempotent; same value is
a no-op):

- trim; length ≤ 64;
- `TimeZoneInfo.TryFindSystemTimeZoneById` must succeed **and** `HasIanaId` must
  be true (rejects Windows ids, which Linux .NET would otherwise resolve, and raw
  offsets like `+02:00`) → otherwise `400`, stored value unchanged;
- store the id as resolved. **No aggressive canonicalization**: .NET has no API
  for IANA link → canonical mapping, and aliases (`Europe/Kiev` / `Europe/Kyiv`,
  `Asia/Calcutta` / `Asia/Kolkata`) resolve to the same rules, so they schedule
  identically;
- tzdata version skew: a device can know a newer zone than the server image. Then
  the server returns `400`, keeps the previous value, and the app retries on a
  later start (after the server image updates). Logged as a warning with the id
  (not personal data).
- `Etc/GMT±N` / `UTC` are valid IANA ids (fixed rules); accepted if the device
  reports them.
- The API image must contain tzdata (Debian-based `aspnet:10.0` expected —
  **VERIFY AT IMPLEMENTATION**); when automation is enabled the API resolves
  `Europe/Rome` at startup and refuses to start if it cannot.
- BCL `TimeZoneInfo` only; no NodaTime.

### Resolving a local due time

A pure, unit-tested Application helper (part of the foundation):

```text
LocalSchedule.ResolveWeekly(zone, nowUtc, DayOfWeek day, TimeOnly time, TimeSpan maxLateness)
  → Occurrence { LocalDate, DueAtUtc, ExpiresAtUtc } | none
```

1. `localNow = ConvertTime(nowUtc, zone)`.
2. candidate local date = most recent `day` ≤ `localNow.Date` (may be today);
3. `dueLocal = date + time` → UTC with the DST rules below;
4. due iff `DueAtUtc ≤ nowUtc < DueAtUtc + maxLateness`.

`ResolveDaily` is added by AUTO-003, not before.

### DST rules (for every local-time automation)

| Situation | Rule | Example (Europe/Rome) |
|---|---|---|
| Local time does not exist (spring-forward gap) | shift forward by the gap length | 2026-03-29 02:30 → 03:30 CEST = 01:30Z |
| Local time occurs twice (fall-back) | first occurrence (earlier instant) | 2026-10-25 02:30 → 02:30 CEST = 00:30Z |
| Normal | plain conversion | 2026-10-04 20:00 CEST = 18:00Z |

Sunday 20:00 is never in a gap/overlap in current zones, but the rule is defined
once. The occurrence key is the **local date**, so an ambiguous time still yields
one occurrence. Both 2026 Europe/Rome DST changes fall on Sundays (29 March,
25 October): those weeks have 167/169 hours, so AUTO-002 aggregates by **local
dates**, never by `now - 7 days`.

### Time zone changes

- New zone applies from the next tick.
- Moving west can make "Sunday 20:00" recur in absolute time: the key
  `(user, WeeklyReview, local Sunday date)` prevents a second report.
- Moving east can put the new due instant in the past: inside the lateness window
  it runs at the next tick; beyond it, that week's occurrence is skipped (no row).
  Documented, acceptable.
- Two devices of one user in different zones: last writer wins (see Risks).

### Delayed tick

A tick at 20:03, 20:47 or the next morning executes the same occurrence once as
long as `now < expires_at_utc`.

## 7. Tick/scheduling algorithm

### Cadence independence

The algorithm makes no assumption about cadence. Correctness requires only:

> for every occurrence, **at least one tick** lands in `[DueAtUtc, ExpiresAtUtc)`.

Promptness is `tick interval` + cold start. How often and when ticks fire is an
operational choice that evolves by stage (§18). Inside the tick window the
recommended interval is **10 minutes**: delay 0–10 min, invisible for a weekly
review; 5 min doubles database wake-ups for no visible gain.

### One tick

```text
POST /api/internal/automation/tick
  authenticate (§13) → 401 on failure
  if a tick started < 60 s ago on this instance → 200 { "skipped": true }
  nowUtc = TimeProvider.GetUtcNow(); deadline = nowUtc + 20 s

  Phase A — deliveries:  claim ≤ 50 per-device deliveries (Pending & next_attempt ≤ now,
                          or Sending with expired lease), oldest first; send; record.
  Phase B — retries:     claim ≤ 25 executions (FailedRetryable & next_attempt ≤ now,
                          or Running with expired lease); execute; complete.
  Phase C — discovery:   handlers in rotated order: FindDue(nowUtc, remaining cap)
                          → claim (INSERT … ON CONFLICT DO NOTHING) → execute → complete.
  stop claiming at deadline or cap; already-claimed items finish.

  200 { "deliveries": n, "executions": m, "more": true|false }      (counts only)
```

- Not linked to `HttpContext.RequestAborted`: a scheduler giving up does not abort
  an item mid-way. Atomic completion (§8) makes aborts harmless anyway.
- `more: true` = backlog; the next tick continues. Nothing waits or sleeps inside a
  request.
- With zero handlers (AUTO-001), Phase C is a no-op and Phases A–B process test
  deliveries and nothing else.

### Finding due users without loading everyone (fixed-time automations)

**Zone buckets**:

```text
zones = SELECT DISTINCT time_zone_id FROM users WHERE time_zone_id IS NOT NULL   (≤ ~400)
open  = zones where LocalSchedule.ResolveWeekly(zone, now, Sunday, 20:00, 24h) is due
due   = SELECT u.id FROM users u
        WHERE u.time_zone_id = ANY(@openZones)
          AND <module enabled condition>
          AND NOT EXISTS (SELECT 1 FROM automation_executions e
                          WHERE e.user_id = u.id AND e.automation_type = 'WeeklyReview'
                            AND e.occurrence_key = <key for u's zone>)
        ORDER BY u.id LIMIT @limit                                    (ids only)
```

Zone arithmetic runs in .NET over a few hundred zones; PostgreSQL filters by zone
id. When per-user times appear (AUTO-003), that module materializes
`next_due_at_utc` on its own settings row instead (§16).

## 8. Idempotency/concurrency

### Logical occurrence identity

`(user_id, automation_type, occurrence_key)` — UNIQUE in PostgreSQL. The key comes
from **local calendar data**, never from the tick instant:

| Automation | occurrence_key |
|---|---|
| WeeklyReview (AUTO-002) | local week-ending Sunday date, `2026-10-04` — one report max per user/week |
| Daily reminder (AUTO-003) | local date |
| Todo due (later) | `<todo id>:<due local date-time>` |

### Claim = insert

```sql
INSERT INTO automation_executions (..., status, attempt_count, lease_expires_at_utc)
VALUES (..., 'Running', 1, @now + interval '5 minutes')
ON CONFLICT (user_id, automation_type, occurrence_key) DO NOTHING
RETURNING id;
```

Committed in its own short transaction. A returned row = this tick owns attempt 1.

### Retry claim = conditional update

```sql
UPDATE automation_executions
SET status = 'Running', attempt_count = attempt_count + 1,
    lease_expires_at_utc = @now + @lease, started_at_utc = @now, next_attempt_at_utc = NULL
WHERE id = @id AND @now < expires_at_utc
  AND ((status = 'FailedRetryable' AND next_attempt_at_utc <= @now)
    OR (status = 'Running' AND lease_expires_at_utc <= @now))
RETURNING attempt_count;
```

Batch selection uses `FOR UPDATE SKIP LOCKED`; ticks never block each other.

### Atomic, fenced completion

The handler reads/computes outside any transaction, then one transaction:

1. write the artifact (AUTO-002 `weekly_reviews`, with its own unique key);
2. `UPDATE automation_executions SET status='Succeeded', … WHERE id=@id AND status='Running'
    AND attempt_count=@myAttempt` — must affect exactly 1 row;
3. insert one `notification_deliveries` row per **currently Active** device of the
   user, `ON CONFLICT (notification_key, device_registration_id) DO NOTHING`.
   Zero active devices ⇒ zero rows; the execution still succeeds.

If step 2 affects 0 rows (lease expired, another tick took over), everything rolls
back: no duplicate artifact, no duplicate deliveries. A transactional outbox in
one database — no broker.

### Concurrent ticks

Both compute the same candidates; the unique index lets one INSERT win. Retries
and deliveries are claimed with conditional updates / `SKIP LOCKED`, so each row
is handled by one tick. The 60 s in-memory guard only saves wasted work on the
single instance; correctness never depends on it.

### Lease

5 minutes (≫ 20 s tick budget, ≪ retry delays). An expired lease = crashed attempt.

## 9. Execution state machine

Four states — the smallest model covering crash, retry and give-up.

```text
          claim (INSERT)
  (no row) ─────────────► Running ── atomic completion ──► Succeeded
                            │  ▲
       retryable failure,   │  │ retry claim (next_attempt ≤ now, now < expires)
       attempts < 3,        ▼  │
       now < expires     FailedRetryable
                            │
  attempts = 3, expired,    ▼
  or permanent failure ─► FailedFinal

  Running with expired lease ── retry claim ──► Running     (or ► FailedFinal "Expired")
```

- **Succeeded** means the artifact exists and deliveries (if any device) were
  enqueued. Notification outcome is **not** part of execution state.
- **FailedFinal** codes: `MaxAttemptsReached`, `Expired`, `Permanent:<code>`.

Handler results (no exceptions for expected cases): `Succeeded(resultId,
notification?)`, `RetryableFailure(code)`, `PermanentFailure(code)`,
`NotApplicable` (e.g. feature disabled since discovery → recorded Succeeded with
no artifact and no notification, so it is not rediscovered). Unexpected exceptions
→ retryable `Unhandled`, details only in sanitized logs.

## 10. Failure/retry policy

| | Executions | Per-device deliveries |
|---|---|---|
| Max attempts | 3 | 5 |
| Delays between attempts | 10 min, 30 min | 10 min, 30 min, 1 h, 3 h |
| Absolute bound | `expires_at_utc` (WeeklyReview: due + 24 h) | `expires_at_utc` (WeeklyReviewReady: created + 24 h; Test: created + 15 min) |

Retries are executed **by later ticks**, never by waiting in a request. Retries
therefore also depend on ticks being scheduled; the Stage 1 window (§18) is wide
enough for all weekly-review retries.

### Weekly review lateness: 24 hours

Justification: covers a cold start, an overnight Render sleep, a scheduler outage
of several hours and the whole Sunday/Monday tick window (§18) for every zone;
beyond 24 h (Monday 20:00 local) a "your weekly review is ready" push is stale, so
the occurrence expires (`FailedFinal: Expired` if it was ever claimed; otherwise no
row) and is visible in logs.

### Scenarios

| Scenario | Behaviour |
|---|---|
| Scheduler request fails | Nothing (or nothing complete) claimed; next tick does it. |
| Render asleep when tick fires | The tick wakes it; the cold start (~1 min) may exceed cron-job.org's timeout and that request may or may not be processed; the next tick 10 min later processes all non-expired work. |
| API restarts during execution | Row stays Running; lease expires; later tick reclaims (attempt +1). Completion is one transaction, so nothing partial exists. |
| Module summary fails | FailedRetryable → up to 3 attempts within 24 h → FailedFinal. No artifact, no deliveries. |
| AI interpretation fails (AUTO-002.1 only) | Not an automation failure: deterministic report saved, execution Succeeded, push sent. |
| User has no active device | Execution Succeeded, zero delivery rows. Report available in-app. |
| Push transient failure on one device | Only that device's row returns to Pending with backoff. Other devices unaffected. Report never regenerated. |
| Push token invalid (`UNREGISTERED` / invalid token) | That row Failed (`TokenInvalid`); that registration becomes Inactive, token cleared. Other devices unaffected. |
| Device signs out / denies permission while a row is Pending | Row Failed (`DeviceInactive`) when processed; nothing sent. |
| DB write succeeds, push fails | The outbox case: delivery rows exist and retry independently. |
| FCM accepted, crash before `Sent` recorded | Lease expiry → resend to that device only. The Android notification **tag** (= `notification_key`) makes the second replace the first: no visible duplicate. |
| Same tick twice / two ticks at once | Unique index + conditional claims (§8). |
| Render wakes late | Due work runs at the first tick before `expires_at_utc`. |
| User deleted | CASCADE removes devices, executions, deliveries. |

## 11. Push notification architecture

### Provider and project (PD-1, PD-6)

**Firebase Cloud Messaging, HTTP v1 API**, authenticated with **`Google.Apis.Auth`**.

```text
Google Cloud project "LifeOS" (existing)
├── Google OAuth Production client          (exists, unchanged)
└── Firebase (added by AUTO-001 implementation, Spark plan, no billing)
    ├── Android app it.colazzo.lifeos        (Release)
    ├── Android app it.colazzo.lifeos.dev    (Debug)
    ├── Firebase Cloud Messaging API (enabled)
    └── service account "lifeos-api-fcm"     (send-only role)
```

- Same project as OAuth (PD-1). The runbook statement "Google Cloud is used only
  for OAuth" is amended when AUTO-001 is implemented.
- FCM is free; no payment method (keeps runbook stop condition S13).
- FCM relays to APNs later, so iOS needs no server redesign.
- `Google.Apis.Auth` supplies service-account credential loading, scoping
  (`https://www.googleapis.com/auth/firebase.messaging`) and cached access-token
  refresh, without the larger Firebase Admin SDK surface. The sender itself is a
  plain `HttpClient` call to
  `POST https://fcm.googleapis.com/v1/projects/<project-id>/messages:send`.
  Both live only in Infrastructure.

### Credential (high level; nothing created in this task)

- Type: a **Google Cloud service-account key (JSON)** for a dedicated service
  account with the minimal FCM send role (e.g. *Firebase Cloud Messaging API
  Admin*; exact role **VERIFY AT IMPLEMENTATION**).
- Loaded with `Google.Apis.Auth`'s **typed service-account loader**, so only a
  service-account credential is accepted from configuration.
- Production: a Render **Secret** environment value
  (`Notifications__Fcm__ServiceAccountJson`, Base64 of the JSON) plus non-secret
  `Notifications__Fcm__ProjectId`. Never committed, never logged, never on the
  device. Whether an absent credential in Production fails startup or disables
  push (rest of LifeOS unaffected) is Open question 3.
- Rotation: create new key → update Render → verify test notification → delete
  old key.
- The app's `google-services.json` is client configuration, not a secret.

### Flow

```text
Android app ─ FCM token + POST_NOTIFICATIONS permission
            └─ PUT /api/devices/{installationId}  ──► device_registrations (Active)

completion transaction / test endpoint
            └─ one notification_deliveries row per Active device

Phase A (tick) or inline (test)
   claim row → load its registration → still Active? else Failed(DeviceInactive)
   → build message from notification_type (fixed English copy)
   → IPushNotificationSender.SendAsync(token, message)
   → Accepted → Sent | TokenInvalid → Failed + registration Inactive
     | Transient → Pending + backoff | other 4xx → Failed(Rejected)
```

### Why one delivery per device (PD-7)

| | One row per logical notification (v1) | One row per device (v2, chosen) |
|---|---|---|
| Phone accepted, tablet transient | either resend to both (duplicate on phone) or give up on tablet | retry tablet only |
| Invalid token on one device | handled inline, no record | recorded on that row |
| Sent time | one, ambiguous | per device |
| Attempts / backoff | shared | independent |
| Claiming | one row fans out (long work in one claim) | small rows, `SKIP LOCKED` per device |
| Future iOS/APNs with different failure modes | awkward | natural |
| Cost | 1 row | N rows (N = active devices, typically 1–3) |

The extra rows are trivial; the gain is exact, independent retry without ever
re-notifying a device that already accepted. The logical notification is
identified by `notification_key`; uniqueness `(notification_key,
device_registration_id)` prevents duplicate rows.

A device that registers **after** the rows were created gets nothing for that
notification (documented, acceptable).

### Message shape and copy (PD-3)

```json
{
  "message": {
    "token": "<device token>",
    "notification": { "title": "LifeOS", "body": "Your weekly review is ready" },
    "data": { "type": "weekly_review", "id": "0192f0c3-…" },
    "android": {
      "priority": "normal",
      "notification": { "tag": "<notification_key>", "channel_id": "lifeos_general" }
    }
  }
}
```

| notification_type | Title | Body | Data |
|---|---|---|---|
| WeeklyReviewReady (AUTO-002) | LifeOS | Your weekly review is ready | `type=weekly_review`, `id` |
| Test (AUTO-001) | LifeOS | Test notification from LifeOS | `type=test` |

- Notification + data message: in background the system shows it and passes
  `data` to the launched activity; in foreground the app shows it as a local
  notification with the same tag.
- `data` carries only a type and an opaque id. On tap: the app routes through the
  normal authenticated shell; if signed out, sign in first, then navigate; an id
  that does not belong to the user yields the normal not-found page.
- English only, fixed strings in code; no localization infrastructure.

## 12. FCM device lifecycle

| Event | App | Server |
|---|---|---|
| First start after sign-in | create `installationId` (random UUID in app Preferences, survives sign-outs, lost on reinstall); request permission; get FCM token | `PUT /api/devices/{installationId}` upsert: `user_id` from the access token, token, platform, `Active` |
| Every app start / resume (signed in) | re-send registration | upsert, `last_seen_at_utc` |
| Token rotation (`OnNewToken`) | re-send registration | update token on that installation |
| Permission denied / revoked | send `notificationsPermitted: false` | `Inactive (PermissionDenied)`, token cleared — **no active push registration** |
| Permission granted later | re-send with token | `Active` |
| Same token already on another row (other user or stale install) | — | same transaction: clear token + `Inactive` on the other row, then upsert. A token is never active for two users |
| Another user signs in on this installation | same `installationId`, new access token | row re-owned by the new user; one installation ↔ one user while signed in |
| Sign-out | best-effort `DELETE /api/devices/{installationId}` **before** revoking the session; delete the local FCM token | `Inactive (SignedOut)`, token cleared (row kept for delivery history) |
| Sign-out while offline | — | row stays Active until the next sign-in on that install re-owns it, or FCM reports `UNREGISTERED` after `deleteToken` |
| Reinstall | new `installationId`, new token | new row; old row becomes Inactive on its first `UNREGISTERED` |
| FCM `UNREGISTERED` / invalid token | — | `Inactive (TokenInvalid)`, token cleared |
| Long inactivity | — | deferred cleanup: deactivate rows with `last_seen_at_utc` older than 60 days (FCM expires tokens inactive ~270 days) |
| Multiple devices | one row each | every Active row gets its own delivery |
| Future iOS | `platform = iOS` | no schema change |

Ownership (ADR-006): `UserId` is never in a payload. `DELETE` of an installation
owned by someone else → 404. The upsert re-owns an installation by design (that
is "another user signed in on this device"); it never touches other rows of the
caller or other users except through the token-uniqueness rule above.

### Test notification endpoint

```text
POST /api/notifications/test        (no body; LifeOS user access token)
→ 200 { "devices": n, "sent": s, "failed": f }      (or 409 "no_active_device")
```

- Authenticated as the current user; targets **only that user's Active
  registrations**; creates rows with `notification_key = "test:<uuid v7>"` and
  sends them inline through the same delivery service (proves the per-device
  queue); leftovers expire after 15 minutes.
- Fixed generic copy; **no client-supplied text, token, device id or user id**;
  cannot reach another user.
- Rate limit: 1 per minute and 10 per day per user (in-memory is enough on one
  instance; `429` otherwise).
- **Recommendation: keep it permanently, in Development and Production.** It can
  only send a fixed harmless message to the caller's own devices, and it is the
  simplest way to diagnose "I don't get notifications" (permission, reinstall,
  token rotation, FCM credential rotation). Whether it gets a visible Settings
  button or stays a diagnostics action is decided after acceptance (Open question 2).

## 13. Internal tick authentication

**Header: `X-LifeOS-Automation-Key: <secret>`** — not `Authorization: Bearer`,
because in `lifeos-api` `Authorization: Bearer` already carries LifeOS user access
tokens (JWT). A separate header keeps the user-auth pipeline untouched and makes
it impossible to confuse a user token with the scheduler credential.

- Secret: ≥ 32 random bytes, Base64; only in Render env `Automation__TickKey` and
  the cron-job.org tick job's header. Never in the repository, a URL, a query
  string, the Android app or logs.
- Comparison: SHA-256 both values, then `CryptographicOperations.FixedTimeEquals`.
- A dedicated authentication scheme + policy on that one endpoint (ADR-006
  "endpoint by endpoint"; not `AllowAnonymous` + ad-hoc check). A user JWT can never
  satisfy it.
- Failure: `401`, empty body, warning log without the presented value.
- Not configured: endpoint **not mapped** (404), automation disabled, rest of
  LifeOS works. Configured with < 32 visible characters: startup failure.
- No body (any body → 400), no parameters. **Possession of the key only lets the
  caller make LifeOS evaluate work that is already due**; it cannot choose users,
  types or times. The 60 s guard bounds abuse cost.
- Response: counts only (cron-job.org keeps response history).
- No IP allowlist.
- Rotation: update Render and cron-job.org together; worst case one missed tick.

## 14. Module boundary

The core (`Application/Automation`, `Application/Notifications`) knows users,
zones, executions, deliveries, devices, the handler registry and local-time
helpers — **nothing** about Finance, Gym or Nutrition.

```csharp
// shape only
public interface IAutomationHandler
{
    string AutomationType { get; }                 // "WeeklyReview"
    TimeSpan MaxLateness { get; }
    Task<IReadOnlyList<DueOccurrence>> FindDueAsync(DateTimeOffset nowUtc, int limit, CancellationToken ct);
    Task<AutomationResult> ExecuteAsync(AutomationOccurrence occurrence, CancellationToken ct);
}
```

- Plain DI registrations (`IEnumerable<IAutomationHandler>`); no scanning, no
  plugins, no MediatR.
- Handlers live in their module (`Application/WeeklyReview/WeeklyReviewAutomationHandler`)
  and call that module's use cases. Handlers depend on the core; never the reverse.
- Architecture test: `Application.Automation` / `.Notifications` reference no
  `Finance|Gym|Nutrition` namespaces; FCM and `Google.Apis.Auth` appear only in
  Infrastructure.

### Preferences: module-owned settings (approach B)

| | A. Generic `AutomationPreference(type, schedule)` | B. Module-owned settings + shared helpers (chosen) |
|---|---|---|
| Weekly review (day + time) | fits | fits |
| Reminders with windows | JSON or many nullable columns | module owns its shape |
| Todo (due-date driven) | does not fit | handler queries Todo items |
| Validation | generic, weak | typed, in the owning Domain |
| Risk | becomes a rules engine | small repeated `enabled` flags |

AUTO-001 creates no preferences table. Shared pieces are code: `LocalSchedule`,
execution store, delivery queue.

## 15. AI boundary

- AUTO-001: **no AI**. No `lifeos-ai` call; the tick never wakes `lifeos-ai`.
- AUTO-002 v1: **no AI** (PD-9). Deterministic Finance + Gym + Nutrition summaries
  = the saved weekly report; the push follows.
- AUTO-002.1 (later): optional AI commentary as a best-effort enrichment of an
  already saved report. If `lifeos-ai` is unavailable or slow, the deterministic
  report stays valid and the push is still delivered. AI latency (cold
  `lifeos-ai` can exceed 60 s; ADR-011 uses 120 s) does not fit a 20 s tick, which
  is one more reason it must never gate the report or the notification.

## 16. Multi-user scaling path

Already in v1:

- bounded batches (≤ 25 executions, ≤ 50 deliveries, ≤ 20 s per tick);
- ids-only, keyset-ordered, `LIMIT`ed discovery; zone math over distinct zones;
- oldest-first claims and rotated handler order for fairness; `more: true` → next
  tick continues;
- lateness windows bound backlog: late work expires instead of piling up.

Capacity (Stage 1 window, §18): 25 × 6 ticks/hour × 48 h ≈ 7,000 executions per
week on one Free instance if each takes < 1 s — orders of magnitude beyond
personal use.

Later, only on measured need: bigger batches / 5-min cadence → per-module
`next_due_at_utc` when per-user times exist → tick only enqueues and a separate
worker drains (`SKIP LOCKED` already supports N workers) → PostgreSQL advisory
lock instead of the in-memory guard with > 1 instance → a different scheduler (the
endpoint stays the same).

## 17. Security/privacy

### Trust boundaries

```text
[cron-job.org keepalive] ── GET /health/live (anonymous) ──► [lifeos-api]   no data, no DB

[cron-job.org tick] ── X-LifeOS-Automation-Key ──► [lifeos-api]
    trusted only to say "now is a good time". Knows no users or data.
    Key leak ⇒ extra evaluation of already-due work, bounded by the 60 s guard.

[Android app] ── LifeOS access token ──► [lifeos-api]
    manages its own installation, reads its own reports, triggers own test push.
    Never sees the tick key or FCM credential.

[lifeos-api] ── service-account OAuth (Google.Apis.Auth) ──► [FCM / Google] ──► [device]
    Google receives: device token, fixed English title/body, type + opaque id.
    Never report contents, amounts, names or email.

[lifeos-api] ◄──► [Neon PostgreSQL]   reports, executions, deliveries, devices (system of record)
[lifeos-api] ──► [lifeos-ai]           AUTO-002.1+ only, optional; never from AUTO-001
```

### Rules

- Secrets (Render Secret class): `Automation__TickKey`,
  `Notifications__Fcm__ServiceAccountJson`. Non-secret: `Notifications__Fcm__ProjectId`.
- Device tokens: never logged (log the registration id), never returned by any
  API, cleared when Inactive, cascade-deleted with the user.
- Logs: tick counts, execution/delivery ids, types, statuses, error **codes**.
  Never tokens, keys, report bodies, FCM request/response bodies, emails.
- Stored error codes are a fixed vocabulary, never provider messages or exception text.
- Notification copy is fixed and non-personal; deep links carry opaque ids;
  authorization happens on open.
- Retention (PD-5): `automation_executions` and `notification_deliveries` kept
  13 months; Inactive device registrations without remaining deliveries removed
  after 13 months. Cleanup may be implemented later as a bounded maintenance
  step. 13 months ≫ any lateness window, so deleting an old execution can never
  cause an occurrence to run again.
- Runbook additions at implementation: tick answers anything but 401 without the
  key; logs contain a token or key; push copy contains personal data → stop.

### Public-product strengthening (not built now)

| Area | Later need |
|---|---|
| Rate limits | per-user notification caps per day/type; stronger tick abuse protection |
| Preferences | per-type opt-out, quiet hours (AUTO-003), global mute |
| Unsubscribe | disable from settings / notification action |
| Workers | worker separate from web, multiple instances, advisory locks |
| Scheduler | redundant trigger or managed scheduler |
| Observability | metrics (due, late, expired, failed, push accepted/invalid), alerts |
| GDPR | export of reports/executions/deliveries/devices; deletion already cascades; retention enforced |
| Credentials | FCM key rotation policy, keyless workload identity, dual tick keys |
| Cost/abuse | caps on devices per user, Neon/Render budget monitoring |
| Localization | translated copy (PD-3 defers it) |

## 18. Render/cron-job.org production flow

### Two jobs, two purposes (PD-8)

| Job | Request | Schedule | Changes with AUTO-001/002? |
|---|---|---|---|
| `lifeos-api keepalive` | `GET /health/live`, no headers | `*/10 7-22 * * *` Europe/Rome | **no** — unchanged |
| `lifeos-ai keepalive` | `GET /health/live`, no headers | `*/10 7-22 * * *` Europe/Rome | **no** — unchanged |
| `lifeos-api automation tick` | `POST /api/internal/automation/tick`, header `X-LifeOS-Automation-Key`, no body | staged (below) | new |

Stop condition S19 keeps applying to keepalive jobs. The tick job gets its own
stop conditions (only the tick URL, POST, the key header, no query string).

**VERIFY AT EXECUTION**: cron-job.org supports POST with custom headers, per-job
time zone (UTC), its request timeout (~30 s expected), and that its response
history holds only our count-only body.

### Neon wake strategy (staged)

A DB-touching tick every 10 minutes all day would keep Neon compute awake most of
the day for work that exists one evening a week. AUTO-001 does **not** solve this
with an in-memory scheduling cache. It solves it operationally: the tick is
cadence-independent (§7), so the **schedule** carries the cost decision, while
LifeOS still decides who/what is due. cron-job.org knows only broad time windows,
never users, zones or modules.

**Stage 0 — AUTO-001 (no business handler).** No production tick job is needed.
Acceptance uses manual authenticated ticks (one-off POST from PowerShell) and the
test notification, which sends inline without a tick. Optionally a short-lived
job for verification, then paused.

**Stage 1 — AUTO-002 (weekly review only).** One tick job, **UTC, every 10 minutes
on Sunday and Monday only**:

```text
cron (time zone UTC):   */10 * * * 0,1
```

Why this window works for every zone:

- the earliest Sunday 20:00 on Earth is UTC+14 → **Sunday 06:00 UTC**;
- the latest is UTC−12 → **Monday 08:00 UTC**;
- the window Sunday 00:00 → Monday 23:50 UTC contains every due instant, with ≥ 16 h
  after the latest one for execution retries (10 + 30 min) and delivery retries
  (up to ~4.7 h);
- Europe/Rome: Sunday 20:00 = 18:00 UTC (CEST) / 19:00 UTC (CET) — inside.

Cost: ~2 of 7 days with DB-touching ticks; Neon sleeps the other 5 days as today.
Render: the tick wakes `lifeos-api` outside keepalive hours for roughly 16 h per
week (about +70 instance-hours per month, on top of ~500 h, within the 750 h Free
allowance — **VERIFY current terms**).

Trade-off and variants:

| Variant | Schedule | Pro | Con |
|---|---|---|---|
| **Sun+Mon UTC (recommended)** | `*/10 * * * 0,1` | one job, simple, large retry slack | ~18 h/week more ticking than strictly needed |
| Tight window | Sun 06:00 → Mon 12:00 UTC (two jobs) | ~30 h/week of ticks | two jobs to keep in sync; less slack |
| Hourly in window | `0 * * * 0,1` | ~6× fewer DB wakes | up to 60 min delay; retries slower |
| Global every 10 min, every day | `*/10 * * * *` | simplest mentally | Neon awake most of every day for one weekly job; Render ~744 h/month |

**Stage 2 — regular global tick.** Move to a daily, every-10-minutes tick when
frequent work becomes normal: the first **daily or due-date automation** ships
(AUTO-003 reminders or Todo). From then any day can have due work in some zone. At
that point measure Neon/Render usage and choose between: accepting the cost,
15–20-minute cadence, ticking 24 h vs. bounded hours, or a paid tier. Each of these
is a schedule change only.

**Stage 3 — scale.** Worker/queue separation and a different scheduler (§16).

### Sunday in production (Stage 1, Europe/Rome user)

```text
00:00 UTC Sunday   tick job starts (cold wake possible; first tick may time out)
...                every 10 min: Phase A/B/C; outside open zones discovery is cheap
18:00 UTC (20:00)  Rome users due → reviews saved, per-device deliveries inserted
18:00/18:10        Phase A sends pushes
Monday             late zones (Americas) and retries; 23:50 UTC last tick
Tue–Sat            no tick; keepalive alone; Neon sleeps
```

## 19. Alternatives considered

| Alternative | Verdict |
|---|---|
| One scheduler job per user / per automation type | Rejected: scheduler would know users/logic. |
| Replace the keepalive with the tick | Rejected (PD-8): would wake Neon all day and mix an anonymous health probe with a secret-bearing job. |
| In-memory "next work at" hint to skip DB | Rejected for AUTO-001: complexity and invalidation bugs; staged schedule solves the cost. |
| In-process `BackgroundService` / timer | Rejected: does not run while Render sleeps; duplicates with > 1 instance. |
| Hangfire / Quartz.NET | Rejected: same sleep problem; extra packages and tables. |
| Render Cron Job / paid scheduler / VPS | Rejected: paid or new infrastructure. |
| pg_cron on Neon | Rejected: business scheduling in the database; cannot push. |
| GitHub Actions `schedule` | Possible backup trigger later (same endpoint/key); delays make it a poor primary. |
| Generic workflow/rules engine | Rejected; module-owned settings (§14). |
| Per-user `next_due_at` table now | Deferred until per-user times exist. |
| Delivery state on executions | Rejected: tangles state machines; blocks non-automation notifications. |
| One delivery per logical notification | Rejected in v2 (PD-7): cannot retry one device without re-notifying others. |
| Firebase Admin SDK | Not chosen (PD-6): larger surface than needed. Reconsider only for a concrete need (e.g. topic management). |
| Hand-written OAuth/JWT exchange | Rejected (PD-6): security-sensitive code we would own. |
| Second Google/Firebase project | Rejected (PD-1). |
| FCM data-only messages | Rejected for v1: not shown when the app process is restricted. |
| UnifiedPush / self-hosted push | Rejected for v1: extra infrastructure. |
| `Authorization: Bearer` for the tick | Rejected: header already means "user access token". |
| NodaTime | Not added; BCL suffices with the rules in §6. |
| "Home time zone" setting | Rejected (PD-2): device-following zone in v1. |

## 20. Proposed implementation phases

One branch, four reviewable steps, each building and tested. **ADR-012
"Server-side automation and push notifications"** (external tick, PostgreSQL
state, per-device FCM delivery, Firebase in the existing project) is written with
step 1.

1. **Time zone** — `users.time_zone_id`, `User.SetTimeZone`, `PUT /api/me/time-zone`,
   app synchronization (§6); `LocalSchedule.ResolveWeekly` + DST tests.
2. **Automation core** — `automation_executions`, `IAutomationHandler` registry,
   `RunAutomationTick`, claim/retry/fenced completion store, tick endpoint +
   scheme/policy + startup validation. No business handler; tests use a test-only
   handler.
3. **Notifications** — `device_registrations`, `notification_deliveries`
   (per device), device endpoints, delivery service, `IPushNotificationSender` +
   `FcmPushNotificationSender` (HTTP v1 + `Google.Apis.Auth`), test endpoint;
   MAUI: Firebase Messaging binding, permission, token registration, `OnNewToken`,
   notification channel, tap/deep-link routing.
4. **Production** — Firebase added to the existing Google Cloud project, Android
   apps (Release + Debug), service account and key, Render secrets, runbook part E
   (incl. tick job definition for Stage 1, paused until AUTO-002), phone acceptance.

Migration ordering: NUT-003 (nutrition targets) adds its own migration in
parallel; AUTO-001's migration is generated **after** NUT-003 merges.

## 21. Open questions

### Closed (product decisions)

| # | Question | Decision |
|---|---|---|
| 1 | Time zone: device or home zone? | Follows the device (PD-2) |
| 2 | Firebase project | Same existing LifeOS Google Cloud project (PD-1) |
| 3 | Notification language | English initially (PD-3) |
| 4 | Quiet hours | Deferred to AUTO-003 (PD-4) |
| 5 | FCM server auth library | FCM HTTP v1 + `Google.Apis.Auth` (PD-6) |
| 6 | Retention | 13 months (PD-5) |
| 7 | Delivery granularity | One row per device (PD-7) |
| 8 | Delivery rows when no active device | None; automation still succeeds |

### Still open (need later evidence)

1. **Tick cadence and Neon wake cost.** Is Stage 1 (`*/10 * * * 0,1` UTC) cheap
   enough on Neon Free, or should it be the tight window / hourly variant? Measure
   during the first AUTO-002 weeks. When exactly to enter Stage 2 is decided with
   AUTO-003 using those numbers.
2. **Test notification after acceptance.** Recommended to keep (safe); decide
   whether it gets a visible Settings action or stays a diagnostics-only action.
3. **FCM credential deployment mechanics.** Base64 JSON in an environment variable
   vs. a Render Secret File; whether the Google Cloud project's policy allows
   service-account key creation; exact minimal IAM role; behaviour when the
   credential is absent in Production (fail startup vs. push disabled).
4. **Public-product thresholds.** At what user/notification volume to add worker
   separation, per-user caps and stronger observability.
5. **Multi-device time zone conflict.** Is last-writer-wins acceptable in practice
   (phone and tablet set to different zones), or does the server need to prefer the
   most recently active device? Needs real usage.

## 22. Suggested AUTO-001 implementation scope

In scope:

- `users.time_zone_id`; app → API time zone synchronization (§6).
- Scheduling/time zone helpers (`LocalSchedule.ResolveWeekly`, DST rules).
- `automation_executions` persistence; leases; idempotent claims; fenced completion.
- Authenticated `POST /api/internal/automation/tick` (`X-LifeOS-Automation-Key`),
  60 s guard, budgets, phases A–C, counts-only response; unmapped when not configured.
- Handler registry/dispatcher — **no business handler ships**.
- FCM device registration lifecycle (`PUT`/`DELETE /api/devices/{installationId}`,
  token uniqueness, sign-out, invalid tokens).
- Per-device `notification_deliveries` queue with bounded retries.
- FCM HTTP v1 sender with `Google.Apis.Auth`.
- App: notification permission, FCM token handling, `OnNewToken`, channel,
  notification tap/deep-link infrastructure.
- Safe `POST /api/notifications/test`.
- ADR-012; runbook/production documentation (Firebase in existing project, secrets,
  tick job definition, stop conditions).
- Tests: DST/lateness helper, idempotent and concurrent claims, lease expiry,
  fencing, retry bounds/expiry, delivery state per device, token reassignment,
  architecture rules.

Demonstrable with: register a device → test notification arrives on the phone;
authenticated tick with zero handlers returns `200` and processes nothing
(unauthenticated → `401`); execution/idempotency infrastructure tests green.

Out of scope: any scheduled business handler, weekly review, reminders, AI,
preferences tables, quiet hours, localization, retention cleanup job (period fixed,
job deferred), iOS, production tick job activation.

## 23. Suggested AUTO-002 weekly-review scope

- `weekly_reviews`: `id`, `user_id`, `week_start_date`, `week_end_date` (local
  Monday–Sunday), `time_zone_id`, `generated_at_utc`, versioned deterministic
  snapshot (`data_version` + document, like ADR-008 snapshots — the saved report
  does not change when history is edited later); `UNIQUE (user_id, week_end_date)`.
- Read-only weekly summaries from Finance, Gym and Nutrition Application layers,
  per user, by local date range.
- `WeeklyReviewAutomationHandler`: Sunday 20:00 local, 24 h lateness, key = local
  week-ending date, module-owned `enabled` setting (default on).
- Decide coverage of Sunday itself (until 20:00 vs. full day) and interaction with
  Nutrition lazy close.
- `GET /api/weekly-reviews`, `GET /api/weekly-reviews/{id}`; app Weekly Review page;
  deep link `weekly_review`.
- Push `WeeklyReviewReady` with the fixed English copy.
- Activate the Stage 1 tick job (§18).
- **No AI.** AI commentary is AUTO-002.1.

## 24. Suggested AUTO-003 reminders scope

- `LocalSchedule.ResolveDaily`.
- **Quiet hours** (PD-4) and **per-type notification preferences** — first real
  need; module-owned or one small `(user_id, notification_type)` table, decided then.
- Finance reminders (e.g. recurring items awaiting manual confirmation, ADR-009;
  planned expenses due) with copy that contains no amounts.
- Nutrition reminders (e.g. no meals logged today, evening).
- Future Todo due-date notifications (occurrence key per item).
- Entering **Stage 2** of the tick schedule (§18) based on measured cost.
- Hydration reminders are **not designed** until a hydration feature exists.

---

## Minimum database changes for AUTO-001

| Change | Responsibility |
|---|---|
| `users.time_zone_id` | current device-reported IANA zone used for future local occurrences |
| `device_registrations` | one row per app installation: owner, platform, FCM token, Active/Inactive |
| `automation_executions` | one row per logical occurrence: idempotency, state, lease, retries, history |
| `notification_deliveries` | one row per (logical notification, device): independent status, attempts, retry, sent time |

Three new tables and one column. No preferences table, no `weekly_reviews`.

---

## Implementation status

The design above is unchanged. This section only records progress on branch
`feature/automation-foundation`.

### Step 1 — Time zone (WP1): implemented, phone acceptance pending

**Server**

- `User.TimeZoneId` is stored in `users.time_zone_id varchar(64) NULL`, with
  index `ix_users_time_zone_id`.
- `PUT /api/me/time-zone` uses `SetTimeZoneHandler`. It returns `204` and is
  idempotent. An id that is not IANA (including Windows ids and raw offsets)
  returns `400` and leaves the stored value unchanged. It returns `401` without a
  token and `404` for a missing user.
- The migration `AddAutomationFoundation` contains only that column and that
  index.

**`LocalSchedule.ResolveWeekly`**

- Implements the §6 DST rules. A spring-forward gap time is read with the offset
  in force before the gap, which is the same as shifting it forward by the gap
  length (2026-03-29 02:30 → 03:30 CEST = 01:30Z).
- A fall-back time uses the earlier instant.
- The occurrence key is the local date.
- An occurrence is due iff `DueAtUtc ≤ now < ExpiresAtUtc`.

**App**

- `TimeZoneSynchronizer` sends the device zone when either of these happens:
  - session restore or sign-in finishes loading the profile;
  - the window resumes.
- It reads `TimeZoneInfo.Local` after `ClearCachedData()`. If that is not IANA,
  it falls back to `java.util.TimeZone.getDefault().getID()`.
- It sends only if the zone differs from the last zone the server acknowledged
  for this user. That value is in Preferences, and only a `204` updates it.
- The sync is best effort. It runs in the background and shows no sync UI. It
  makes no auth decision itself; the shared authorized HTTP pipeline still ends
  a session when refresh is rejected or a refreshed token receives another 401.
  A failure is retried on the next trigger.
- Concurrent triggers wait for the in-flight attempt and re-read the device
  zone, so a changed zone or a new account's sign-in is not dropped. Queued work
  for an account that is no longer current is ignored. Requests are bound to the
  session that started them: an account switch cannot replay a time-zone update
  with the new account's token or acknowledge the old account's request.
- There is no `ACTION_TIMEZONE_CHANGED` receiver.

**ADR-012** is written.

**Still to verify on a physical phone**

- Check the real value of `TimeZoneInfo.Local.Id` (expected `Europe/Rome`) and
  whether the Java fallback is ever used.
- Check that the server receives the zone after sign-in and after a cold-start
  session restore.
- Check that changing the phone's zone while the app is in the background is sent
  on resume. This depends on `ClearCachedData()` picking up the new zone in the
  running process.

**Not in step 1**

- The tzdata startup check ("resolve `Europe/Rome` at startup when automation is
  enabled") belongs to step 2, because it depends on automation being enabled.
- §6 says a rejected zone id is logged as a warning (for tzdata skew). This is
  not implemented yet; it is a deferred improvement.

### Steps 2–4: not started
