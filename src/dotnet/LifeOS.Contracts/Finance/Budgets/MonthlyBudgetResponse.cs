namespace LifeOS.Contracts.Finance.Budgets;

public sealed record MonthlyBudgetResponse(Guid Id, int Year, int Month, string Currency,
    decimal Amount, decimal Spent, decimal Remaining, int? RemainingDays, decimal? SafeDailySpend,
    decimal ExpectedRecurringExpenses = 0, decimal FreeToSpend = 0, decimal ExpectedPlannedExpenses = 0)
{
    public decimal ExpectedExpensesTotal => ExpectedRecurringExpenses + ExpectedPlannedExpenses;
}

// Missing budget is a successful read, rather than a transport error.
public sealed record GetMonthlyBudgetResponse(MonthlyBudgetResponse? Budget);
public sealed record SetMonthlyBudgetRequest(decimal Amount);
