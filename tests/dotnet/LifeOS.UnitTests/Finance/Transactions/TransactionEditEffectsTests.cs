using LifeOS.Application.Finance.Accounts.GetAccountBalances;
using LifeOS.Application.Finance.Analytics;
using LifeOS.Application.Finance.Analytics.GetMonthlyAnalytics;
using LifeOS.Application.Finance.Transactions.DeleteTransaction;
using LifeOS.Application.Finance.Transactions.UpdateTransaction;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Transactions;

// Balances and analytics are derived from the stored transactions: after an edit or a delete, the
// existing read handlers return the new values with no extra logic. Each test runs the real update or
// delete handler, then the real balances / analytics handlers, on the same in-memory data.
public class TransactionEditEffectsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset InSeptember = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset InAugust = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly (DateTimeOffset From, DateTimeOffset To) September = (new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
    private static readonly (DateTimeOffset From, DateTimeOffset To) August = (new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

    private readonly InMemoryOpeningBalanceRepository _openingBalances = new();
    private readonly InMemoryAccountRepository _accounts;
    private readonly InMemoryCategoryRepository _categories = new();
    private readonly InMemoryTransactionRepository _transactions = new();

    private readonly Account _checking;
    private readonly Account _savings;
    private readonly Category _food;
    private readonly Category _groceries;
    private readonly Category _eatingOut;
    private readonly Category _salary;

    public TransactionEditEffectsTests()
    {
        _accounts = new InMemoryAccountRepository(_openingBalances);
        _checking = AddAccount("Checking", openingBalance: 1000m, asOf: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        _savings = AddAccount("Savings", openingBalance: 500m, asOf: new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        _food = AddCategory("Food & Drink", CategoryType.Expense);
        _groceries = AddCategory("Groceries", CategoryType.Expense, _food);
        _eatingOut = AddCategory("Eating out", CategoryType.Expense, _food);
        _salary = AddCategory("Salary", CategoryType.Income);
    }

    [Fact]
    public async Task ExpenseAmount50To20_RaisesTheBalanceAndLowersMonthlyExpenses()
    {
        var expense = Add(Transaction.CreateExpense(TestUsers.A, _checking.Id, _groceries.Id, 50m, "EUR", InSeptember, null, Now));
        Assert.Equal(950m, await BalanceAsync(_checking));

        await UpdateAccountTransactionAsync(expense, 20m, InSeptember, _checking, _groceries);

        Assert.Equal(980m, await BalanceAsync(_checking));
        Assert.Equal(20m, (await MonthAsync(September)).Expenses);
    }

    [Fact]
    public async Task ExpenseMovedToAnotherAccount_UpdatesBothBalances()
    {
        var expense = Add(Transaction.CreateExpense(TestUsers.A, _checking.Id, _groceries.Id, 50m, "EUR", InSeptember, null, Now));

        await UpdateAccountTransactionAsync(expense, 50m, InSeptember, _savings, _groceries);

        Assert.Equal(1000m, await BalanceAsync(_checking));
        Assert.Equal(450m, await BalanceAsync(_savings));
    }

    [Fact]
    public async Task ExpenseMovedToAnotherSubcategory_MovesTheRowButNotTheParentTotal()
    {
        var expense = Add(Transaction.CreateExpense(TestUsers.A, _checking.Id, _groceries.Id, 50m, "EUR", InSeptember, null, Now));

        await UpdateAccountTransactionAsync(expense, 50m, InSeptember, _checking, _eatingOut);

        var group = Assert.Single((await MonthAsync(September)).ExpenseCategories);
        Assert.Equal((_food.Id, 50m), (group.CategoryId!.Value, group.Amount));
        Assert.Equal([(_eatingOut.Id, 50m)], group.Subcategories.Select(subcategory => (subcategory.CategoryId, subcategory.Amount)));
    }

    [Fact]
    public async Task IncomeAmountDateAndAccountChange_MoveItsEffects()
    {
        var income = Add(Transaction.CreateIncome(TestUsers.A, _checking.Id, _salary.Id, 1500m, "EUR", InSeptember, null, Now));
        Assert.Equal(2500m, await BalanceAsync(_checking));

        await UpdateAccountTransactionAsync(income, 1600m, InAugust, _savings, _salary);

        Assert.Equal(1000m, await BalanceAsync(_checking));
        Assert.Equal(2100m, await BalanceAsync(_savings));
        Assert.Null(await TryMonthAsync(September));
        Assert.Equal((1600m, 1600m), ((await MonthAsync(August)).Income, (await MonthAsync(August)).NetFlow));
    }

    [Fact]
    public async Task TransferAmountAndEndpointChanges_RecomputeBothAccounts_AndAnalyticsStayEmpty()
    {
        var cash = AddAccount("Cash", openingBalance: 0m, asOf: new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        var transfer = Add(Transaction.CreateTransfer(TestUsers.A, _checking.Id, _savings.Id, 100m, "EUR", InSeptember, null, Now));
        Assert.Equal((900m, 600m), (await BalanceAsync(_checking), await BalanceAsync(_savings)));

        await UpdateAsync(new UpdateTransactionCommand(transfer.Id, 40m, InSeptember, null, null, new TransferInput(_savings.Id, cash.Id)));

        Assert.Equal((1000m, 460m, 40m), (await BalanceAsync(_checking), await BalanceAsync(_savings), await BalanceAsync(cash)));
        Assert.Null(await TryMonthAsync(September));
    }

    [Fact]
    public async Task ExpenseMovedAcrossTheMonthBoundary_LeavesTheOldMonthForTheNewOne()
    {
        var expense = Add(Transaction.CreateExpense(TestUsers.A, _savings.Id, _groceries.Id, 30m, "EUR", InSeptember, null, Now));

        await UpdateAccountTransactionAsync(expense, 30m, InAugust, _savings, _groceries);

        Assert.Null(await TryMonthAsync(September));
        Assert.Equal(30m, (await MonthAsync(August)).Expenses);
    }

    [Fact]
    public async Task ExpenseMovedBeforeTheOpeningBalance_NoLongerCountsTowardTheBalance()
    {
        // Checking's opening balance is declared at 1 September (ADR-007): a backfilled expense from
        // August is history before the baseline, so it does not change the balance derived from it.
        var expense = Add(Transaction.CreateExpense(TestUsers.A, _checking.Id, _groceries.Id, 50m, "EUR", InSeptember, null, Now));
        Assert.Equal(950m, await BalanceAsync(_checking));

        await UpdateAccountTransactionAsync(expense, 50m, InAugust, _checking, _groceries);

        Assert.Equal(1000m, await BalanceAsync(_checking));
        Assert.Equal(50m, (await MonthAsync(August)).Expenses);
    }

    [Fact]
    public async Task DeletingAnExpenseAndAnIncome_ReversesBalancesAndAnalytics()
    {
        var expense = Add(Transaction.CreateExpense(TestUsers.A, _checking.Id, _groceries.Id, 50m, "EUR", InSeptember, null, Now));
        var income = Add(Transaction.CreateIncome(TestUsers.A, _checking.Id, _salary.Id, 200m, "EUR", InSeptember, null, Now));
        Assert.Equal(150m, (await MonthAsync(September)).NetFlow);

        await DeleteAsync(expense.Id);
        Assert.Equal((1200m, 0m, 200m), (await BalanceAsync(_checking), (await MonthAsync(September)).Expenses, (await MonthAsync(September)).NetFlow));

        await DeleteAsync(income.Id);
        Assert.Equal(1000m, await BalanceAsync(_checking));
        Assert.Null(await TryMonthAsync(September));
    }

    [Fact]
    public async Task DeletingATransfer_ReversesBothBalances_AnalyticsUnaffected()
    {
        var expense = Add(Transaction.CreateExpense(TestUsers.A, _checking.Id, _groceries.Id, 10m, "EUR", InSeptember, null, Now));
        var transfer = Add(Transaction.CreateTransfer(TestUsers.A, _checking.Id, _savings.Id, 100m, "EUR", InSeptember, null, Now));
        var before = await MonthAsync(September);

        await DeleteAsync(transfer.Id);

        Assert.Equal((990m, 500m), (await BalanceAsync(_checking), await BalanceAsync(_savings)));
        var after = await MonthAsync(September);
        Assert.Equal((before.Expenses, before.Income, before.NetFlow), (after.Expenses, after.Income, after.NetFlow));
        Assert.Single(_transactions.Transactions, transaction => transaction.Id == expense.Id);
    }

    private async Task UpdateAccountTransactionAsync(Transaction transaction, decimal amount, DateTimeOffset occurredAtUtc, Account account, Category category) =>
        await UpdateAsync(new UpdateTransactionCommand(
            transaction.Id, amount, occurredAtUtc, null, new AccountTransactionInput(account.Id, category.Id), null));

    private async Task UpdateAsync(UpdateTransactionCommand command)
    {
        var result = await new UpdateTransactionHandler(_accounts, _categories, _transactions)
            .HandleAsync(TestUsers.A, command, CancellationToken.None);

        Assert.Equal(UpdateTransactionStatus.Updated, result.Status);
    }

    private async Task DeleteAsync(Guid id) =>
        Assert.Equal(
            DeleteTransactionResult.Deleted,
            await new DeleteTransactionHandler(_transactions).HandleAsync(TestUsers.A, id, CancellationToken.None));

    private async Task<decimal?> BalanceAsync(Account account)
    {
        var result = await new GetAccountBalancesHandler(_accounts, _openingBalances, _transactions, new FixedTimeProvider(Now), new InMemoryAccountReconciliationRepository(_accounts, _openingBalances, _transactions))
            .HandleAsync(TestUsers.A, new GetAccountBalancesQuery(Now), CancellationToken.None);

        return result.Balances.Single(balance => balance.AccountId == account.Id).Balance;
    }

    private async Task<CurrencyAnalytics> MonthAsync((DateTimeOffset From, DateTimeOffset To) month) =>
        (await TryMonthAsync(month))!;

    private async Task<CurrencyAnalytics?> TryMonthAsync((DateTimeOffset From, DateTimeOffset To) month)
    {
        var result = await new GetMonthlyAnalyticsHandler(_transactions, _categories)
            .HandleAsync(TestUsers.A, new GetMonthlyAnalyticsQuery(month.From, month.To), CancellationToken.None);

        return result.Currencies.SingleOrDefault();
    }

    private Account AddAccount(string name, decimal openingBalance, DateTimeOffset asOf)
    {
        var account = Account.Create(TestUsers.A, name, AccountType.BankAccount, "EUR", asOf.AddDays(-1));
        _accounts.Accounts.Add(account);
        _openingBalances.OpeningBalances.Add(OpeningBalance.Create(account, openingBalance, asOf, Now));

        return account;
    }

    private Category AddCategory(string name, CategoryType type, Category? parent = null)
    {
        var category = Category.Create(TestUsers.A, name, type, parent, Now);
        _categories.Categories.Add(category);

        return category;
    }

    private Transaction Add(Transaction transaction)
    {
        _transactions.Transactions.Add(transaction);

        return transaction;
    }
}
