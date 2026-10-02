using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Domain.Finance.Accounts;

// Derived account balances (ADR-007). Balances are never stored.
//
// Transaction effects are evaluated before transactions occurring at T:
// - with an opening balance OB:
//     T < OB.AsOfUtc  → not available (null);
//     otherwise       → OB.Amount + effects of transactions with OB.AsOfUtc <= OccurredAtUtc < T;
// - without one: effects of all transactions with OccurredAtUtc < T.
// No opening balance is therefore NOT the same as an opening balance of zero.
// Reconciliation effects apply inclusively at T and at/after any opening baseline.
public static class AccountBalanceCalculator
{
    public static decimal? Calculate(
        Account account,
        OpeningBalance? openingBalance,
        IEnumerable<Transaction> transactions,
        DateTimeOffset atUtc,
        IEnumerable<AccountBalanceAdjustment>? adjustments = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(transactions);

        if (openingBalance is not null && openingBalance.AccountId != account.Id)
        {
            throw new ArgumentException("The opening balance belongs to another account.", nameof(openingBalance));
        }

        var at = atUtc.ToUniversalTime();

        if (openingBalance is not null && at < openingBalance.AsOfUtc)
        {
            return null;
        }

        var balance = openingBalance?.Amount ?? 0m;

        foreach (var transaction in transactions)
        {
            if (transaction.OccurredAtUtc >= at
                || (openingBalance is not null && transaction.OccurredAtUtc < openingBalance.AsOfUtc))
            {
                continue;
            }

            var effect = transaction.EffectOn(account.Id);

            if (effect == 0m)
            {
                continue;
            }

            if (transaction.UserId != account.UserId)
            {
                throw new ArgumentException("A transaction of another user affects this account.", nameof(transactions));
            }

            if (transaction.Currency != account.Currency)
            {
                throw new ArgumentException("A transaction in another currency affects this account.", nameof(transactions));
            }

            balance += effect;
        }

        foreach (var adjustment in adjustments ?? [])
        {
            if (adjustment.AccountId != account.Id || adjustment.UserId != account.UserId
                || adjustment.EffectiveAtUtc > at
                || (openingBalance is not null && adjustment.EffectiveAtUtc < openingBalance.AsOfUtc))
                continue;
            balance += adjustment.Amount;
        }
        return balance;
    }
}
