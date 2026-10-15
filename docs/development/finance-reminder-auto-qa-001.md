# AUTO-QA-001 — Finance reminder reliability audit

Issue: [#42](https://github.com/colteo/LifeOS/issues/42). Branch:
`feature/auto-qa-finance-reminders`. Audit date: 14 October 2026.
Read AGENTS.md/CLAUDE.md, ADR-009/010/012, AUTO-001/002/003A, architecture dependency rules,
local development documentation, production runbook Part G and automation/notification code.
No production access, real notifications, deployment, scheduler changes or migration.

## Effective behavior

1. The external parameterless authenticated POST wakes the API. The 60-second singleton guard
   avoids overlapping/recent ticks on one instance; PostgreSQL uniqueness/fencing ensures correctness.
2. Phase A finalizes abandoned deliveries and dispatches existing rows; Phase B retries executions;
   Phase C discovers work. New reminder deliveries wait for a **later tick**. Counts report attempts/
   processed claims, not provider acceptance; `more=true` reports possible backlog. Caps are 25
   executions, 50 deliveries and a 20-second budget for new claims. Claimed work completes.
3. Discovery resolves the most recent daily 09:00 occurrence in each stored valid IANA zone, then
   SQL selects only items scheduled on that local date. Null/invalid zones schedule nothing. There
   is no past-due backlog reminder or daily check-in. Both monthly Income and Expense qualify.
4. Recurring identity is user + type + rule-id/year/month; requested days clamp to month-end and
   start/end months are inclusive. Confirmed/Skipped are excluded. Planned identity is user + type
   + expense-id/date; Confirmed/Cancelled are excluded. Discovery excludes existing execution keys
   and disabled per-type preferences. Execution re-reads the owned item/preferences; no longer
   applicable succeeds with no notification. Recurring moved into the future is no longer Due;
   planned rescheduled to any different date is inapplicable for the old key.
5. Fenced completion and one delivery per currently Active device commit in the same transaction.
   Finance remains read-only. Succeeded does not mean a push was sent. No active device means zero
   deliveries, with no later device-registration backfill. Current item changes after enqueue do
   not cancel deliveries; disabling a preference after enqueue also does not cancel them.
6. Execution window is `[due, due+24h)`, up to 3 attempts with 10/30-minute delays. Delivery window
   is independently enqueue+24h, with up to 5 sends and 10-minute/30-minute/1-hour/3-hour backoff.
   Retry times are earliest eligibility: only a later tick actually performs work.
7. Reminder dispatch uses the user's current zone and quiet hours (default 22:00–08:00). Deferral
   returns the claim's attempt and waits for the end; if end is at/after delivery expiry it fails
   Expired. Weekly Review/Test bypass quiet hours. With no FCM sender, Phase A leaves rows alone.
8. FCM acceptance records Sent, not a device receipt/display acknowledgement. Token invalidation,
   sign-out and installation reassignment preserve user isolation. A crash after acceptance before
   recording Sent may resend; Android's stable notification tag replaces the visible notification.

### Rome and DST

15 October 2026 09:00 CEST = **07:00Z**. Discovery window ends 16 October **07:00Z**, exclusive.
At the reported daily 10-minute cadence a tick at 09:00 can enqueue and a tick at 09:10 can send;
cold starts/backlog/retries/quiet hours can delay this. The physical-phone test remains user-owned.

29 March 2026 09:00 CEST = 07:00Z; 25 October 2026 09:00 CET = 08:00Z. UTC expiry always adds
24 elapsed hours. AUTO-003A explicitly chooses the most recent date: spring consecutive due times
are 23h apart (the older discovery candidate is superseded); fall times are 25h apart (one hour with
no open candidate). These documented policies are unchanged, not a promise of catch-up of every
item for every outage strictly shorter than 24h across DST. Gap local times shift by gap length;
ambiguous due times use the first occurrence. Quiet-hour ends have their own first-allowed-instant
semantics, including gap/overlap ends. Existing tests already cover those policies.

## Coverage map (existing evidence and additions)

Paths below are relative to `tests/dotnet`. Existing coverage is retained, not duplicated.

| Invariant | Existing automated evidence | AUTO-QA-001 addition |
|---|---|---|
| Local 09:00/UTC, no pre-due, DST, exclusive window | Unit: Automation/LocalScheduleTests (normal, previous-date, 23/25h days, gap/ambiguous time); Finance/Reminders/FinanceReminderTests (pre-due recurring) | Both types through tick at -1s, due, +10min, +12h, +23:59:59, +24h, +25h; PostgreSQL handlers/discovery at -1s/due/+23:59:59/+24h on 15 Oct and both DST transition dates |
| Delivery expiry independent of execution | Unit: Notifications/NotificationDispatchTests, Automation/RunAutomationTickTests (expiry/retry); PostgreSQL: NotificationDeliveryPersistenceTests | Late Finance discovery preserves enqueue+24h and expires without a send; retry exactly at execution expiry does not read or enqueue; pre-send expiry during preliminary read |
| Idempotency/concurrency/fencing | Unit: FinanceReminderTests, RunAutomationTickTests; PostgreSQL: AutomationExecutionPersistenceTests and NotificationDeliveryPersistenceTests (concurrent claims/completion, rollback/fan-out); FinanceReminderPersistenceTests (repeat ticks) | Recurring edited day does not repeat same month; planned new date can remind once, reverting never repeats old date; corresponding real SQL checks |
| Confirmed/Skipped/Cancelled, range/clamping, dates | Unit: FinanceReminderTests; PostgreSQL: FinanceReminderPersistenceTests (all processed states, start/end, late-day clamp, Income/Expense) | Confirmation between failed attempt and retry; deleted items after discovery |
| Per-type preferences/ownership | Unit: FinanceReminderTests, Notifications/NotificationPreferencesTests; PostgreSQL: FinanceReminderPersistenceTests; HTTP preference tests | Wrong-owner item key returns inapplicable for both types; disabled-after-enqueue still dispatches; two users in the same zone with inverse preferences |
| Quiet hours/DST/retry | Unit: Notifications/QuietHoursTests, NotificationDispatchTests (boundaries, wrap, gap/overlap end, deferred retries); PostgreSQL: FinanceReminderPersistenceTests (fenced deferral, end-to-end) | Dispatcher deferral across spring/fall to 08:00 with new UTC offset; two users' quiet hours and transient delivery outcomes remain independent |
| Failure codes/device isolation/provider | Unit: NotificationDispatchTests, FcmPushNotificationSenderTests, DeviceRegistrationTests; PostgreSQL: NotificationDeliveryPersistenceTests | Regression reproducer for both reminder types expiring during preference read |
| Endpoint/auth/wiring/layers | HTTP: AutomationTickHttpTests (key, body/query, guards, handler registration); Architecture: AutomationArchitectureTests | Existing suites run unchanged in final gate |
| Android permission/channel/tap | Unit: App/FinanceReminderAppTests and notification App tests; code inspection of AndroidPushPlatform/LifeOSFirebaseMessagingService | Read-only field checklist; no physical display claim or real send |

New tests: 20 Unit cases and 14 PostgreSQL cases (34 total). The initial targeted Unit run reproduced
two failing expiry cases; all other 282 selected tests passed. After the fix, the focused Finance/
dispatcher set passed 69/69; relevant HTTP/PostgreSQL set passed 76/76, using a disposable
Testcontainers PostgreSQL image and fake senders/HTTP handlers.

## Defect and minimal correction

**Delivery could start after expiry.** Claim checked expiry, but awaited device/preference reads
could cross it. Dispatcher then called the provider without checking time again. A deterministic
clock-advancing preference repository reproduced this for both Finance reminder types. An eight-line
guard immediately before sending now records fenced Failed/Expired and does not call the sender.
No persistence/schema/contract/policy change. This guards the start of the sender invocation; an
already-started network operation and provider/device queues are not an end-to-end display deadline.

The first full Unit gate also exposed a baseline stale release assertion: commit `5f1ffbc` had
already bumped App to 1.6.1/16001, while DiagnosticsAppTests still expected 1.6.0/16000. Only the
test name and exact expected values were aligned; no App runtime/version change or weaker assertion.

## Validation and operational handoff

Final gate completed successfully on 14 October 2026 after fixing the stale release test:

| Check | Final result |
|---|---|
| API Debug build (affected backend and dependencies) | Passed; 0 warnings, 0 errors. |
| Full Unit suite | 2,268 passed; 0 failed/skipped. |
| Full Integration suite | 821 passed; 0 failed; 1 optional live AI smoke skipped; final duration 5m40s. |
| Full Architecture suite | 85 passed; 0 failed/skipped. |
| git diff --check | Passed; no whitespace errors (Windows line-ending conversion warnings only). |

The first complete Integration run also passed (821 + 1 skipped, 6m40s). After the initial Unit
failure, only the stale test was rerun until green, followed by one final complete regression gate.
No blocking issue remains. TRX artifacts are local/ignored under each test project's TestResults
directory. Backend projects build through the test project builds. Android
build is not required: no App/Android code or Contracts/shared client code changed; the App release
test reads the existing project file. EF model/migrations are unchanged, so no EF migration tooling
or pending-model CLI check is required (existing PostgreSQL model/schema tests still run).

Reproduction commands (repository root, Docker Desktop running):

```powershell
dotnet build src/dotnet/LifeOS.Api/LifeOS.Api.csproj --no-restore
dotnet test tests/dotnet/LifeOS.UnitTests/LifeOS.UnitTests.csproj --no-restore --no-build
dotnet test tests/dotnet/LifeOS.IntegrationTests/LifeOS.IntegrationTests.csproj --no-restore --no-build
dotnet test tests/dotnet/LifeOS.ArchitectureTests/LifeOS.ArchitectureTests.csproj --no-restore --no-build
git diff --check
```

Use `--no-restore`/`--no-build` only after a successful restore/build. First-time checkouts run the
same test commands without those flags. The only skipped integration case is the optional
`ActionAgentLiveSmokeTests.TheRealModel_ReadsThroughDotNetTools_AndEndsWithAValidTerminalDecision`;
its live provider environment is deliberately not configured for this offline task.

The standard sandbox terminal initially failed to start; the alternate local runtime could read
code but NuGet lacked Windows special-folder paths. Running approved local test commands outside
that restriction resolved restore/build. Docker Desktop was started locally for disposable test
containers. No tools/packages were added to the project.

Operational guide: [field checklist](../operations/finance-reminder-diagnostics.md), linked from
[production runbook Part G4](../operations/production-runbook.md#g4-read-only-missing-notification-diagnostics).
It distinguishes scheduler/HTTP, tick processing, execution/outbox, FCM acceptance and Android
display, with privacy-safe query examples; no production query is executed by this task.

## Changed files

| File | Purpose |
|---|---|
| `src/dotnet/LifeOS.Application/Notifications/NotificationDispatcher.cs` | Re-check expiry before invoking push sender. |
| `tests/dotnet/LifeOS.UnitTests/Finance/Reminders/FinanceReminderTests.cs` | 15 boundary/lifecycle/ownership cases through handlers/tick. |
| `tests/dotnet/LifeOS.UnitTests/Notifications/NotificationDispatchTests.cs` | 5 expiry/DST/user-isolation cases; local clock-advancing repository decorator. |
| `tests/dotnet/LifeOS.IntegrationTests/PostgreSql/FinanceReminderPersistenceTests.cs` | 14 real SQL temporal/preference/date-identity cases. |
| `tests/dotnet/LifeOS.UnitTests/App/DiagnosticsAppTests.cs` | Align stale release assertions to existing 1.6.1/16001. |
| `docs/operations/finance-reminder-diagnostics.md` | Field checklist and operator read-only appendix. |
| `docs/operations/production-runbook.md` | Link checklist from Part G4. |
| `docs/development/finance-reminder-auto-qa-001.md` | Effective behavior, coverage map, defect evidence, validation and handoff. |

## Deferred improvements

- Persisting a stable NotApplicable reason would improve historical diagnostics, but requires a
  separate policy/schema decision; Succeeded alone cannot distinguish a skipped condition from
  zero-device fan-out after the fact.
- Physical-phone receipt/display and provider queue timing remain the user's separate test.
- Documented product choices remain: most-recent-date DST behavior, no old backlog/backfill,
  no cancellation after enqueue, last reported device zone wins, cleanup implementation deferred.
  No broader scheduler, observability, App or notification-product refactor was introduced.
