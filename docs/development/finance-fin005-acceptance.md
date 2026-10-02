# FIN-005 phone acceptance

Use synthetic data on a development/test backend. Automated Android compilation
does not replace physical device acceptance. These checks remain pending.

1. Finance → Planned expenses. Create a one-off Expense with trimmed name,
   EUR account, Expense category, amount and future calendar date. Verify
   Projected and no confirmation action. No new bottom navigation or Home list.
2. Create items scheduled today and yesterday. Verify Due. Edit an unresolved
   item's name, account, category, amount, scheduled date and note. Check the
   new account's currency is authoritative. Income categories are unavailable.
3. Transactions → selected month. Verify one Planned area with distinct Recurring
   and One-off groups before Actual transactions. Move between months: both
   planning and history align; cancelled/confirmed rows are absent from planning.
4. Confirm a Due one-off directly in Transactions. Review name, account, category,
   expected amount/date; override actual amount, local date-time and note.
   Verify one ordinary actual Expense and the management Transaction link.
   Repeat from Planned expenses, including a past-month default local noon.
5. Invalid amount/date, excess precision and unavailable API show adjacent errors
   with recovered buttons. Correct and retry. Interrupt transport after confirming,
   retry and verify one actual Transaction. Tap Confirm twice quickly.
6. Cancel Projected and Due directly in Transactions. Verify no actuals, balance
   or analytics effect. Find Cancelled in management; Restore re-derives status.
   Editing requires restore. Confirmed cannot edit or restore through planning.
7. Budget 100, expected recurring 10 and expected one-off 20: Spent 0, Remaining
   100, expected total 30, free 70. Same-month confirmation at 20 preserves free;
   confirmation at 25 lowers it to 65. Daily safe spend uses free / remaining days.
8. Confirm an October plan using a November actual date. October loses that
   expectation; November owns spending. Check balances/analytics/history use
   the actual date exclusively. Ordinary actual edits do not rewrite planning.
9. Delete a confirmed actual Transaction. Its surviving plan reopens; confirm
   again. Delete planning metadata through explicit review: actual history survives.
   Test deleting unresolved and cancelled metadata through the same review step.
10. Delete an account/category referenced by a plan: clear conflict. Cancelled
    plans still restrict deletion; remove metadata first. Ordinary actual-history
    restrictions continue after planning deletion.
11. Another synthetic user cannot list/get/edit/process/delete or reference these
    items. Device local date near UTC midnight correctly determines Projected/Due.
12. Smoke-test FIN-001–004, Transaction CRUD, Home budget, Analytics, balances,
    Recurring Skip/Restore/confirmation, Gym and sign-in/out.
