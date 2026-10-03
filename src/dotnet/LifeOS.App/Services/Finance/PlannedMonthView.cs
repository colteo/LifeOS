using LifeOS.Contracts.Finance.PlannedExpenses;
using LifeOS.Contracts.Finance.Recurring;

namespace LifeOS.App.Services.Finance;

// Transactions has two views of the selected month: what actually happened and what is planned.
public enum TransactionsTab { Actual, Planned }

public static class TransactionsTabs
{
    public const string QueryName = "tab";

    // Unknown or missing values open Actual, the default history view.
    public static TransactionsTab Parse(string? value) =>
        string.Equals(value, "planned", StringComparison.OrdinalIgnoreCase) ? TransactionsTab.Planned : TransactionsTab.Actual;

    public static string? QueryValue(TransactionsTab tab) => tab == TransactionsTab.Planned ? "planned" : null;
}

// The selected month's unresolved planning, kept as two distinct kinds: recurring rule occurrences
// (Confirm/Skip) and one-off planned expenses (Confirm/Cancel). Processed items are excluded; confirmed
// ones appear only as actual transactions.
public sealed record PlannedMonthView(
    IReadOnlyList<RecurringOccurrenceResponse> Recurring,
    IReadOnlyList<PlannedExpenseResponse> OneOff)
{
    public static PlannedMonthView Empty { get; } = new([], []);

    public static PlannedMonthView ForMonth(
        IEnumerable<RecurringOccurrenceResponse> occurrences, IEnumerable<PlannedExpenseResponse> oneOff, int year, int month) =>
        new(RecurringPlanning.ForMonth(occurrences, year, month), PlannedExpensePlanning.ForMonth(oneOff, year, month));

    public int DueCount => Recurring.Count(o => o.Status == "Due") + OneOff.Count(i => i.Status == "Due");

    public bool IsEmpty => Recurring.Count == 0 && OneOff.Count == 0;

    // Both kinds share one wording: Projected reads as "Expected" in the UI.
    public static string StatusLabel(string status) => status == "Projected" ? "Expected" : status;

    public static string PlannedTabLabel(int dueCount) => dueCount > 0 ? $"Planned · {dueCount} due" : "Planned";
}
