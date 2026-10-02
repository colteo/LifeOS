# FIN-004 phone acceptance

Use synthetic accounts and categories on a development/test backend. English UI.

1. Finance → Recurring → New recurring item. Create Expense “Bank fee”, EUR account,
   matching Expense category, amount 1, day 31, current start month. Verify short-month
   guidance, date clamping and Due/Projected labels. Home has no full recurring list.
2. Create a rule beginning two months ago; verify unresolved past months are Due.
   Use Earlier months for older history and Current and upcoming to return.
3. Review a Due occurrence. Verify type/account/category/expected amount/scheduled
   date; change actual amount, note and local date/time. Confirm. Open its actual
   Transaction and verify history, analytics and balance. Future dates offer only Skip.
4. Skip Due and Projected months; verify no Transaction or balance effect. Restore
   both; verify Due/Projected derives again. Confirmed offers the actual Transaction.
5. Edit a rule's day and amount after processing a month. Verify that month retains
   its processed status/date and later unprocessed months use the changes.
6. With budget 1500, spent 620 and expected expenses 80, verify Remaining 880 and
   FreeToSpend 800. Confirm expected 20 at actual 20 in the same month: Spent 640,
   expected 60, free 800. Use actual 25 in a separate case: free 795. Daily figure
   divides free by remaining calendar days including today.
7. Edit a confirmed Transaction; verify rule/future expectations are unchanged.
   Delete it; verify the logical month reopens and can be confirmed again or skipped.
8. Try deleting an account/category referenced by a rule. Verify clear conflict.
   Delete the rule through its review step; actual Transactions survive and ordinary
   account/category deletion restrictions still apply.
9. Repeat confirmation after a transport interruption. Verify the same Transaction
   reference and no duplicate in history. Sign in as another test user: no foreign
   recurring rules, states or resource choices appear.

10. Create “Car installment”, start October 2026, end May 2027, day 31. Verify
    May 31 exists and June has no occurrence in Recurring or Transactions →
    Recurring planning. February clamps to its last day. June budget expectations
    exclude this rule. Create with No end and verify later months remain planned.
11. Reject an end before start; accept end equal to start and December–January.
    Confirm a Due month, then shorten the end before it. Its actual Transaction
    remains in history and analytics/balance; the planning month disappears.
    Extend the end again: the same Confirmed link returns without duplication.
    Repeat with a Skipped month: its Skipped status returns. Delete the actual
    Transaction while outside the range: no plan reappears until range extension.
12. In Transactions, move between final and following month. Recurring planning
    shows Due/Projected expectations separately from history, with a Manage
    recurring plans link. Confirm or skip via Recurring and return: the expected
    movement disappears; only a confirmed real Transaction appears in history.

Physical-device acceptance must be performed by the user; automated validation
includes Android Debug compilation, HTTP transport/auth and PostgreSQL concurrency.
