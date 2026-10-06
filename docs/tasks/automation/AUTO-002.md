# AUTO-002 — Weekly Review v1

Status: IMPLEMENTED on branch `feature/weekly-review-automation`. Not merged, not deployed. The
Production activation (migration, `Automation__TickKey`, Stage 1 tick job) is manual and happens
after merge; it is described in the [runbook Part F](../../operations/production-runbook.md#part-f--weekly-review-and-the-automation-tick-auto-002).

Builds on [AUTO-001](AUTO-001.md) (§3, §8, §14, §18, §23) and
[ADR-012](../../adr/0012-server-side-automation-and-push-notifications.md). No new ADR: every
decision below applies ADR-012 inside one module.

---

## 1. Product decisions (closed, given)

| # | Decision |
|---|---|
| W-1 | **No AI** in v1. AI commentary is AUTO-002.1. |
| W-2 | Schedule **Sunday 20:00** in the user's stored IANA zone, via `LocalSchedule.ResolveWeekly`. Lateness **24 h**. Occurrence key = local week-ending Sunday date (`2026-10-04`). |
| W-3 | Period = local **Monday–Sunday**. The saved review holds the data available at generation time. |
| W-4 | Read-only towards Finance, Gym and Nutrition. **Nutrition lazy close is never triggered.** |
| W-5 | Saved review = **immutable, deterministic, versioned snapshot**. Later source edits never rewrite it. |
| W-6 | At most one review per user and week: `UNIQUE (user_id, week_end_date)`. |
| W-7 | **Enabled by default**, module-owned setting. |
| W-8 | After a new review is saved, `WeeklyReviewReady` is enqueued through the existing outbox. Fixed English copy, no personal data. |
| W-9 | Tap `weekly_review` opens that review, through the AUTO-001 tap machinery. |
| W-10 | Code prepares Stage 1 ticks; no scheduler job or Production variable is created by this branch. |

## 2. Implementation decisions

| # | Decision | Why |
|---|---|---|
| D-1 | Module namespace `WeeklyReviews` in Domain, Application, Infrastructure, Contracts, Api, App. | Same shape as Finance/Gym/Nutrition. |
| D-2 | **Enabled setting = `weekly_review_settings` table**, one optional row per user (`user_id` PK). **No row ⇒ enabled.** | Module-owned (AUTO-001 §14 approach B), default-on without a backfill, no column on `users`, no generic preferences framework. |
| D-3 | **Snapshot = typed Domain records (`WeeklyReviewSnapshot`) stored as `jsonb`**, with `data_version` (1). Infrastructure serializes with fixed `System.Text.Json` options; reading dispatches on `data_version` and rejects unknown versions. | The repository had no JSON persistence convention. A typed document keeps the report immutable and self-contained (ADR-008 idea) without 4–5 snapshot tables for a read-only report. Versioning is explicit and per row. |
| D-4 | The `weekly_reviews` table is mapped by an Infrastructure persistence record (`WeeklyReviewRecord`), not by the Domain entity. | The version-aware document read needs the `data_version` column; an EF value converter cannot see it. Domain stays free of persistence concerns. |
| D-5 | **Artifact in the completion transaction.** `AutomationResult.Succeeded` gains an optional `saveArtifact` delegate. `RunAutomationTick` runs, in one `IUnitOfWork` transaction: fenced completion → `saveArtifact` → notification fan-out. `saveArtifact` returning false rolls everything back. | AUTO-001 WP3A explicitly reserved this ("AUTO-002 adds its artifact write to that same work delegate"). Smallest change; the core still knows no module. |
| D-6 | Review insert = `INSERT … ON CONFLICT (user_id, week_end_date) DO NOTHING`. A conflict returns false → rollback → the attempt stays Running → lease takeover → the next attempt finds the existing review and succeeds **without** a notification. | No duplicate review, no duplicate logical notification, self-healing. |
| D-7 | Coverage of Sunday: the **full local week** `[Mon 00:00, next Mon 00:00)` is the period; the content is whatever exists when the snapshot is taken. A run at Sunday 20:00 therefore contains nothing logged later; a late run (≤ 24 h) contains what was logged by then for that week. Monday data is never included. | Matches W-3 literally ("data available at generation time"), avoids a second, artificial cut-off instant. |
| D-8 | Local week bounds → UTC via the AUTO-001 DST rules (`LocalSchedule.ToUtc`, now public). | DST weeks have 167/169 hours; never `now − 7 days` (AUTO-001 §6). |
| D-9 | The zone used for a review is the **execution's** `time_zone_id` (the zone the occurrence was resolved with), not the user's current zone. | An occurrence is self-consistent even if the device zone changes between claim and retry. |
| D-10 | Entry point: one row **"Weekly Review"** in the Modules directory (`/more`). The bottom dock is unchanged. | The Modules page is the directory every module gets one entry in; least intrusive. |
| D-11 | Enabled toggle lives on the Weekly Review list page. | The only place the feature is managed; no Settings redesign. |
| D-12 | Paging: keyset cursor = the last item's week-ending date (`yyyy-MM-dd`), `limit` 1–50 (default 20), like Gym history. | Existing convention; a user has one review per week, so the date is a unique key. |

## 3. Weekly data (read-only, reused calculations)

| Section | Source (reused) | Content |
|---|---|---|
| Finance | `GetMonthlyAnalyticsHandler` (accepts any half-open UTC range ≤ 32 days) → `MonthlyAnalyticsCalculator` | Per currency: expenses, income, net flow (transfers excluded, currencies never mixed), top-level expense categories with current names, sorted as Analytics. |
| Gym | `GetWorkoutHistoryHandler` (keyset pages of **Completed** sessions, newest first) | Completed workouts with `CompletedAtUtc` in the week: count, total duration (seconds), completed/prescribed sets; per workout: local date, workout and program names (as snapshotted at start), duration, sets. |
| Nutrition | `DailyNutritionSummary.From` (NUT-002 rules) over a new read-only `IMealNutritionRepository.GetDaysAsync(from, to)` (one query instead of 7) | Days with meals, meal count, analyzed meal count, fully analyzed days, sums of **analyzed meals only**, and per day: meals, analyzed meals, kcal/macros. Never estimated, never lazily closed: unanalyzed meals stay counted as unanalyzed. |

### Omitted from v1 (documented)

- **Budget context.** Budgets are monthly; a week can span two months and the budget handler
  needs a device offset and recurring/planned projections. A weekly budget figure would need new
  financial rules → omitted.
- **Transaction count, subcategories.** Not in the analytics result; not worth a new query.
- **Nutrition targets / adherence.** Target resolution per day exists, but "adherence" on partial
  data would be invented analytics → omitted.
- **Exercise-level detail** (volume, PRs): no existing weekly calculation.

## 4. Data model (migration `AddWeeklyReviews`)

```text
weekly_reviews
- id                uuid          PK (UUID v7)
- user_id           uuid          → users(id) ON DELETE CASCADE
- week_start_date   date          local Monday
- week_end_date     date          local Sunday (= occurrence key)
- time_zone_id      varchar(64)   zone the week was resolved with
- generated_at_utc  timestamptz
- data_version      integer       1
- snapshot          jsonb         WeeklyReviewSnapshot v1

ux_weekly_reviews_user_week_end   UNIQUE (user_id, week_end_date)   (also the list index)
ck_weekly_reviews_week            week_end_date = week_start_date + 6 AND ISODOW(week_end_date) = 7
ck_weekly_reviews_data_version    data_version >= 1
ck_weekly_reviews_snapshot        jsonb_typeof(snapshot) = 'object'

weekly_review_settings
- user_id           uuid          PK → users(id) ON DELETE CASCADE
- enabled           boolean
- updated_at_utc    timestamptz
```

User deletion cascades (ADR-006 ownership; same as executions and deliveries). Down drops both
tables.

## 5. Snapshot v1 (`data_version = 1`)

```json
{
  "finance": { "currencies": [
    { "currency": "EUR", "expenses": 120.50, "income": 0, "netFlow": -120.50,
      "expenseCategories": [ { "name": "Food", "amount": 80.00 } ] } ] },
  "gym": { "completedWorkouts": 2, "totalDurationSeconds": 7200, "completedSets": 30, "prescribedSets": 32,
    "workouts": [ { "date": "2026-09-29", "workoutName": "Upper A", "programName": "Base",
                    "durationSeconds": 3600, "completedSets": 15, "prescribedSets": 16 } ] },
  "nutrition": { "daysWithMeals": 3, "mealCount": 7, "analyzedMealCount": 5, "fullyAnalyzedDays": 2,
    "analyzedCaloriesKcal": 4200.0, "analyzedProteinGrams": 210.0, "analyzedCarbsGrams": 400.0,
    "analyzedFatGrams": 150.0,
    "days": [ { "date": "2026-09-29", "mealCount": 3, "analyzedMealCount": 3, "caloriesKcal": 2100.0,
                "proteinGrams": 105.0, "carbsGrams": 200.0, "fatGrams": 75.0 } ] }
}
```

Determinism: every list is ordered (currencies ordinal; categories as Analytics; workouts by
completion time then id; days by date); no generation-time values inside the document; fixed
serializer options. A later `data_version` adds a new reader; v1 rows are never rewritten.

## 6. Automation handler

`WeeklyReviewAutomationHandler` (`Application/WeeklyReviews`), registered with the automation core
(only when `Automation__TickKey` is configured).

**FindDue(now, limit)**

1. Distinct `users.time_zone_id` values (≤ a few hundred).
2. Keep valid IANA zones (`SetTimeZoneHandler.TryNormalizeIanaTimeZone`); invalid stored values are
   skipped (no row, nothing scheduled).
3. Open zones: `LocalSchedule.ResolveWeekly(zone, now, Sunday, 20:00, 24 h)`; key = local date.
4. One query: users in open zones, **not disabled**, with no `WeeklyReview` execution for that key
   and no review for that week; `ORDER BY id LIMIT limit` (ids only).

**Execute(occurrence)** (outside any transaction, read-only)

1. Key must parse as a Sunday, zone must be valid IANA → otherwise `PermanentFailure`
   (`InvalidOccurrenceKey` / `InvalidTimeZone`).
2. Disabled since discovery → `NotApplicable` (Succeeded, no artifact, no push).
3. Review for that week already exists → `Succeeded(existing id)` with **no** notification.
4. Build the snapshot (§3), create the `WeeklyReview`, return
   `Succeeded(review.Id, WeeklyReviewReady → weekly_review/<id>, saveArtifact: insert)`.

Unexpected exceptions become the engine's retryable `Unhandled` (no text stored). Retries,
leases, expiry and max attempts are AUTO-001's (3 attempts, 10/30 min, `expires = due + 24 h`).

## 7. API

| Endpoint | Result |
|---|---|
| `GET /api/weekly-reviews?limit=&cursor=` | `200 { items: [{ id, weekStartDate, weekEndDate, generatedAtUtc }], nextCursor }`, newest week first; `400` bad limit/cursor |
| `GET /api/weekly-reviews/{id}` | `200` review (dates, zone, generatedAtUtc, dataVersion, finance, gym, nutrition); `404` for missing or another user's |
| `GET /api/weekly-reviews/settings` | `200 { enabled }` (`true` when never set) |
| `PUT /api/weekly-reviews/settings` `{ enabled }` | `204`; `400` without `enabled`; `404` missing user |

All require the user access token; the owner comes only from the token. No execution metadata is
exposed.

## 8. Notification

`WeeklyReviewReady` (modeled by AUTO-001): title `LifeOS`, body `Your weekly review is ready`,
data `{ type: "weekly_review", id: <review id> }`, 24 h expiry, key `automation:<execution id>`,
one delivery per Active device, enqueued in the completion transaction. Never FCM directly.

App: `NotificationTap.PathFor(WeeklyReview) = weekly-reviews/<id>`; `AuthGate` opens it once,
signed in and onboarded. An id of another user shows the page's not-found error (API 404).

## 9. Operations (Stage 1)

Documented in runbook Part F, **not executed**: deploy with the migration, set
`Automation__TickKey` (Secret), create the separate `lifeos-api automation tick` job
`POST /api/internal/automation/tick`, header `X-LifeOS-Automation-Key`, cron `*/10 * * * 0,1`
in **UTC**. The keepalive jobs stay unchanged (PD-8, S19).

## 10. Risks / deferred

- AUTO-002.1: AI commentary.
- Budget context, nutrition target adherence, exercise-level weekly metrics (§3).
- A device registered after the review gets no push for it (AUTO-001 §11).
- Two devices in different zones: last writer wins (AUTO-001 open question 5).
- Retention of `weekly_reviews` is not defined by PD-5 (that covers executions/deliveries);
  reviews are user data and stay until the user is deleted.
- Stage 1 Neon/Render cost to be measured after activation (AUTO-001 open question 1).

## Implementation status

Implemented on `feature/weekly-review-automation`; not merged, not deployed; Part F not executed.

**Changes to the AUTO-001 core** (the only ones):

- `AutomationResult.Succeeded(resultId, notification, saveArtifact)` and its use in
  `RunAutomationTick` (fenced completion → artifact → deliveries, one transaction).
- `LocalSchedule.ToUtc` made public (was the private `ResolveLocalToUtc`).
- `NotificationTap.PathFor(WeeklyReview)` → `weekly-reviews/<id>`.

**Module files**

- Domain: `WeeklyReviews/WeeklyReview.cs` (+ `WeeklyReviewSettings`), `WeeklyReviewSnapshot.cs`.
- Application: `WeeklyReviews/` — `IWeeklyReviewRepository`, `WeeklyReviewPeriod`,
  `WeeklyReviewSnapshotBuilder`, `WeeklyReviewAutomationHandler`, `WeeklyReviewHandlers`;
  Nutrition port gains the read-only `IMealNutritionRepository.GetDaysAsync`.
- Infrastructure: `WeeklyReviews/` — `WeeklyReviewRepository`, `WeeklyReviewRecord`,
  `WeeklyReviewSnapshotJson`; `Configurations/WeeklyReviewConfiguration.cs`; migration
  `AddWeeklyReviews`; `MealNutritionRepository.GetDaysAsync`.
- Contracts/API: `WeeklyReviews/WeeklyReviewContracts.cs`, `WeeklyReviewEndpoints.cs`; handler
  registration in `Program.cs` (the automation handler only when the tick key is configured).
- App: `Services/WeeklyReviews/` (API client, display), `Pages/WeeklyReviews/` (list, detail),
  Modules entry, tap route.

**Verification still manual** (after deploy, runbook Part F): first real Sunday run, push on a
physical phone, tap from closed/running app, Stage 1 cost.
