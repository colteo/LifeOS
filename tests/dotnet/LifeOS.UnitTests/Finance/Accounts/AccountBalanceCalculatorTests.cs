using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Accounts;

// ADR-007: Balance(X, T) = OB.Amount + effects of transactions with OB.AsOfUtc <= OccurredAtUtc < T;
// without an opening balance, effects of all transactions with OccurredAtUtc < T.
public class AccountBalanceCalculatorTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 9, 30, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Created = Baseline.AddDays(30);

    private readonly Account _checking = NewAccount("Checking", AccountType.BankAccount);
    private readonly Account _savings = NewAccount("Savings", AccountType.Savings);
    private readonly Guid _categoryId = Guid.CreateVersion7();

    // ---- Transaction.EffectOn ----

    [Fact]
    public void EffectOn_FollowsTheTransactionType()
    {
        var income = Income(_checking, 100m, Baseline);
        var expense = Expense(_checking, 30m, Baseline);
        var transfer = Transfer(_checking, _savings, 50m, Baseline);

        Assert.Equal(100m, income.EffectOn(_checking.Id));
        Assert.Equal(-30m, expense.EffectOn(_checking.Id));
        Assert.Equal(-50m, transfer.EffectOn(_checking.Id));
        Assert.Equal(50m, transfer.EffectOn(_savings.Id));
        Assert.Equal(0m, income.EffectOn(_savings.Id));
        Assert.Equal(0m, transfer.EffectOn(Guid.CreateVersion7()));
    }

    // ---- boundaries ----

    [Fact]
    public void TransactionExactlyAtAsOf_Counts_AndOneJustBefore_DoesNot()
    {
        var openingBalance = Opening(_checking, 1000m, Baseline);
        var transactions = new[]
        {
            Expense(_checking, 10m, Baseline),
            Expense(_checking, 99m, Baseline.AddTicks(-10))
        };

        Assert.Equal(990m, Balance(_checking, openingBalance, transactions, Baseline.AddHours(1)));
    }

    [Fact]
    public void TransactionExactlyAtT_DoesNotCount()
    {
        var openingBalance = Opening(_checking, 1000m, Baseline);
        var at = Baseline.AddHours(2);

        Assert.Equal(1000m, Balance(_checking, openingBalance, [Expense(_checking, 10m, at)], at));
        Assert.Equal(990m, Balance(_checking, openingBalance, [Expense(_checking, 10m, at)], at.AddTicks(10)));
    }

    [Fact]
    public void AtAsOf_IsExactlyTheOpeningAmount()
    {
        var openingBalance = Opening(_checking, 1000m, Baseline);

        Assert.Equal(1000m, Balance(_checking, openingBalance, [Expense(_checking, 10m, Baseline)], Baseline));
    }

    [Fact]
    public void BeforeAsOf_IsNotAvailable()
    {
        var openingBalance = Opening(_checking, 1000m, Baseline);

        Assert.Null(Balance(_checking, openingBalance, [], Baseline.AddTicks(-10)));
    }

    // ---- no opening balance vs an opening balance of zero ----

    [Fact]
    public void NoOpeningBalance_IsNotTheSameAsAnOpeningBalanceOfZero()
    {
        var history = new[]
        {
            Income(_checking, 500m, Baseline.AddDays(-10)),
            Expense(_checking, 120m, Baseline.AddDays(-5))
        };

        // Without a baseline the balance is derived from the whole history, at any instant.
        Assert.Equal(500m, Balance(_checking, null, history, Baseline.AddDays(-7)));
        Assert.Equal(380m, Balance(_checking, null, history, Baseline));
        Assert.Equal(380m, Balance(_checking, null, history, Baseline.AddDays(1)));

        // With an opening balance of 0 at Baseline, earlier history is absorbed by the baseline.
        var zero = Opening(_checking, 0m, Baseline);
        Assert.Null(Balance(_checking, zero, history, Baseline.AddDays(-7)));
        Assert.Null(Balance(_checking, zero, history, Baseline.AddTicks(-10)));
        Assert.Equal(0m, Balance(_checking, zero, history, Baseline));
        Assert.Equal(0m, Balance(_checking, zero, history, Baseline.AddDays(1)));
    }

    [Fact]
    public void WithoutOpeningBalance_EveryTransactionBeforeTCounts()
    {
        var transactions = new[]
        {
            Income(_checking, 1000m, Baseline.AddYears(-3)),
            Expense(_checking, 250m, Baseline),
            Income(_checking, 7m, Baseline.AddDays(2))
        };

        Assert.Equal(750m, Balance(_checking, null, transactions, Baseline.AddDays(1)));
    }

    // ---- transfers ----

    [Fact]
    public void Transfer_AffectsSourceAndDestinationOppositely()
    {
        var transactions = new[] { Transfer(_checking, _savings, 200m, Baseline.AddHours(1)) };
        var at = Baseline.AddDays(1);

        Assert.Equal(800m, Balance(_checking, Opening(_checking, 1000m, Baseline), transactions, at));
        Assert.Equal(700m, Balance(_savings, Opening(_savings, 500m, Baseline), transactions, at));
    }

    [Fact]
    public void Transfer_ChangesEachAccountButNotTheirSum()
    {
        var openingChecking = Opening(_checking, 1000m, Baseline);
        var openingSavings = Opening(_savings, 500m, Baseline);
        var before = Baseline.AddHours(1);
        var after = Baseline.AddHours(3);
        var transactions = new[] { Transfer(_checking, _savings, 200m, Baseline.AddHours(2)) };

        var checkingBefore = Balance(_checking, openingChecking, transactions, before);
        var savingsBefore = Balance(_savings, openingSavings, transactions, before);
        var checkingAfter = Balance(_checking, openingChecking, transactions, after);
        var savingsAfter = Balance(_savings, openingSavings, transactions, after);

        Assert.NotEqual(checkingBefore, checkingAfter);
        Assert.NotEqual(savingsBefore, savingsAfter);
        Assert.Equal(checkingBefore + savingsBefore, checkingAfter + savingsAfter);
    }

    [Fact]
    public void Transfer_UsesEachAccountsOwnBaseline()
    {
        // Checking's baseline is before the transfer, Savings' baseline after it.
        var transfer = Transfer(_checking, _savings, 200m, Baseline.AddDays(1));
        var at = Baseline.AddDays(3);

        Assert.Equal(800m, Balance(_checking, Opening(_checking, 1000m, Baseline), [transfer], at));
        Assert.Equal(500m, Balance(_savings, Opening(_savings, 500m, Baseline.AddDays(2)), [transfer], at));
    }

    // ---- sign convention and other rules ----

    [Fact]
    public void CreditCard_DebtIsNegativeAndAPaymentReducesIt()
    {
        var card = NewAccount("Card", AccountType.CreditCard);
        var transactions = new[]
        {
            Expense(card, 50m, Baseline.AddHours(1)),
            Transfer(_checking, card, 300m, Baseline.AddHours(2))
        };

        Assert.Equal(-100m, Balance(card, Opening(card, -350m, Baseline), transactions, Baseline.AddDays(1)));
    }

    [Fact]
    public void FutureDatedTransaction_IsNotInTheCurrentBalance()
    {
        var now = Baseline.AddDays(1);

        Assert.Equal(1000m, Balance(_checking, Opening(_checking, 1000m, Baseline), [Expense(_checking, 90m, now.AddDays(3))], now));
    }

    [Fact]
    public void BackfilledTransactionBeforeTheBaseline_DoesNotChangeTheBalance()
    {
        var openingBalance = Opening(_checking, 1000m, Baseline);
        var backfilled = Expense(_checking, 400m, Baseline.AddMonths(-2));

        Assert.Equal(1000m, Balance(_checking, openingBalance, [backfilled], Baseline.AddDays(1)));
    }

    [Fact]
    public void BalancesAreAdditiveOverHalfOpenWindows()
    {
        var openingBalance = Opening(_checking, 1000m, Baseline);
        var from = Baseline.AddDays(1);
        var to = Baseline.AddDays(2);
        var transactions = new[]
        {
            Income(_checking, 100m, Baseline.AddHours(1)),
            Expense(_checking, 40m, from),
            Income(_checking, 15m, from.AddHours(5)),
            Expense(_checking, 1m, to)
        };

        var net = transactions
            .Where(transaction => transaction.OccurredAtUtc >= from && transaction.OccurredAtUtc < to)
            .Sum(transaction => transaction.EffectOn(_checking.Id));

        Assert.Equal(
            Balance(_checking, openingBalance, transactions, from) + net,
            Balance(_checking, openingBalance, transactions, to));
    }

    [Fact]
    public void OpeningBalanceOfAnotherAccount_IsRejected()
    {
        Assert.Throws<ArgumentException>(
            () => Balance(_checking, Opening(_savings, 1m, Baseline), [], Baseline.AddDays(1)));
    }

    [Fact]
    public void TransactionInAnotherCurrency_IsRejected()
    {
        var foreign = Transaction.CreateIncome(TestUsers.A, _checking.Id, _categoryId, 5m, "USD", Baseline, null, Created);

        Assert.Throws<ArgumentException>(() => Balance(_checking, null, [foreign], Baseline.AddDays(1)));
    }

    [Fact]
    public void TransactionOfAnotherUser_IsRejected()
    {
        var foreign = Transaction.CreateIncome(TestUsers.B, _checking.Id, _categoryId, 5m, "EUR", Baseline, null, Created);

        Assert.Throws<ArgumentException>(() => Balance(_checking, null, [foreign], Baseline.AddDays(1)));
    }

    private static decimal? Balance(Account account, OpeningBalance? openingBalance, IEnumerable<Transaction> transactions, DateTimeOffset at) =>
        AccountBalanceCalculator.Calculate(account, openingBalance, transactions, at);

    private static Account NewAccount(string name, AccountType type) =>
        Account.Create(TestUsers.A, name, type, "EUR", Baseline.AddYears(-5));

    private static OpeningBalance Opening(Account account, decimal amount, DateTimeOffset asOf) =>
        OpeningBalance.Create(account, amount, asOf, Created);

    private Transaction Income(Account account, decimal amount, DateTimeOffset occurredAt) =>
        Transaction.CreateIncome(TestUsers.A, account.Id, _categoryId, amount, "EUR", occurredAt, null, Created);

    private Transaction Expense(Account account, decimal amount, DateTimeOffset occurredAt) =>
        Transaction.CreateExpense(TestUsers.A, account.Id, _categoryId, amount, "EUR", occurredAt, null, Created);

    private static Transaction Transfer(Account source, Account destination, decimal amount, DateTimeOffset occurredAt) =>
        Transaction.CreateTransfer(TestUsers.A, source.Id, destination.Id, amount, "EUR", occurredAt, null, Created);
}
