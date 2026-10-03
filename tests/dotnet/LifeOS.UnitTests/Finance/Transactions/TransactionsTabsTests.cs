using LifeOS.App.Services.Finance;
using LifeOS.Contracts.Finance.PlannedExpenses;
using LifeOS.Contracts.Finance.Recurring;

namespace LifeOS.UnitTests.Finance.Transactions;

public class TransactionsTabsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, TransactionsTab.Actual)] [InlineData("", TransactionsTab.Actual)]
    [InlineData("actual", TransactionsTab.Actual)] [InlineData("unknown", TransactionsTab.Actual)]
    [InlineData("planned", TransactionsTab.Planned)] [InlineData("Planned", TransactionsTab.Planned)]
    public void Parse_DefaultsToActual_AndRecognisesPlanned(string? value, TransactionsTab expected) =>
        Assert.Equal(expected, TransactionsTabs.Parse(value));

    [Fact]
    public void QueryValue_RoundTrips_AndActualOmitsTheParameter()
    {
        Assert.Null(TransactionsTabs.QueryValue(TransactionsTab.Actual));
        Assert.Equal(TransactionsTab.Planned, TransactionsTabs.Parse(TransactionsTabs.QueryValue(TransactionsTab.Planned)));
    }

    [Fact]
    public void ForMonth_KeepsRecurringAndOneOffSeparate_OnlyUnresolvedInSelectedMonth()
    {
        var recurring = new[]
        {
            Occurrence("Rent", 10, "Due"), Occurrence("Gym", 10, "Projected"),
            Occurrence("Salary", 10, "Confirmed"), Occurrence("Streaming", 10, "Skipped"), Occurrence("Next", 11, "Projected"),
        };
        var oneOff = new[]
        {
            Expense("Visa", 10, "Due"), Expense("Insurance", 10, "Projected"),
            Expense("Paid", 10, "Confirmed"), Expense("Dropped", 10, "Cancelled"), Expense("Later", 11, "Projected"),
        };

        var view = PlannedMonthView.ForMonth(recurring, oneOff, 2026, 10);

        Assert.Equal(["Gym", "Rent"], view.Recurring.Select(o => o.Name)); // Ordered by scheduled date.
        Assert.Equal(["Visa", "Insurance"], view.OneOff.Select(i => i.Name));
        Assert.Equal(2, view.DueCount);
        Assert.False(view.IsEmpty);
        Assert.True(PlannedMonthView.ForMonth(recurring, oneOff, 2026, 12).IsEmpty);
    }

    [Fact]
    public void Empty_HasNoPlans()
    {
        Assert.True(PlannedMonthView.Empty.IsEmpty);
        Assert.Equal(0, PlannedMonthView.Empty.DueCount);
    }

    [Theory]
    [InlineData("Projected", "Expected")] [InlineData("Due", "Due")]
    public void StatusLabel_UsesOneWordingForBothKinds(string status, string expected) =>
        Assert.Equal(expected, PlannedMonthView.StatusLabel(status));

    [Theory]
    [InlineData(0, "Planned")] [InlineData(1, "Planned · 1 due")] [InlineData(3, "Planned · 3 due")]
    public void PlannedTabLabel_ShowsDueCountOnlyWhenSomethingIsDue(int due, string expected) =>
        Assert.Equal(expected, PlannedMonthView.PlannedTabLabel(due));

    private static RecurringOccurrenceResponse Occurrence(string name, int month, string status) =>
        new(Guid.NewGuid(), name, "Expense", Guid.NewGuid(), Guid.NewGuid(), "EUR", 10, null, 2026, month,
            new DateOnly(2026, month, name.Length), status, status == "Confirmed" ? Guid.NewGuid() : null);

    private static PlannedExpenseResponse Expense(string name, int month, string status) =>
        new(Guid.NewGuid(), name, Guid.NewGuid(), Guid.NewGuid(), "EUR", 20, new DateOnly(2026, month, name.Length),
            null, status, status == "Confirmed" ? Guid.NewGuid() : null, Now, Now);
}
