namespace LifeOS.Contracts.Finance.Budgets;

public sealed record MonthlyBudgetResponse(Guid Id, int Year, int Month, string Currency,
    decimal Amount, decimal Spent, decimal Remaining, int? RemainingDays, decimal? SafeDailySpend);

// Missing budget is a successful read, rather than a transport error.
public sealed record GetMonthlyBudgetResponse(MonthlyBudgetResponse? Budget);
public sealed record SetMonthlyBudgetRequest(decimal Amount);
