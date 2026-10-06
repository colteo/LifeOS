# AUTO-003A — Finance reminders foundation

Status: IMPLEMENTED on branch `feature/finance-reminders`. Not merged, not deployed. No Production
database, Render, Firebase, Google Cloud or cron-job.org change is part of this branch. The future
Production steps are described in the [runbook Part G](../../operations/production-runbook.md#part-g--finance-reminders-auto-003a)
and are **not executed**.

Builds on [AUTO-001](AUTO-001.md) (§6 DST rules, §8 idempotency, §14 module boundary, §18 staged
scheduler, §24 AUTO-003 scope), [AUTO-002](AUTO-002.md) (first business handler, outbox in the
completion transaction) and [ADR-012](../../adr/0012-server-side-automation-and-push-notifications.md).
Finance semantics are unchanged and come from [ADR-009](../../adr/0009-monthly-recurring-planning-and-manual-confirmation.md)
(FIN-004) and [ADR-010](../../adr/0010-one-off-planned-expenses.md) (FIN-005). No new ADR: every decision
below applies ADR-012 inside the Notifications core and the Finance module.

---

## 1. Scope

In scope: `LocalSchedule.ResolveDaily`, quiet hours, per-type reminder preferences, two Finance
reminder types, the Settings UI for them, deep links. Reuses the automation engine, the
`notification_deliveries` outbox, `users.time_zone_id` and the existing tap machinery. No AI.

Out of scope (not implemented): Nutrition, Todo and hydration reminders; activating Stage 2 of the
tick schedule; localization; per-user reminder times.

## 2. Product decisions (given)

| # | Decision |
|---|---|
| R-1 | Quiet hours are user-local and apply to **reminder** notifications only. Weekly Review stays Sunday 20:00 and is never held back. |
| R-2 | Default quiet hours 22:00–08:00 local. |
| R-3 | A reminder due inside quiet hours is sent at the first allowed local time, with the same occurrence identity; it is dropped only if its delivery expires first. |
| R-4 | Preferences: one small user-owned model; no generic settings framework. |
| R-5 | Reminder types: (A) recurring occurrence awaiting manual confirmation; (B) one-off planned expense due. |
| R-6 | Copy has no amount, balance, category, payee, account or email: "LifeOS" / "A recurring transaction needs your confirmation", "LifeOS" / "A planned expense is due". |
| R-7 | Taps reuse the existing machinery and open the closest stable Finance page. |
| R-8 | Both reminder types enabled by default. |
| R-9 | One logical reminder per item/occurrence/date, keyed by the Finance identity. |
| R-10 | Reminders never confirm, create, skip, cancel or edit Finance data. |
| R-11 | No scheduler change. Stage 1 stays as it is; Stage 2 is documented only. |

## 3. Design decisions

| # | Decision | Why |
|---|---|---|
| D-1 | **Preferences = one table `notification_preferences`, one optional row per user** (`user_id` PK): one boolean per reminder type + quiet hours. No row ⇒ defaults. | Smallest coherent shape. No column on `users`, no Finance-specific table, no `(user, type)` key/value table (quiet hours are per user, not per type, so a key/value table would need a second table). Adding a reminder type later = one boolean column. Default-on without backfill, like AUTO-002's `weekly_review_settings`. |
| D-2 | Preferences live in the **Notifications core** (`Domain/Notifications`, `Application/Notifications`). | Quiet hours are a user-level notification concern shared by every future reminder module. The core still references no business module (architecture tests). Weekly Review keeps its own module-owned setting. |
| D-3 | **`QuietHours` value object in Domain**: local `Start`/`End` (whole minutes), `[Start, End)`, wraps at midnight when `Start > End`, `Start = End` rejected. `NextAllowedUtc(zone, instant)` computes the end of the current quiet period in the user's IANA zone (§5). | Pure BCL `TimeZoneInfo`; no Application dependency; unit-testable. Never a UTC "22:00". |
| D-4 | **Quiet hours are enforced at dispatch time** (`NotificationDispatcher`), not at discovery. A claimed reminder delivery inside quiet hours goes back to Pending with `next_attempt_at_utc` = end of the quiet period, and its claim's attempt is given back (`CompleteDeferredAsync`: `attempt_count - 1`). | One mechanism covers every path to a device: first send, transient retries (10 min / 30 min / 1 h / 3 h), lease takeovers, and a delivery enqueued just before quiet hours and sent by the next tick. Discovery stays zone-bucketed (no per-user quiet-hours math in SQL). A deferral is not a send attempt, so it cannot exhaust the 5 attempts. |
| D-5 | Reminder deliveries expire **24 h** after enqueue. | A quiet period is always < 24 h, so a deferred reminder is always sent when quiet hours end (R-3). If quiet hours would outlast the expiry, the row becomes `Failed/Expired` instead of being sent late. |
| D-6 | The user's **current** zone (`users.time_zone_id`) is used for quiet hours; a user without a valid zone gets the reminder without quiet-hours evaluation (cannot happen in practice: discovery needs a zone). | Quiet hours follow the device like every local-time rule (PD-2). |
| D-7 | **Two handlers** (`FinanceRecurringReminder`, `FinancePlannedExpenseReminder`) in `Application/Finance/Reminders`. | Different occurrence identities and Finance sources; each is enabled independently. |
| D-8 | Reminder time **09:00 local on the item's scheduled date**, `LocalSchedule.ResolveDaily`, lateness **24 h** (`expires = due + 24 h`). | 09:00 is after the default quiet end (08:00). The reminder is about "today": after the next day's reminder time the item is still listed in Transactions → Planned, and a push would be stale. 24 h also covers Render sleep, cold starts, a scheduler outage of hours, and both execution retries. |
| D-9 | Discovery = zone buckets (AUTO-001 §7): `ResolveDaily` over the distinct user zones, then one ids-only `LIMIT`ed SQL per type over the users of the open zones. | Same pattern as AUTO-002; no per-user schedule table. |
| D-10 | Execution re-reads the item through the **existing read ports** (`IRecurringRepository.ReadAsync`, `IPlannedExpenseRepository.GetAsync`) and decides with the **Domain status** (`RecurringTransactionRule.Status`, `PlannedExpense.Status`). Not due any more ⇒ `NotApplicable` (Succeeded, no push, never rediscovered). | No recreated recurrence rules, no new due semantics (R-10). |
| D-11 | No artifact: `AutomationResult.Succeeded(notification: …)`, `result_id` NULL. | The reminder is the notification; Finance owns the data. |
| D-12 | Deep link: both types open **`finance/transactions?tab=planned`**. | The Planned tab of Transactions is the existing, stable page where due recurring occurrences and one-off planned expenses of the current month are confirmed inline (ADR-009/010). No second navigation system; the opaque id is carried but not needed. |
| D-13 | Settings: a **Notifications** section in the existing Settings page (two switches, two time inputs, one Save button). | No Settings or navigation redesign. The API takes the whole form at once. |

## 4. Reminder types

### A. Recurring transaction awaiting manual confirmation (FIN-004 / ADR-009)

- **Condition:** a Monthly Income or Expense rule whose logical month (year, month) of local date D:
  - is inside the rule's `[start, end]` month range;
  - is scheduled on D (requested day clamped to the month's last day: a day-31 rule is due on
    30 November);
  - has no Confirmed/Skipped state (`recurring_transaction_occurrences`).

  This is exactly ADR-009's "Due" on its scheduled date (unprocessed, scheduled ≤ local today).
- **Occurrence key:** `<rule id>:<yyyy-MM>` — the ADR-009 identity RuleId + Year + Month, never the
  scheduled date. Editing the rule's day cannot remind the same month twice.
- **Not reminded:** Confirmed, Skipped, out of range, deleted, or moved to a later day since discovery
  (then Projected). Past-due months that were never reminded (e.g. a rule created with a start month
  in the past) are not back-filled: only the scheduled date's window reminds.
- **Copy:** `LifeOS` / `A recurring transaction needs your confirmation`; data `type=finance_recurring`,
  `id=<rule id>`.

### B. One-off planned expense due (FIN-005 / ADR-010)

- **Condition:** a planned expense scheduled on local date D with no Confirmed/Cancelled state
  (`planned_expense_states`) — ADR-010's "Due" on its date.
- **Occurrence key:** `<planned expense id>:<yyyy-MM-dd>`. One reminder per expense and date: an
  unresolved expense rescheduled to another date is reminded again on that date; never twice for one date.
- **Not reminded:** Confirmed, Cancelled, deleted, or rescheduled since discovery.
- **Copy:** `LifeOS` / `A planned expense is due`; data `type=finance_planned_expense`,
  `id=<planned expense id>`.

Both conditions were cleanly supported by the existing Domain; no reminder type had to be dropped.

## 5. Quiet hours semantics

- Local wall-clock range `[Start, End)` in the user's current IANA zone; `Start > End` crosses midnight.
  Exactly at `Start` is quiet; exactly at `End` is allowed.
- `NextAllowedUtc(zone, now)`: `now` itself when not quiet; otherwise the end of the current period
  (today's `End`, or tomorrow's when the range crosses midnight and `now ≥ Start`).
- **Spring forward:** the end is reached at the local time `End` with the new offset (Rome 22:00–08:00
  on 29 March ends at 08:00 CEST = 06:00Z). An `End` inside the gap (e.g. 02:30) is reached when the
  clock jumps past it (03:00 CEST = 01:00Z).
- **Fall back:** the end uses the new offset (08:00 CET = 07:00Z on 25 October). An ambiguous `End`
  (e.g. 02:30) is its next occurrence after `now`: if the wall clock falls back into the range, the
  period lasts until `End` occurs again.
- Only `RecurringTransactionReminder` and `PlannedExpenseReminder` are subject to quiet hours
  (`NotificationCatalog.IsReminder`). `WeeklyReviewReady` and `Test` are never held back.

## 6. `LocalSchedule.ResolveDaily`

`ResolveDaily(zone, nowUtc, time, maxLateness)` → `LocalOccurrence { LocalDate, DueAtUtc, ExpiresAtUtc }` or none:

1. local today = `ConvertTime(now, zone)`'s date; due = today at `time` via `ToUtc` (AUTO-001 §6 rules:
   a gap time shifts forward by the gap length, an ambiguous time uses its first occurrence);
