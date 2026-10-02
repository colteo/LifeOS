# ADR-009: Monthly recurring planning and manual confirmation

## Status

Accepted — FIN-004

## Decision

Recurring rules are planning data, separate from financial history. Monthly
Income/Expense rules describe an expected positive numeric(19,4) amount, owned
account/category, day 1–31 and immutable start month. Account currency remains
authoritative. Transfers and other recurrence patterns are outside this version.

Occurrences are derived on demand. Identity is **RuleId + Year + Month**, never
scheduled date. Requested days clamp to the last day of short months, including
leap February. Months before the start are absent. Unprocessed dates on/before
the user's local Today are Due; later dates are Projected. As in FIN-001, Today
uses server TimeProvider with the validated current device UTC offset. Calendar
dates never assume the UTC date equals local Today. Query ranges are inclusive
and capped at 120 months; the client pages older history and includes the next
three months. The cap is an Application transport limit, not a Domain lifetime.

Only user actions persist a state: Confirmed or Skipped. PostgreSQL enforces one
state per logical month. A state preserves the scheduled date at processing;
edits to the rule cannot create another occurrence for that month. Restoring a
skip removes its state and re-derives against the current rule and local Today.

Only manual confirmation of Due creates a normal Transaction. Review may change
actual amount, note and occurred-at instant using ordinary Transaction semantics.
Changing one actual Transaction does not change the rule. Confirmed retries
return the original linked Transaction, even if the retried review payload differs;
editing that result uses the ordinary Transaction edit flow. Skipped months cannot
be confirmed without explicit restoration. Projected months cannot be confirmed.

Application decides against a focused persistence snapshot. Infrastructure uses
SERIALIZABLE transactions and an owned rule FOR UPDATE, with at most five retries
for serialization/deadlock or the precise logical-month uniqueness collision.
Owned account/category validation, state check, Transaction and Confirmed state
commit atomically. The unique logical month is the database backstop. Ownership
is enforced by composite FKs to account, category, rule and actual Transaction.
No background worker, scheduler, queue or external process generates occurrences.

## Optional final calendar month (FIN-004 acceptance amendment)

Rules support nullable EndYear/EndMonth. Both are absent for **No end**, or both
are valid and the end is at/after the immutable start. Year + month comparison
defines the inclusive range: StartMonth <= logical month <= EndMonth. The final
month still clamps the requested day normally; later months have no occurrence,
not a synthetic Skipped state. Domain scheduling and Application projection use
the same range predicate, so budget expectations also stop after the end.

Editing the end changes planning only. Keep existing Confirmed and Skipped states
unchanged, including states outside a shortened range; projection hides all such
months. This preserves audit linkage without a second history model or cleanup
policy. Extending the range reveals the original processed states, preventing
another confirmation. A successful confirmation retry still returns its original
Transaction even when its logical month is now outside the range. Other actions
outside the range are rejected. Deleting an actual Transaction still removes its
state atomically, but its month only derives again if inside the current range.

Create/edit shows an optional End month with an explicit No end checkbox. The
Transactions screen has a separate Recurring planning section for the selected
month, showing only unprocessed Due/Projected occurrences from the bounded API
query. Due offers inline Review & Confirm and Skip; Projected offers Skip only.
Both pages share the same occurrence action component and App flow helper, calling
the existing recurring API. Review shows name/type/account/category, expected amount
and scheduled date, with editable actual amount, local date/time and note. Validation
and API errors appear beside the active review or action; submission state resets
in finally, and malformed responses are readable errors. Successful actions reload
both selected-month history and planning in Transactions. Recurring remains the
rule configuration, broader forecast, Skipped/Restore and processed inspection
surface; navigation there is not required to confirm or skip. Actual history remains separate;
Confirmed appears there solely through its real Transaction. Skipped and months
outside the configured range do not appear in that planning section.

The local date/time input accepts invariant HTML minute, second and fractional-second
forms. Its default is the scheduled date at local noon, including past months. Convert
using the entered date's timezone offset; utcOffsetMinutes separately communicates
the current local Today for status derivation. No Domain recurrence semantics or
schema change is required by this App correction.

## Budget formulas

Spent and Remaining preserve FIN-001: actual Expense Transactions and Amount -
Spent. ExpectedRecurringExpenses includes only unprocessed Due/Projected Expense
occurrences in the owner's selected month and account currency. Confirmed and
Skipped are excluded. FreeToSpend = Amount - Spent - ExpectedRecurringExpenses.
Current-month SafeDailySpend is max(0, FreeToSpend) divided by remaining calendar
days including today, with decimal precision and rounding only for display.

The budget reads actual movements and planning states from one repeatable-read
snapshot. Confirmation cannot make a response mix its before/after representations.
When the actual Transaction falls in the occurrence's budget month and matches
the expected amount, FreeToSpend is unchanged; a different amount changes it by
the difference. An explicitly chosen different transaction month retains normal
Transaction semantics: spending belongs to that actual month.

Analytics, account balances and transaction history continue querying actual
Transactions only. No recurrence input enters Analytics or AccountBalanceCalculator.

## Deletion

Account/category FKs use RESTRICT. Referencing rules block their deletion and
the API reports a conflict, including a race after an earlier lookup. After
deleting a rule, ordinary history-based restrictions still apply.

Rule deletion cascades only planning states, preserving actual Transactions.
Actual Transaction deletion cascades only its linked occurrence state in the
same PostgreSQL statement. If the rule remains and its range includes the month,
the logical month derives again
(normally Due; Projected if local Today or the rule's day has moved earlier/later).
No Confirmed state may point to a deleted Transaction. Editing actual history
does not change the rule, state identity or future projections.

## Consequences

Planning has no account-balance, analytics or spending effect until confirmation.
Missed months need no scheduler. The initial UI includes a bounded ten-year page
with older-page navigation; rules retain their full lifetime. Snapshot queries
load the owner's Finance data for personal-app volumes.

FIN-005 may reuse rules, logical-month identity, clamping and Transaction linkage
for explicit automatic mode. No speculative automatic-mode columns are introduced.
Its policy, opt-in, idempotency and scheduling remain deferred. A parallel migration
merged into main requires removing the unpublished FIN-004 migration and regenerating
on the combined model; divergent EF snapshots must never be hand-merged.
