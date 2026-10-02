# FIN-003 phone acceptance

Use synthetic accounts and a development API with the reconciliation migration applied.

1. On Accounts, create a EUR Checking account with an opening balance of 1237.50 at a past instant. Open its action panel and select Reconcile balance.
2. Enter 1250.00 and a note. Verify the preview is +12.50 EUR. Submit once; while saving, inputs and duplicate submission are disabled. The panel closes and the account immediately shows 1250.00 EUR.
3. Reconcile to 1200.00. Verify the negative difference, then the corrected balance. Reconcile to 1200.00 again; it succeeds without a zero adjustment.
4. Reconcile a credit account to a negative observed balance. Try both dot and comma decimal input. Invalid text must prevent submission.
5. Cancel a reconciliation and verify no change. After a transient request failure, retry unchanged input; verify it applies only once. Changing the input starts a new submission.
6. Check Transactions, monthly analytics and monthly budget spending before and after reconciliation: all remain unchanged. The opening balance remains unchanged.
7. Attempt to delete a reconciled account, including one with only a zero-difference reconciliation. Verify the audit-history conflict and that the account remains available.
8. Enable portfolio privacy, restart the app and check Home and Portfolio: sensitive amounts remain hidden. Reconciliation does not change the saved privacy preference.
9. With a future opening baseline, verify reconciliation is unavailable. With another user's credentials, the API must hide this account with the same 404 response as a missing account.

Automated tests cover time boundaries, ownership, durable retries, concurrent requests, database constraints and rollback. Physical-phone acceptance is a separate manual check.
