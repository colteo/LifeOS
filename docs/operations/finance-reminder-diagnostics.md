# Finance reminder: field checklist (AUTO-QA-001)

Use after **09:00 Europe/Rome on 15 October 2026**, and retain evidence on 16 October.
This is read-only: do not run a tick/test-notification endpoint, replay a cron request, edit
configuration or change Finance data to reproduce the problem. Credentials stay in secret stores.
No debugger is needed.

**Expected:** an unresolved recurring Income/Expense scheduled on 15 October becomes eligible at
**07:00Z = 09:00 CEST**, until **16 October 07:00Z**, exclusive. The reported production cadence
is every 10 minutes, every day. The first successful tick discovers/enqueues; Phase A of a later
tick sends. A 09:00 tick can therefore produce a push around 09:10, not necessarily at 09:00.
Cold starts, backlog, quiet hours and retries can add delay. This is a conditional Finance event,
not a daily check-in; no eligible item means no notification.

| Step | Read/check | Expected | If different |
|---|---|---|---|
| 1. Phone and item | Signed-in account, planned date/state, Settings → Notifications, Android app/channel permission. | Correct account; unresolved item on the 15th; recurring switch on; stored zone follows device. | Confirmed/Skipped recurring and Confirmed/Cancelled one-off items do not remind. Missing/invalid stored IANA zone prevents discovery. Do not edit the item during investigation. |
| 2. Scheduler history | Existing automation job history around 07:00Z: method, path, status, response, duration. Never display/copy secret header. | POST /api/internal/automation/tick, no body/query; daily runs about every 10 min. | Keepalive GET /health/live does not process reminders. No run means missing scheduler coverage. Timeout may still process work: inspect next tick and execution evidence. |
| 3. HTTP/API logs | Read HTTP result and matching automation log. | 200 with deliveries/executions/more counts; log: Automation tick: … . | 401: missing/wrong dedicated key; 400: body/query; 404: disabled endpoint/wrong route; 5xx/timeout: inspect later ticks. 200 skipped means guard skipped work. more=true means backlog. executions counts attempts; deliveries counts processed claims, including deferral/failure, not successful pushes. |
| 4. Execution/outbox | Operator checks owner's monthly key and linked delivery rows using read-only access below. | One FinanceRecurringReminder execution for rule-id:2026-10; Succeeded; one outbox row per device Active at enqueue. | No execution: check date/month range, state, zone, switch, tick window, backlog. Succeeded without delivery may mean NotApplicable after item/preference change or no active device; it does not prove a push was sent. |
| 5. Delivery/provider | Status, attempts, next attempt, expiry/error per device; sanitized FCM warnings if present. | Sent means FCM accepted. Pending inside quiet hours waits until local end (default 08:00). | Failed codes below. Sending may be in flight; abandoned lease is 5 min. FCM disabled leaves outbox Pending and Phase A does not process it. |
| 6. Android display | If Sent, inspect intended Release installation, LifeOS permission, channel lifeos_general, Do Not Disturb, connectivity. | A recurring transaction needs your confirmation; tap opens Transactions → Planned. | Sent is provider acceptance, not proof of receipt/display. Debug/Release use different Firebase projects. Do not send a real test notification in this investigation. |

## Operator appendix: privacy-safe read-only evidence

Use existing read-only access from [production runbook §3.3](production-runbook.md).
These are examples for an authorized operator; AUTO-QA-001 executes **none** against production.
Bind `@user_id`, `@automation_type`, `@occurrence_key` in your SQL client (or use local synthetic
values). Do not select names, amounts, emails, push tokens or credentials.

```sql
SELECT time_zone_id FROM users WHERE id = @user_id;
SELECT recurring_transaction_reminders_enabled, planned_expense_reminders_enabled,
       quiet_hours_start, quiet_hours_end
FROM notification_preferences WHERE user_id = @user_id;
-- No preferences row = both enabled, quiet hours 22:00–08:00.

SELECT id, automation_type, occurrence_key, time_zone_id, scheduled_for_utc,
       expires_at_utc, status, attempt_count, next_attempt_at_utc,
       lease_expires_at_utc, last_failure_code, created_at_utc, completed_at_utc
FROM automation_executions
WHERE user_id = @user_id AND automation_type = @automation_type
  AND occurrence_key = @occurrence_key;

SELECT d.id, d.device_registration_id, d.status, d.attempt_count,
       d.created_at_utc, d.next_attempt_at_utc, d.lease_expires_at_utc,
       d.expires_at_utc, d.sent_at_utc, d.last_error_code
FROM notification_deliveries AS d
JOIN automation_executions AS e ON e.id = d.source_execution_id AND e.user_id = d.user_id
WHERE e.user_id = @user_id AND e.automation_type = @automation_type
  AND e.occurrence_key = @occurrence_key;

SELECT id, status, inactive_reason, last_seen_at_utc
FROM device_registrations WHERE user_id = @user_id;
```

Execution keys: recurring `<rule-id>:2026-10`; planned `<expense-id>:2026-10-15`.
Read current Finance state in the app; it may differ from discovery-time state. Current device
status also does not establish whether it was Active at enqueue. Preserve sanitized timing/status
evidence. NotApplicable has no persisted reason, so Succeeded alone cannot always prove the cause.

| Evidence | Meaning / next read-only check |
|---|---|
| Execution FailedRetryable / Unhandled | Unexpected handler failure; retry at next_attempt_at_utc, at most 3 attempts (10/30 min delays). Exception text is not persisted. |
| Execution FailedFinal / Expired, MaxAttemptsReached, Permanent:… | Window/attempts exhausted, or invalid key/zone. No new reminder for the same key. |
| Delivery Pending / Transient | Provider/network failure; at most 5 sends with 10 min, 30 min, 1 h, 3 h delays, driven by ticks. Quiet-hours deferral consumes no send attempt. |
| Delivery Failed / Expired | No send starts at/after expiry, including expiry during preliminary reads. Delivery expiry is enqueue +24 h, distinct from discovery's due +24 h. |
| Delivery Failed / MaxAttempts | Five attempts exhausted. Other devices/users retry independently. |
| Delivery Failed / DeviceInactive | Signed out, permission denied, or no longer active for that owner. |
| Delivery Failed / TokenInvalid | Provider rejected device token/project pairing; registration becomes inactive. Check intended Debug/Release environment without exposing token. |
| Delivery Failed / Rejected | Non-retryable rejection; read sanitized HTTP/error-code warning and existing project/permission configuration. |

Disabling a preference **after enqueue does not cancel** that delivery. Execution re-reads Finance
before enqueue; dispatch does not re-check Confirmed/Skipped/Cancelled afterwards. No active device
at enqueue means zero deliveries; registering later does not backfill. Expired undiscovered items
leave no execution row. On 16 October after 09:00, an absent row cannot alone distinguish no
eligible item from no successful tick in the window.

See [coverage/validation report](../development/finance-reminder-auto-qa-001.md) for automated
evidence and documented DST limits. Record UTC and phone local time when escalating.
