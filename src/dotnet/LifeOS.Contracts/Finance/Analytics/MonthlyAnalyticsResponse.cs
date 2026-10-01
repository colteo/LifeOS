namespace LifeOS.Contracts.Finance.Analytics;

// External cash flow of one local calendar month (the half-open UTC range the client asked for),
// as one independent block per currency with income or expenses in it. Currencies are never added
// together. Transfers and opening balances are not cash flow and are excluded.
public sealed record MonthlyAnalyticsResponse(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    IReadOnlyList<CurrencyAnalyticsResponse> Currencies);

// Expenses and Income are positive quantities; NetFlow = Income - Expenses and may be negative.
// The expense categories add up exactly to Expenses.
public sealed record CurrencyAnalyticsResponse(
    string Currency,
    decimal Expenses,
    decimal Income,
    decimal NetFlow,
    IReadOnlyList<ExpenseCategoryAnalyticsResponse> ExpenseCategories);

// A top-level expense category with its current name. Amount = DirectAmount (expenses booked on
// the category itself) + the sum of its subcategories. CategoryId is null only for a defensive
// "Unknown category" group: an expense whose category cannot be resolved, which the database's
// restricting foreign keys make impossible today.
public sealed record ExpenseCategoryAnalyticsResponse(
    Guid? CategoryId,
    string Name,
    decimal Amount,
    decimal DirectAmount,
    IReadOnlyList<ExpenseSubcategoryAnalyticsResponse> Subcategories);

public sealed record ExpenseSubcategoryAnalyticsResponse(Guid CategoryId, string Name, decimal Amount);
