using LifeOS.Domain.Finance.Budgets;
using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Application.Finance.Budgets;

public sealed record MonthlyBudgetSummary(Guid Id, int Year, int Month, string Currency,
    decimal Amount, decimal Spent, decimal Remaining, int? RemainingDays, decimal? SafeDailySpend);

public static class MonthlyBudgetCalculator
{
    public static MonthlyBudgetSummary Build(MonthlyBudget budget, IEnumerable<Transaction> transactions,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, DateOnly today)
    {
        var spent = transactions
            .Where(t => t.UserId == budget.UserId && t.Currency == budget.Currency
                && t.TransactionType == TransactionType.Expense
                && t.OccurredAtUtc >= fromUtc && t.OccurredAtUtc < toUtc)
            .DistinctBy(t => t.Id)
            .Sum(t => t.Amount);
        var remaining = budget.Amount - spent;
        int? days = today.Year == budget.Year && today.Month == budget.Month
            ? DateTime.DaysInMonth(budget.Year, budget.Month) - today.Day + 1 : null;
        decimal? safe = days is { } count ? Math.Max(0m, remaining) / count : null;
        return new(budget.Id, budget.Year, budget.Month, budget.Currency, budget.Amount, spent, remaining, days, safe);
    }
}