2. if today's due is still ahead, the candidate is yesterday;
3. due iff `DueAtUtc ≤ now < DueAtUtc + maxLateness`. The key is the local date.

On DST days consecutive due instants are 23 h or 25 h apart. With 24 h lateness, the most recent
date wins in the spring overlap hour, and no daily occurrence is open during the one-hour fall-back gap.

## 7. Schedule, occurrence and idempotency

| | Recurring | Planned expense |
|---|---|---|
| `automation_type` | `FinanceRecurringReminder` | `FinancePlannedExpenseReminder` |
| `occurrence_key` | `<rule id>:2026-11` | `<expense id>:2026-11-15` |
| `scheduled_for_utc` | 09:00 local on the scheduled date | same |
| `expires_at_utc` | due + 24 h | same |
| Delivery `notification_type` | `RecurringTransactionReminder` | `PlannedExpenseReminder` |
| Delivery key / expiry | `automation:<execution id>`, enqueue + 24 h | same |

- **Claim = insert** on `UNIQUE (user_id, automation_type, occurrence_key)`; discovery also excludes
  keys that already have an execution (SQL rebuilds the key: `uuid::text || ':' || to_char(date, …)`).
- **Retries:** AUTO-001 (3 attempts, 10 / 30 min, never after expiry). Unexpected exceptions are the
  stable code `Unhandled`; no exception text is stored. Invalid keys/zones are `Permanent:InvalidOccurrenceKey`
  / `Permanent:InvalidTimeZone`.
