# ADR-010: One-off planned expenses and manual confirmation

## Status

Accepted — FIN-005

## Decision

FIN-005 is one-off Expense planning, not automatic recurring mode. This replaces
the tentative future FIN-005 direction in ADR-009; FIN-004 behavior is unchanged.

PlannedExpense is a user-owned entity with a calendar ScheduledDate and expected
amount. It has no accounting effect. Projected/Due derives from the validated
current client UTC offset and server TimeProvider, using the same local Today
approach as FIN-001/004. No scheduler exists. Only manual confirmation of Due
creates a normal Expense Transaction with existing Domain amount/currency rules.

Persist metadata in planned_expenses and explicit processing in
planned_expense_states. The second table materially simplifies atomic reopening:
an ownership-safe Transaction FK cascades only the confirmation state when an
ordinary Transaction is deleted. A surviving plan derives Due/Projected again.
There is no nullable composite SET NULL that could clear a required UserId,
custom database trigger, or special Transaction subtype. Unique expense and
Transaction links plus status/Transaction shape checks are final backstops.

Confirmation takes an owned expense FOR UPDATE inside SERIALIZABLE persistence,
validates owned account/category and commits Transaction plus state together.
Serialization/deadlock/expense-state uniqueness failures have up to five attempts.
Confirmed retries reveal the existing Transaction, even with a different payload.
Actual amount, note and occurred-at instant may differ from the expectation.
Confirmed metadata is immutable; edit actuals through ordinary Transaction flows.
Cancelled metadata must be restored before editing. Cancelled may restore;
Confirmed may not. Delete planning metadata in any state after explicit UI review;
actual Transactions always survive. Account/category FKs restrict deletion while
any plan remains, including Cancelled or Confirmed.

Budget reads actuals, recurring rules/states, and planned expenses/states through
a focused Finance planning snapshot port backed by one repeatable-read transaction.
It reuses existing recurrence projection rather than changing recurrence models.
ExpectedRecurringExpenses retains its meaning; ExpectedPlannedExpenses sums
unprocessed one-off expectations in their ScheduledDate's month for owner/currency.
ExpectedExpensesTotal is their sum. Spent and Remaining retain actual-only rules;
FreeToSpend subtracts total expected expenses, and SafeDailySpend uses that result.
Confirmation at equal amount in the scheduled month preserves FreeToSpend.
An amount difference changes it by the delta. If the actual month differs, the
scheduled expectation disappears and actual spending belongs only to the actual
Transaction's month. This can increase the scheduled month's FreeToSpend.

Finance has a distinct Planned expenses management page. Transactions combines
Recurring and One-off groups in one Planned area for its selected month, with
inline actions. Both one-off surfaces share their action component; recurring
and one-off review share confirmation fields and existing amount/time helpers.
Management queries are inclusive and capped at 3661 days; the App pages ten-year
ranges. Cancelled stays in management and leaves ordinary unresolved planning.
Home remains aggregate-only. Analytics and balances never consume planning.

## Consequences

The model is Expense-only with one logical occurrence per entity. All ownership
comes from authentication, and composite account/category/expense/Transaction
FKs reinforce isolation. The extra processing table follows FIN-004 rather than
introducing a parallel processing/deletion mechanism. No worker or external
dependency is introduced. Production migration must follow the full existing
chain; historical migrations are unchanged.
