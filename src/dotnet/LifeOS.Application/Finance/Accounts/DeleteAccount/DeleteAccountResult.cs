namespace LifeOS.Application.Finance.Accounts.DeleteAccount;

public enum DeleteAccountResult
{
    Deleted,

    // Missing, or another user's account.
    NotFound,

    // Transactions reference the account (as account, transfer source or destination).
    HasTransactions,

    // The account changed while it was being deleted (an opening balance was added). Retryable.
    Changed
}