- **One logical notification:** deliveries are enqueued in the fenced completion transaction with
  `ON CONFLICT (notification_key, device_registration_id) DO NOTHING`. A stale attempt writes nothing.
- **Disabled preference:** excluded at discovery; disabled after discovery ⇒ `NotApplicable`.
  Disabling never cancels a delivery already enqueued.
- **Limits:** the existing tick caps (25 executions, 50 deliveries, 20 s) apply to all handlers; the
  handlers are rotated between ticks.

## 8. Preference model and API

```text
notification_preferences
- user_id                                  uuid        PK, FK → users(id) ON DELETE CASCADE
- recurring_transaction_reminders_enabled  boolean     NOT NULL
- planned_expense_reminders_enabled        boolean     NOT NULL
- quiet_hours_start                        time        NOT NULL   local, whole minutes
- quiet_hours_end                          time        NOT NULL   local, whole minutes
- updated_at_utc                           timestamptz NOT NULL

ck_notification_preferences_quiet_hours   start <> end AND whole minutes
```

| Endpoint | Result |
|---|---|
| `GET /api/notification-preferences` | `200 { recurringTransactionReminders, plannedExpenseReminders, quietHoursStart: "22:00", quietHoursEnd: "08:00" }` (defaults when never saved) |
| `PUT /api/notification-preferences` (all four fields required, times `HH:mm`) | `204`; `400` validation problem per field (missing value, not `HH:mm`, start = end); `404` missing user |

