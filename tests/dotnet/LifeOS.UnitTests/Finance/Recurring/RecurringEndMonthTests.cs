using LifeOS.Application.Finance.Recurring;
using LifeOS.App.Services.Finance;
using LifeOS.Contracts.Finance.Recurring;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Recurring;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Recurring;

public class RecurringEndMonthTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static RecurringTransactionRule Rule(int? year = null, int? month = null, int startYear = 2026, int startMonth = 10) =>
        RecurringTransactionRule.Create(Guid.NewGuid(), "Installment", TransactionType.Expense,
            Guid.NewGuid(), Guid.NewGuid(), 20, 31, startYear, startMonth, null, Now, year, month);

    [Fact]
    public void NoEnd_IsIndefinite() => Assert.True(Rule().IncludesMonth(9998, 12));

    [Theory]
    [InlineData(2026, 10, 2026, 10, 31)]
    [InlineData(2027, 1, 2026, 12, 31)]
    [InlineData(2027, 2, 2026, 10, 28)]
    [InlineData(2028, 2, 2026, 10, 29)]
    [InlineData(2027, 4, 2026, 10, 30)]
    public void FinalMonth_IsInclusiveAndClamped(int endYear, int endMonth, int startYear, int startMonth, int day)
    {
        var rule = Rule(endYear, endMonth, startYear, startMonth);
        Assert.Equal(new DateOnly(endYear, endMonth, day), rule.ScheduledDate(endYear, endMonth));
        var next = new DateOnly(endYear, endMonth, 1).AddMonths(1);
        Assert.False(rule.IncludesMonth(next.Year, next.Month));
        Assert.Throws<ArgumentException>(() => rule.ScheduledDate(next.Year, next.Month));
        Assert.True(rule.IncludesMonth(startYear, startMonth));
    }

    [Theory]
    [InlineData(null, 10)] [InlineData(2026, null)] [InlineData(2026, 9)]
    [InlineData(2025, 12)] [InlineData(2027, 0)] [InlineData(2027, 13)]
    [InlineData(0, 1)] [InlineData(9999, 1)]
    public void InvalidEnd_IsRejected(int? year, int? month) => Assert.ThrowsAny<ArgumentException>(() => Rule(year, month));

    [Fact]
    public void InvalidEdit_DoesNotMutateRule()
    {
        var rule = Rule(2027, 5);
        Assert.Throws<ArgumentException>(() => rule.Update("Changed", rule.AccountId, rule.CategoryId, 99, 1, null, Now, 2026, 9));
        Assert.Equal("Installment", rule.Name); Assert.Equal(20, rule.Amount); Assert.Equal(5, rule.EndMonth);
    }

    [Fact]
    public async Task ShortenExtend_PreservesProcessedHistory_AndTransactionsPlanningStopsAtEnd()
    {
        var owner = Guid.NewGuid(); var accounts = new InMemoryAccountRepository(); var categories = new InMemoryCategoryRepository();
        var transactions = new InMemoryTransactionRepository(); var repo = new InMemoryRecurringRepository(accounts, categories, transactions);
        var account = Account.Create(owner, "Cash", AccountType.Cash, "EUR", Now);
        var category = Category.Create(owner, "Installments", CategoryType.Expense, null, Now);
        accounts.Accounts.Add(account); categories.Categories.Add(category);
        var h = new RecurringHandler(repo, new FixedTimeProvider(Now));
        var input = new SaveRecurringRule("Car", TransactionType.Expense, account.Id, category.Id, 20, 31, 2026, 2, null, 2027, 5);
        var rule = (await h.SaveAsync(owner, null, input, default)).Rule!;
        var confirmed = await h.ActAsync(owner, rule.Id, 2026, 3, 0, "confirm", new(25, "actual", Now.AddDays(-1)), default);
        await h.ActAsync(owner, rule.Id, 2026, 4, 0, "skip", null, default);
        await h.SaveAsync(owner, rule.Id, input with { EndYear = 2026, EndMonth = 2 }, default);
        Assert.Single((await h.QueryAsync(owner, 2026, 1, 2027, 6, 0, default)).Occurrences);
        Assert.Equal(confirmed.Transaction!.Id, Assert.Single(transactions.Transactions).Id);
        Assert.Equal(2, repo.States.Count);
        Assert.Equal(confirmed.Transaction.Id, (await h.ActAsync(owner, rule.Id, 2026, 3, 0, "confirm", new(99, null, Now), default)).Transaction!.Id);
        Assert.Equal(RecurringResultStatus.Invalid, (await h.ActAsync(owner, rule.Id, 2026, 5, 0, "skip", null, default)).Status);
        await h.SaveAsync(owner, rule.Id, input, default);
        var query = await h.QueryAsync(owner, 2027, 5, 2027, 6, 0, default);
        var final = Assert.Single(query.Occurrences);
        Assert.Equal(5, final.Month);
        var response = new RecurringOccurrenceResponse(final.RuleId, final.Name, "Expense", final.AccountId, final.CategoryId,
            final.Currency, final.ExpectedAmount, final.Note, final.Year, final.Month, final.ScheduledDate, "Projected", null);
        Assert.Single(RecurringPlanning.ForMonth([response], 2027, 5));
        Assert.Empty(RecurringPlanning.ForMonth([response], 2027, 6));
        var restored = await h.QueryAsync(owner, 2026, 3, 2026, 4, 0, default);
        Assert.Equal(new[] { OccurrenceStatus.Confirmed, OccurrenceStatus.Skipped }, restored.Occurrences.Select(o => o.Status));
        Assert.Empty(RecurringPlanning.ForMonth([response with { Status = "Confirmed" }, response with { Status = "Skipped" }], 2027, 5));
        await h.SaveAsync(owner, rule.Id, input with { EndYear = null, EndMonth = null }, default);
        Assert.Single((await h.QueryAsync(owner, 2027, 6, 2027, 6, 0, default)).Occurrences);
    }

    [Theory]
    [InlineData(null, true)] [InlineData("", true)] [InlineData("2027-05", true)]
    [InlineData("2027-13", false)] [InlineData("05/2027", false)] [InlineData("9999-01", false)]
    public void AppEndInput_ParsesCalendarMonth(string? input, bool valid)
    {
        Assert.Equal(valid, RecurringMonthInput.TryParseOptional(input, out var year, out var month));
        if (input == "2027-05") { Assert.Equal(2027, year); Assert.Equal(5, month); Assert.Equal("May 2027", RecurringMonthInput.EndLabel(year, month)); }
        if (string.IsNullOrWhiteSpace(input)) Assert.Equal("No end", RecurringMonthInput.EndLabel(year, month));
    }
}
