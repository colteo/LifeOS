using LifeOS.Application.Finance.Analytics.GetMonthlyAnalytics;
using LifeOS.Application.Finance.Transactions;
using LifeOS.Domain.Finance.Budgets;

namespace LifeOS.Application.Finance.Budgets;

public enum MonthlyBudgetStatus { Ok, Invalid }
public sealed record MonthlyBudgetResult(MonthlyBudgetStatus Status, MonthlyBudgetSummary? Budget, string? Field, string? Message)
{
    public static MonthlyBudgetResult Ok(MonthlyBudgetSummary? budget = null) => new(MonthlyBudgetStatus.Ok, budget, null, null);
    public static MonthlyBudgetResult Invalid(string field, string message) => new(MonthlyBudgetStatus.Invalid, null, field, message);
}

// Like Analytics, the caller supplies its local month as a half-open UTC range. The current
// device offset is used only to find today's calendar date from server time, never for boundaries
// (the offsets at those boundaries may differ because of DST).
public sealed record GetMonthlyBudgetQuery(int Year, int Month, string Currency,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, int UtcOffsetMinutes);
public sealed record SetMonthlyBudgetCommand(int Year, int Month, string Currency, decimal Amount);

public sealed class GetMonthlyBudgetHandler(IMonthlyBudgetRepository budgets, ITransactionRepository transactions, TimeProvider clock)
{
    public async Task<MonthlyBudgetResult> HandleAsync(Guid userId, GetMonthlyBudgetQuery query, CancellationToken cancellationToken)
    {
        string currency;
        try
        {
            MonthlyBudget.ValidateMonth(query.Year, query.Month);
            currency = MonthlyBudget.NormalizeCurrency(query.Currency);
        }
        catch (ArgumentException exception)
        {
            return MonthlyBudgetResult.Invalid(exception.ParamName!, exception.Message);
        }
        if (query.UtcOffsetMinutes is < -840 or > 840)
            return MonthlyBudgetResult.Invalid("utcOffsetMinutes", "UTC offset must be between -840 and 840 minutes.");
        if (query.FromUtc.Offset != TimeSpan.Zero || query.ToUtc.Offset != TimeSpan.Zero
            || query.FromUtc >= query.ToUtc || query.ToUtc - query.FromUtc > GetMonthlyAnalyticsHandler.MaxRange)
            return MonthlyBudgetResult.Invalid("toUtc", "Supply a half-open UTC month range of at most 32 days.");

        // Each boundary must be a possible local midnight for the selected calendar month.
        var first = new DateTimeOffset(query.Year, query.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var next = first.AddMonths(1);
        if (!IsBoundary(first - query.FromUtc) || !IsBoundary(next - query.ToUtc))
            return MonthlyBudgetResult.Invalid("fromUtc", "The range must match the selected calendar month boundaries.");

        var budget = await budgets.GetAsync(userId, query.Year, query.Month, currency, cancellationToken);
        if (budget is null) return MonthlyBudgetResult.Ok();
        var movements = await transactions.GetByOccurredRangeAsync(userId, query.FromUtc, query.ToUtc, cancellationToken);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromMinutes(query.UtcOffsetMinutes)).DateTime);
        return MonthlyBudgetResult.Ok(MonthlyBudgetCalculator.Build(budget, movements, query.FromUtc, query.ToUtc, today));
    }

    private static bool IsBoundary(TimeSpan offset) =>
        Math.Abs(offset.TotalMinutes) <= 840 && offset.Ticks % TimeSpan.TicksPerMinute == 0;
}

public sealed class SetMonthlyBudgetHandler(IMonthlyBudgetRepository budgets)
{
    public async Task<MonthlyBudgetResult> HandleAsync(Guid userId, SetMonthlyBudgetCommand command, CancellationToken cancellationToken)
    {
        MonthlyBudget budget;
        try { budget = MonthlyBudget.Create(userId, command.Year, command.Month, command.Currency, command.Amount); }
        catch (ArgumentException exception) { return MonthlyBudgetResult.Invalid(exception.ParamName!, exception.Message); }
        await budgets.SetAsync(budget, cancellationToken);
        return MonthlyBudgetResult.Ok();
    }
}

public sealed class DeleteMonthlyBudgetHandler(IMonthlyBudgetRepository budgets)
{
    public async Task<MonthlyBudgetResult> HandleAsync(Guid userId, int year, int month, string currency, CancellationToken cancellationToken)
    {
        try
        {
            MonthlyBudget.ValidateMonth(year, month);
            currency = MonthlyBudget.NormalizeCurrency(currency);
        }
        catch (ArgumentException exception) { return MonthlyBudgetResult.Invalid(exception.ParamName!, exception.Message); }
        await budgets.DeleteAsync(userId, year, month, currency, cancellationToken);
        return MonthlyBudgetResult.Ok();
    }
}
