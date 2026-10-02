using LifeOS.Application.Finance.Accounts;
using LifeOS.Application.Finance.Accounts.DeleteAccount;
using LifeOS.Application.Finance.Accounts.GetAccountBalances;
using LifeOS.Application.Finance.Accounts.ReconcileAccount;
using LifeOS.Application.Finance.Transactions;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public class AccountReconciliationPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RoundTrip_PreservesSignedAmountsOwnerAndMicrosecondTime()
    {
        var (user, account) = await NewAccount();
        var result = await Reconcile(user.Id, account.Id, -350.1234m, Guid.NewGuid(), "  bank  ");
        Assert.Equal(ReconcileAccountStatus.Ok, result.Status);
        await using var scope = fixture.CreateScope();
        var db = Db(scope);
        var adjustment = await db.AccountBalanceAdjustments.AsNoTracking().SingleAsync(a => a.AccountId == account.Id);
        var receipt = await db.AccountReconciliations.AsNoTracking().SingleAsync(r => r.AccountId == account.Id);
        Assert.Equal((user.Id, account.Id, -350.1234m, -350.1234m, "bank"),
            (adjustment.UserId, adjustment.AccountId, adjustment.Amount, adjustment.ObservedBalance, adjustment.Note));
        Assert.Equal((Now, Now, receipt.Id), (adjustment.EffectiveAtUtc, adjustment.CreatedAtUtc, adjustment.Id));
        var get = new GetAccountBalancesHandler(scope.ServiceProvider.GetRequiredService<IAccountRepository>(),
            scope.ServiceProvider.GetRequiredService<IOpeningBalanceRepository>(), scope.ServiceProvider.GetRequiredService<ITransactionRepository>(),
            new FixedTimeProvider(Now), scope.ServiceProvider.GetRequiredService<IAccountBalanceAdjustmentRepository>());
        Assert.Equal(-350.1234m, Assert.Single((await get.HandleAsync(user.Id, new(null), default)).Balances).Balance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurableRetry_AfterInterveningExpense_ReplaysOriginalWithoutNewAdjustment(bool zero)
    {
        var (user, account) = await NewAccount();
        var key = Guid.NewGuid();
        var observed = zero ? 0 : 100;
        var first = await Reconcile(user.Id, account.Id, observed, key);
        var category = Category.Create(user.Id, "Food", CategoryType.Expense, null, Now);
        var expense = Transaction.CreateExpense(user.Id, account.Id, category.Id, 10, "EUR", Now.AddSeconds(-1), null, Now);
        await PostgresAssert.InsertAsync(fixture, category, expense);
        var replay = await Reconcile(user.Id, account.Id, observed, key);
        Assert.True(replay.IsReplay);
        Assert.Equal(first.Receipt!.Id, replay.Receipt!.Id);
        Assert.Equal(first.Receipt.PreviousBalance, replay.Receipt.PreviousBalance);
        Assert.Equal(first.Receipt.EffectiveAtUtc, replay.Receipt.EffectiveAtUtc);
        Assert.Equal(ReconcileAccountStatus.Conflict, (await Reconcile(user.Id, account.Id, observed + 1, key)).Status);
        await using var scope = fixture.CreateScope();
        var adjustments = await Db(scope).AccountBalanceAdjustments.Where(a => a.AccountId == account.Id).ToListAsync();
        Assert.Equal(zero ? 0 : 1, adjustments.Count);
        Assert.Equal(observed - 10, AccountBalanceCalculator.Calculate(account, null, [expense], Now, adjustments));
        Assert.Equal(1, await Db(scope).AccountReconciliations.CountAsync(r => r.AccountId == account.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentCommands_SerializeWithoutDoubleCorrection(bool sameKey)
    {
        var (user, account) = await NewAccount();
        var gate = new Rendezvous(4);
        var key = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await gate.ArriveAndWaitAsync();
            return await Reconcile(user.Id, account.Id, 1250, sameKey ? key : Guid.NewGuid());
        }));
        Assert.All(results, result => Assert.Equal(ReconcileAccountStatus.Ok, result.Status));
        await using var scope = fixture.CreateScope();
        var adjustment = Assert.Single(await Db(scope).AccountBalanceAdjustments.Where(a => a.AccountId == account.Id).ToListAsync());
        Assert.Equal(1250, adjustment.Amount);
        Assert.Equal(sameKey ? 1 : 4, await Db(scope).AccountReconciliations.CountAsync(r => r.AccountId == account.Id));
        if (sameKey) Assert.Single(results, r => !r.IsReplay);
    }

    [Fact]
    public async Task ConcurrentDifferentTargets_RecalculateFromCommittedPredecessor()
    {
        var (user, account) = await NewAccount();
        var gate = new Rendezvous(2);
        var results = await Task.WhenAll(new[] { 100m, 200m }.Select(async observed =>
        {
            await gate.ArriveAndWaitAsync();
            return await Reconcile(user.Id, account.Id, observed, Guid.NewGuid());
        }));
        Assert.All(results, result => Assert.Equal(ReconcileAccountStatus.Ok, result.Status));
        Assert.Contains(results, r => r.Receipt!.PreviousBalance == 0);
        Assert.Contains(results, r => r.Receipt!.PreviousBalance is 100 or 200);
        await using var scope = fixture.CreateScope();
        var adjustments = await Db(scope).AccountBalanceAdjustments.Where(a => a.AccountId == account.Id).ToListAsync();
        var total = AccountBalanceCalculator.Calculate(account, null, [], Now, adjustments);
        Assert.Contains(total!.Value, new[] { 100m, 200m });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task AccountDeletion_RestrictsAuditAndRollsBackOpeningBalanceDeletion(decimal observed)
    {
        var (user, account) = await NewAccount();
        var opening = OpeningBalance.Create(account, 0, Now.AddDays(-1), Now);
        await PostgresAssert.InsertAsync(fixture, opening);
        await Reconcile(user.Id, account.Id, observed, Guid.NewGuid());
        await using var scope = fixture.CreateScope();
        var handler = new DeleteAccountHandler(scope.ServiceProvider.GetRequiredService<IAccountRepository>(),
            scope.ServiceProvider.GetRequiredService<ITransactionRepository>());
        Assert.Equal(DeleteAccountResult.HasReconciliations, await handler.HandleAsync(user.Id, account.Id, default));
        Assert.True(await Db(scope).Accounts.AnyAsync(a => a.Id == account.Id));
        Assert.True(await Db(scope).OpeningBalances.AnyAsync(o => o.Id == opening.Id));
        Assert.True(await Db(scope).AccountReconciliations.AnyAsync(r => r.AccountId == account.Id));
        await PostgresAssert.DeleteBlockedByOneOfAsync(
            ["FK_account_balance_adjustments_accounts_account_id_user_id", "FK_account_reconciliations_accounts_account_id_user_id", "FK_opening_balances_accounts_account_id_user_id"], async () =>
            {
                await using var otherScope = fixture.CreateScope();
                await Db(otherScope).Accounts.Where(a => a.Id == account.Id).ExecuteDeleteAsync();
            });
    }

    [Fact]
    public async Task OtherOwnersAccount_IsHidden_AndCompositeOwnershipFkRejectsCrossUserWrites()
    {
        var (a, account) = await NewAccount();
        var b = User.CreateFromExternalIdentity(null, null, Now);
        await PostgresAssert.InsertAsync(fixture, b);
        Assert.Equal(ReconcileAccountStatus.NotFound, (await Reconcile(b.Id, account.Id, 1, Guid.NewGuid())).Status);
        await PostgresAssert.ViolatesAsync(PostgresAssert.ForeignKeyViolation,
            "FK_account_reconciliations_accounts_account_id_user_id", async () =>
            {
                await using var scope = fixture.CreateScope();
                await Db(scope).Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO account_reconciliations (id,user_id,account_id,request_id,previous_balance,observed_balance,effective_at_utc,created_at_utc)
                    VALUES ({Guid.NewGuid()},{b.Id},{account.Id},{Guid.NewGuid()},0,1,{Now},{Now})
                    """);
            });
        await Reconcile(a.Id, account.Id, 1, Guid.NewGuid());
        await PostgresAssert.ViolatesOneOfAsync(PostgresAssert.ForeignKeyViolation,
            ["FK_account_balance_adjustments_accounts_account_id_user_id", "FK_adjustments_reconciliation_receipt"], async () =>
            {
                await using var scope = fixture.CreateScope();
                var receipt = await Db(scope).AccountReconciliations.SingleAsync(r => r.AccountId == account.Id);
                // A new adjustment references A's account while claiming B as owner.
                await Db(scope).Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO account_balance_adjustments (id,user_id,account_id,amount,observed_balance,effective_at_utc,created_at_utc)
                    VALUES ({Guid.NewGuid()},{b.Id},{account.Id},1,1,{Now},{Now})
                    """);
            });
    }

    [Fact]
    public async Task MonetaryConstraints_RejectZeroAndOverflow_AndReceiptsAreUnique()
    {
        var (user, account) = await NewAccount();
        var key = Guid.NewGuid();
        var result = await Reconcile(user.Id, account.Id, 1.1234m, key);
        await PostgresAssert.ViolatesAsync("23514", "ck_account_balance_adjustments_nonzero", async () =>
        {
            await using var scope = fixture.CreateScope();
            await Db(scope).Database.ExecuteSqlInterpolatedAsync($"UPDATE account_balance_adjustments SET amount=0 WHERE account_id={account.Id}");
        });
        await PostgresAssert.ViolatesAsync("22003", null!, async () =>
        {
            await using var scope = fixture.CreateScope();
            await Db(scope).Database.ExecuteSqlInterpolatedAsync($"UPDATE account_balance_adjustments SET observed_balance=1000000000000000 WHERE account_id={account.Id}");
        });
        var duplicate = AccountReconciliation.Create(account, key, 0, 1, Now, null);
        await PostgresAssert.InsertViolatesAsync(fixture, PostgresAssert.UniqueViolation,
            "ux_account_reconciliations_account_user_request", duplicate);
        await using var read = fixture.CreateScope();
        Assert.Equal(1.1234m, (await Db(read).AccountBalanceAdjustments.SingleAsync(a => a.AccountId == account.Id)).Amount);
    }

    private async Task<(User User, Account Account)> NewAccount()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now);
        var account = Account.Create(user.Id, "Checking", AccountType.BankAccount, "EUR", Now);
        await PostgresAssert.InsertAsync(fixture, user, account);
        return (user, account);
    }
    private async Task<ReconcileAccountResult> Reconcile(Guid owner, Guid accountId, decimal observed, Guid key, string? note = null)
    {
        await using var scope = fixture.CreateScope();
        return await new ReconcileAccountHandler(scope.ServiceProvider.GetRequiredService<IAccountReconciliationRepository>(),
            new FixedTimeProvider(Now)).HandleAsync(owner, new(accountId, key, observed, note), default);
    }
    private static LifeOSDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();
}
