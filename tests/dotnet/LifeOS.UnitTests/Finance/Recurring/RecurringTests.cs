using LifeOS.Application.Finance.Budgets;
using LifeOS.Application.Finance.Recurring;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Budgets;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Recurring;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Recurring;

public class RecurringTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static RecurringTransactionRule Rule(int day = 1, TransactionType type = TransactionType.Expense, decimal amount = 20) =>
        RecurringTransactionRule.Create(Guid.NewGuid(), " Fee ", type, Guid.NewGuid(), Guid.NewGuid(), amount, day, 2024, 1, " note ", Now);

    [Theory]
    [InlineData(2026, 1, 1, 1)] [InlineData(2026, 2, 28, 28)] [InlineData(2026, 2, 29, 28)]
    [InlineData(2026, 2, 30, 28)] [InlineData(2026, 2, 31, 28)] [InlineData(2024, 2, 29, 29)]
    [InlineData(2024, 2, 31, 29)] [InlineData(2026, 4, 31, 30)] [InlineData(2026, 5, 31, 31)]
    public void Schedule_ClampsDeterministically(int year, int month, int day, int expected) =>
        Assert.Equal(new DateOnly(year, month, expected), Rule(day).ScheduledDate(year, month));

    [Theory]
    [InlineData(0)] [InlineData(32)]
    public void InvalidDay(int day) => Assert.Throws<ArgumentOutOfRangeException>(() => Rule(day));
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(0.00001)] [InlineData(1000000000000000)]
    public void InvalidAmount(decimal amount) => Assert.ThrowsAny<ArgumentException>(() => Rule(amount: amount));
    [Fact]
    public void TypesStartAndMetadata()
    {
        Assert.Throws<ArgumentException>(() => Rule(type: TransactionType.Transfer));
        Assert.Throws<ArgumentException>(() => Rule(type: (TransactionType)99));
        Assert.Throws<ArgumentException>(() => Rule().ScheduledDate(2023, 12));
        Assert.Equal("Fee", Rule().Name); Assert.Equal("note", Rule().Note);
        Assert.Equal(TransactionType.Income, Rule(type: TransactionType.Income).TransactionType);
    }
    [Theory]
    [InlineData(OccurrenceStatus.Confirmed)] [InlineData(OccurrenceStatus.Skipped)]
    public void Edit_PreservesProcessedLogicalMonth(OccurrenceStatus status)
    {
        var r = Rule(); var state = RecurringOccurrenceState.Create(r, 2026, 10, status,
            status == OccurrenceStatus.Confirmed ? Guid.NewGuid() : null, Now);
        r.Update("Changed", r.AccountId, r.CategoryId, 50, 15, null, Now);
        Assert.Equal(status, r.Status(2026, 10, new(2026, 10, 10), state));
        Assert.Equal(new DateOnly(2026, 10, 1), state.ScheduledDate);
        Assert.Equal(new DateOnly(2026, 11, 15), r.ScheduledDate(2026, 11));
    }
    [Fact]
    public void DerivedAndPersistedStatuses_UseLocalDate()
    {
        var r = Rule(10);
        Assert.Equal(OccurrenceStatus.Projected, r.Status(2026, 10, new(2026, 10, 9)));
        Assert.Equal(OccurrenceStatus.Due, r.Status(2026, 10, new(2026, 10, 10)));
        Assert.Throws<ArgumentException>(() => RecurringOccurrenceState.Create(r, 2026, 10, OccurrenceStatus.Due, null, Now));
        Assert.Throws<ArgumentException>(() => RecurringOccurrenceState.Create(r, 2026, 10, OccurrenceStatus.Confirmed, null, Now));
        Assert.Throws<ArgumentException>(() => RecurringOccurrenceState.Create(r, 2026, 10, OccurrenceStatus.Skipped, Guid.NewGuid(), Now));
        var clock = new FixedTimeProvider(new(2026, 10, 1, 0, 30, 0, TimeSpan.Zero));
        Assert.Equal(new DateOnly(2026, 9, 30), RecurringHandler.LocalToday(clock, -60));
    }

    [Fact]
    public async Task Application_CRUD_Ownership_MissedMonths_AndActions()
    {
        var owner = Guid.NewGuid(); var accounts = new InMemoryAccountRepository(); var categories = new InMemoryCategoryRepository();
        var transactions = new InMemoryTransactionRepository(); var repo = new InMemoryRecurringRepository(accounts, categories, transactions);
        var handler = new RecurringHandler(repo, new FixedTimeProvider(Now));
        var account = Account.Create(owner, "Cash", AccountType.Cash, "EUR", Now);
        var category = Category.Create(owner, "Fees", CategoryType.Expense, null, Now);
        accounts.Accounts.Add(account); categories.Categories.Add(category);
        var input = new SaveRecurringRule("Fee", TransactionType.Expense, account.Id, category.Id, 20, 1, 2026, 2, null);
        Assert.Equal(RecurringResultStatus.Invalid, (await handler.SaveAsync(Guid.NewGuid(), null, input, default)).Status);
        var r = (await handler.SaveAsync(owner, null, input, default)).Rule!;
        Assert.Equal(RecurringResultStatus.NotFound, (await handler.DeleteAsync(Guid.NewGuid(), r.Id, default)).Status);
        Assert.Empty((await handler.QueryAsync(Guid.NewGuid(), 2026, 2, 2026, 12, 0, default)).Rules);
        Assert.Equal(RecurringResultStatus.Invalid, (await handler.QueryAsync(owner, 2026, 1, 2040, 1, 0, default)).Status);
        var q = await handler.QueryAsync(owner, 2026, 1, 2026, 12, 0, default);
        Assert.Equal(11, q.Occurrences.Count); Assert.Equal(9, q.Occurrences.Count(o => o.Status == OccurrenceStatus.Due));
        Assert.Equal(RecurringResultStatus.Conflict, (await handler.ActAsync(owner, r.Id, 2026, 11, 0, "confirm", new(20, null, Now), default)).Status);
        Assert.Equal(RecurringResultStatus.Ok, (await handler.ActAsync(owner, r.Id, 2026, 11, 0, "skip", null, default)).Status);
        Assert.Empty(transactions.Transactions);
        await handler.ActAsync(owner, r.Id, 2026, 11, 0, "restore", null, default);
        Assert.Equal(OccurrenceStatus.Projected, (await handler.QueryAsync(owner, 2026, 11, 2026, 11, 0, default)).Occurrences.Single().Status);
        await handler.ActAsync(owner, r.Id, 2026, 2, 0, "skip", null, default);
        await handler.ActAsync(owner, r.Id, 2026, 2, 0, "restore", null, default);
        Assert.Equal(OccurrenceStatus.Due, (await handler.QueryAsync(owner, 2026, 2, 2026, 2, 0, default)).Occurrences.Single().Status);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => handler.ActAsync(owner, r.Id, 2026, 10, 0, "confirm", new(25, "actual", Now.AddDays(-1)), default)));
        Assert.All(results, result => Assert.Equal(RecurringResultStatus.Ok, result.Status));
        var actual = Assert.Single(transactions.Transactions); Assert.Equal(25, actual.Amount); Assert.Equal(20, r.Amount);
        Assert.All(results, result => Assert.Equal(actual.Id, result.Transaction!.Id));
        Assert.Equal(RecurringResultStatus.Conflict, (await handler.ActAsync(owner, r.Id, 2026, 10, 0, "restore", null, default)).Status);
        await handler.SaveAsync(owner, r.Id, input with { DayOfMonth = 15 }, default);
        Assert.Equal(OccurrenceStatus.Confirmed, (await handler.QueryAsync(owner, 2026, 10, 2026, 10, 0, default)).Occurrences.Single().Status);
        await handler.DeleteAsync(owner, r.Id, default);
        Assert.Empty(repo.Rules); Assert.Empty(repo.States); Assert.Single(transactions.Transactions);
    }

    [Theory]
    [InlineData(20, 800)] [InlineData(25, 795)] [InlineData(15, 805)]
    public void BudgetTransition_PreservesRemaining_UsesFreeToSpend(decimal actual, decimal expectedFree)
    {
        var r = Rule(); var budget = MonthlyBudget.Create(r.UserId, 2026, 10, "EUR", 1500);
        var spent = Transaction.CreateExpense(r.UserId, r.AccountId, r.CategoryId, 620, "EUR", Now, null, Now);
        var confirmed = Transaction.CreateExpense(r.UserId, r.AccountId, r.CategoryId, actual, "EUR", Now, null, Now);
        var from = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero); var to = from.AddMonths(1);
        var before = MonthlyBudgetCalculator.Build(budget, [spent], from, to, new(2026, 10, 10), 80);
        var after = MonthlyBudgetCalculator.Build(budget, [spent, confirmed], from, to, new(2026, 10, 10), 60);
        Assert.Equal(800, before.FreeToSpend); Assert.Equal(expectedFree, after.FreeToSpend);
        Assert.Equal(1500 - 620 - actual, after.Remaining); Assert.Equal(expectedFree / 22, after.SafeDailySpend);
        Assert.Equal(0, MonthlyBudgetCalculator.Build(budget, [spent], from, to, new(2026, 10, 10), 1000).SafeDailySpend);
    }
}
