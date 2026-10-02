using LifeOS.Application.Finance.Accounts.GetAccountBalances;
using LifeOS.Application.Finance.Accounts.ReconcileAccount;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Accounts;

public class AccountReconciliationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private readonly InMemoryOpeningBalanceRepository _openings = new();
    private readonly InMemoryTransactionRepository _transactions = new();
    private readonly InMemoryAccountRepository _accounts;
    private readonly InMemoryAccountReconciliationRepository _repository;
    private readonly Account _account = Account.Create(TestUsers.A, "Checking", AccountType.BankAccount, "EUR", Now);

    public AccountReconciliationTests()
    {
        _accounts = new(_openings);
        _accounts.Accounts.Add(_account);
        _repository = new(_accounts, _openings, _transactions);
    }
    private Task<ReconcileAccountResult> Reconcile(decimal observed, Guid? key = null, Guid? owner = null, string? note = null) =>
        new ReconcileAccountHandler(_repository, new FixedTimeProvider(Now)).HandleAsync(owner ?? TestUsers.A,
            new(_account.Id, key ?? Guid.NewGuid(), observed, note), default);

    [Theory]
    [InlineData(1237.5, 1250, 12.5)]
    [InlineData(1250, 1237.5, -12.5)]
    [InlineData(0, -350.25, -350.25)]
    [InlineData(-350, -300, 50)]
    public async Task SignedDelta_IsServerCalculated_AndBaselineIsUnchanged(decimal previous, decimal observed, decimal delta)
    {
        var opening = OpeningBalance.Create(_account, previous, Now.AddDays(-1), Now);
        _openings.OpeningBalances.Add(opening);
        var result = await Reconcile(observed, note: "  Bank reconciliation  ");
        Assert.Equal(ReconcileAccountStatus.Ok, result.Status);
        Assert.Equal(previous, result.Receipt!.PreviousBalance);
        var adjustment = Assert.Single(_repository.Adjustments);
        Assert.Equal((delta, observed, "Bank reconciliation"), (adjustment.Amount, adjustment.ObservedBalance, adjustment.Note));
        Assert.Equal((Now, Now), (adjustment.EffectiveAtUtc, adjustment.CreatedAtUtc));
        Assert.Equal(previous, opening.Amount);
        var get = new GetAccountBalancesHandler(_accounts, _openings, _transactions, new FixedTimeProvider(Now), _repository);
        Assert.Equal(observed, Assert.Single((await get.HandleAsync(TestUsers.A, new(null), default)).Balances).Balance);
        Assert.Empty(_transactions.Transactions);
    }

    [Fact]
    public async Task ZeroDifference_PersistsOnlyReceipt_AndRetryAfterExpenseReturnsOriginalResult()
    {
        var key = Guid.NewGuid();
        var first = await Reconcile(0, key, note: " ");
        Assert.Null(first.Adjustment);
        Assert.Empty(_repository.Adjustments);
        Assert.Single(_repository.Receipts);
        Assert.Null(first.Receipt!.Note);
        _transactions.Transactions.Add(Transaction.CreateExpense(TestUsers.A, _account.Id, Guid.NewGuid(), 10, "EUR", Now.AddSeconds(-1), null, Now));
        var replay = await Reconcile(0, key);
        Assert.True(replay.IsReplay);
        Assert.Equal(first.Receipt, replay.Receipt);
        Assert.Empty(_repository.Adjustments);
        Assert.Equal(-10, AccountBalanceCalculator.Calculate(_account, null, _transactions.Transactions, Now, _repository.Adjustments));
    }

    [Fact]
    public async Task RetryAfterExpense_DoesNotReapplyCorrection_AndChangedPayloadConflicts()
    {
        var key = Guid.NewGuid();
        var first = await Reconcile(100, key, note: "bank");
        _transactions.Transactions.Add(Transaction.CreateExpense(TestUsers.A, _account.Id, Guid.NewGuid(), 10, "EUR", Now.AddSeconds(-1), null, Now));
        var replay = await Reconcile(100, key, note: " bank ");
        Assert.True(replay.IsReplay);
        Assert.Equal(first.Receipt, replay.Receipt);
        Assert.Single(_repository.Adjustments);
        Assert.Equal(ReconcileAccountStatus.Conflict, (await Reconcile(101, key, note: "bank")).Status);
        Assert.Equal(ReconcileAccountStatus.Conflict, (await Reconcile(100, key, note: "different")).Status);
    }

    [Fact]
    public async Task ConcurrentRequests_WithDistinctKeysAtSameInstant_DoNotDoubleCorrect()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Reconcile(1250)));
        Assert.All(results, r => Assert.Equal(ReconcileAccountStatus.Ok, r.Status));
        Assert.Single(_repository.Adjustments);
        Assert.Equal(4, _repository.Receipts.Count);
        Assert.Equal(1250, AccountBalanceCalculator.Calculate(_account, null, [], Now, _repository.Adjustments));
    }

    [Fact]
    public async Task Ownership_UnavailableAndFailureDoNotConsumeRequestKey()
    {
        var key = Guid.NewGuid();
        Assert.Equal(ReconcileAccountStatus.NotFound, (await Reconcile(100, key, TestUsers.B)).Status);
        _openings.OpeningBalances.Add(OpeningBalance.Create(_account, 100, Now.AddMinutes(1), Now));
        Assert.Equal(ReconcileAccountStatus.Unavailable, (await Reconcile(100, key)).Status);
        Assert.Empty(_repository.Receipts);
        _openings.OpeningBalances.Clear();
        Assert.Equal(ReconcileAccountStatus.Ok, (await Reconcile(100, key)).Status);
    }

    [Theory]
    [InlineData("1.00001")]
    [InlineData("1000000000000000")]
    [InlineData("-1000000000000000")]
    public async Task UnsupportedMoney_IsRejectedBeforePersistence(string text)
    {
        var observed = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(ReconcileAccountStatus.Invalid, (await Reconcile(observed)).Status);
        Assert.Empty(_repository.Receipts);
        Assert.Empty(_repository.Adjustments);
    }

    [Fact]
    public async Task DifferenceRange_IsValidated_AndMaximumSignedObservedBalancesAreSupported()
    {
        var maximum = Transaction.MaxAmount;
        var receipt = AccountReconciliation.Create(_account, Guid.NewGuid(), 0, -maximum, Now, null);
        Assert.Equal(-maximum, receipt.AdjustmentAmount);
        _openings.OpeningBalances.Add(OpeningBalance.Create(_account, -maximum, Now.AddDays(-1), Now));
        Assert.Equal(ReconcileAccountStatus.Invalid, (await Reconcile(maximum)).Status);
        Assert.Empty(_repository.Receipts);
    }

    [Fact]
    public void AdjustmentBoundary_IsInclusive_AndFutureAndPreBaselineEffectsAreExcluded()
    {
        var before = Adjustment(_account, Now.AddSeconds(-1), 50);
        var at = Adjustment(_account, Now, 12.5m);
        var future = Adjustment(_account, Now.AddSeconds(1), 99);
        var other = Account.Create(TestUsers.B, "Other", AccountType.Cash, "USD", Now);
        var foreign = Adjustment(other, Now, 999);
        var opening = OpeningBalance.Create(_account, 100, Now, Now);
        Assert.Null(AccountBalanceCalculator.Calculate(_account, opening, [], Now.AddTicks(-1), [before, at]));
        Assert.Equal(112.5m, AccountBalanceCalculator.Calculate(_account, opening, [], Now, [before, at, future, foreign]));
        Assert.Equal(62.5m, AccountBalanceCalculator.Calculate(_account, null, [], Now, [before, at, future, foreign]));
    }

    [Fact]
    public void TransactionAndAdjustmentComposition_PreservesExclusiveTransactionBoundary()
    {
        var opening = OpeningBalance.Create(_account, 1000, Now.AddDays(-1), Now);
        var category = Guid.NewGuid();
        Transaction[] movements = [Transaction.CreateExpense(TestUsers.A, _account.Id, category, 50, "EUR", Now.AddSeconds(-1), null, Now),
            Transaction.CreateIncome(TestUsers.A, _account.Id, category, 999, "EUR", Now, null, Now)];
        Assert.Equal(962.5m, AccountBalanceCalculator.Calculate(_account, opening, movements, Now, [Adjustment(_account, Now, 12.5m)]));
    }

    [Fact]
    public async Task BackwardServerClock_ConflictsRatherThanInsertingBeforeCommittedAdjustment()
    {
        _repository.Adjustments.Add(Adjustment(_account, Now.AddSeconds(1), 10));
        Assert.Equal(ReconcileAccountStatus.Conflict, (await Reconcile(20)).Status);
        Assert.Empty(_repository.Receipts);
    }

    [Fact]
    public void ServerInstants_AreNormalizedToUtcMicroseconds()
    {
        var receipt = AccountReconciliation.Create(_account, Guid.NewGuid(), 0, 1, Now.ToOffset(TimeSpan.FromHours(2)).AddTicks(7), null);
        Assert.Equal(Now, receipt.EffectiveAtUtc);
        Assert.Equal(TimeSpan.Zero, receipt.CreatedAtUtc.Offset);
    }

    private static AccountBalanceAdjustment Adjustment(Account account, DateTimeOffset at, decimal delta) =>
        AccountBalanceAdjustment.From(AccountReconciliation.Create(account, Guid.NewGuid(), 0, delta, at, null))!;
}
