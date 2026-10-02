# FIN-001 and FIN-002 phone acceptance

Use the local Development API and synthetic data. Apply `AddMonthlyBudgets`
with the existing development migration command before launching the API.
Follow [physical-device-debugging.md](physical-device-debugging.md) to build,
deploy and forward port 5050. Update the installed app without uninstalling
when checking local preference persistence.

## FIN-001

1. Sign in to a synthetic user whose default currency is EUR. Open Home.
   Verify the order: Portfolio, Monthly budget, Recent transactions.
2. With no current-month EUR budget, verify the compact Set budget CTA.
   Set 1500 EUR. Verify the current month, 0 spent, 1500 remaining, and
   1500 divided by the remaining calendar days **including today**.
3. Record current-month EUR Expenses totalling 620. Return to Home. Verify
   620 of 1500 spent, 880 remaining, progress, and 880 / remaining days.
   On the 10th of a 31-day month this is 40 EUR/day.
4. Record EUR Income, a same-currency Transfer, a USD Expense and an Expense
   outside the current month. Verify none changes EUR budget spending.
   An opening balance must not change spending either.
5. Edit the budget to 620. Verify zero remaining and zero safe daily spend.
   Edit to 600. Verify -20 remaining, a clear exceeded state and zero/day.
6. Try saving zero or invalid text. Verify the error and unchanged budget.
   Edit to a positive amount, restart the app, and verify it persists.
7. Remove the budget; cancel once, then confirm. Verify the CTA returns and
   transaction history is unchanged. Restart and verify it remains removed.
8. Sign in as a second synthetic user. Verify the first user's budget and
   spending are invisible. Set/edit/remove the second user's own EUR budget.
   Return to the first user and verify their budget is unchanged.
9. Where practical, repeat at the first/last day of a month: the divisor must
   be the number of days in the month / 1 respectively. Automated tests cover
   these boundaries, leap February and the server/device offset near midnight.

## FIN-002

1. On a device without the preference, portfolio amounts start visible.
   Tap Hide portfolio amounts on Home. Verify totals become neutral bullets,
   while currency codes, the budget, safe daily spend and recent transaction
   amounts remain visible. Tapping elsewhere on the card still opens Portfolio.
2. On Portfolio verify all currency totals and per-account amounts are hidden,
   including negative balances. Account names, types and currency codes remain.
3. Tap Show portfolio amounts. Verify actual totals and account balances return.
   Return to Home and verify the same visible state.
4. Hide amounts, force-stop and relaunch the app without clearing app data.
   Verify Home and Portfolio remain hidden. Show amounts and repeat the restart;
   verify the visible state persists too.
5. While hidden, open Transactions and Analytics. Verify their monetary values
   are unchanged. Budget values must remain visible throughout.
6. With TalkBack, verify the eye control announces Show portfolio amounts or
   Hide portfolio amounts as appropriate and can be activated independently
   of the Portfolio navigation link.

Portfolio hiding is a device-local presentation preference, shared by Home and
Portfolio. It is not encryption or an access-control feature. FIN-002 adds no
API, database change or migration; the budget migration belongs only to FIN-001.