User access token only; the owner comes from the token (a `userId` in the body is ignored). The
write is `INSERT … ON CONFLICT (user_id) DO UPDATE`.

Migration `AddNotificationPreferences`: creates only this table (PK index, the check, the user FK).
`Down` drops the table. No backfill.

## 9. App

- **Settings → Notifications:** "Recurring transaction reminders" and "Planned expense reminders"
  switches, "Quiet hours start" / "Quiet hours end" time inputs, "Save notification settings".
  Loading, load error with Retry, validation/API error and "saved" states.
- `NotificationPreferencesApiClient` on the authenticated pipeline (`CreateAuthorizedHttpClient`).
- **Taps:** `finance_recurring` and `finance_planned_expense` → `finance/transactions?tab=planned`
  through `NotificationTap.PathFor` and the existing `AuthGate` (signed in and onboarded, opened once).
  The Planned tab shows the current month; a reminder delivered after midnight on the 1st (e.g. a
  31st item deferred by quiet hours) opens the new month, one tap from the previous one.

## 10. Operations (not executed)

- Runbook [Part G](../../operations/production-runbook.md#part-g--finance-reminders-auto-003a): migration
  contents and verification.
- **Scheduler:** the Stage 1 job (`*/10 * * * 0,1` UTC) is unchanged. With Stage 1 only, Finance
  reminders are sent only for items whose 09:00-local window falls on Sunday/Monday UTC ticks. Daily
  reminders need **Stage 2** (AUTO-001 §18): a tick every day. Required cadence: at least one tick in
  every `[09:00 local, +24 h)` window per zone and around every quiet-hours end; recommended
  `*/10 * * * *` UTC (or 15–20 min after measuring Neon/Render cost). Deciding and activating Stage 2 is a
  separate operational step.

## 11. Risks / deferred

- **Stage 2 not active:** see §10.
- **Two devices in different zones:** quiet hours follow the last reported zone (AUTO-001 open question 5).
- **Past-due backlog:** occurrences already Due before the feature ships, or created after their date
  has passed by more than 24 h, are never reminded (by design: no spam, R-9).
- **Disabled after enqueue:** a reminder already enqueued is still delivered.
- **Retention:** executions/deliveries follow PD-5 (13 months); cleanup job still deferred.
- **Deferred improvements:** user-chosen reminder time; a reminder for still-unconfirmed occurrences a
  few days later; per-module notification categories/channels on Android; localization.

## Implementation status

Implemented on `feature/finance-reminders`; not merged, not deployed; Part G not executed.

**Changes to the AUTO-001/002 core:**

- `NotificationType.RecurringTransactionReminder`, `PlannedExpenseReminder` (copy, 24 h expiry,
  `NotificationCatalog.IsReminder`).
- `NotificationDispatcher` takes `INotificationPreferencesRepository` and defers reminders inside quiet
  hours; `INotificationDeliveryStore.CompleteDeferredAsync`.
- `LocalSchedule.ResolveDaily`.

**Files by layer:**

- Domain: `Notifications/QuietHours.cs`, `Notifications/NotificationPreferences.cs`, new enum values.
- Application: `Automation/LocalSchedule.cs`; `Notifications/NotificationPreferencesHandlers.cs`
  (port, Get/Set handlers), dispatcher, catalog, store port; `Finance/Reminders/`
  (`IFinanceReminderRepository`, `FinanceReminderSchedule`, `RecurringTransactionReminderHandler`,
  `PlannedExpenseReminderHandler`).
- Infrastructure: `Notifications/NotificationPreferencesRepository.cs`, `NotificationDeliveryStore`
  (deferral), `Finance/Reminders/FinanceReminderRepository.cs`,
  `Persistence/Configurations/NotificationPreferencesConfiguration.cs`, migration
  `AddNotificationPreferences`, DI.
- Contracts/API: `Notifications/NotificationPreferencesContracts.cs`,
  `Api/Notifications/NotificationPreferenceEndpoints.cs`, `Program.cs` (handlers registered only when
  the tick key is configured).
- App: `Services/Notifications/NotificationPreferencesApiClient.cs` (client + form),
  `Components/Pages/Settings.razor`, `NotificationTap` routes, `MauiProgram` registration.
