using LifeOS.Contracts.Finance.PlannedExpenses;

namespace LifeOS.App.Services.Finance;

public static class PlannedExpensePlanning
{
    public static IReadOnlyList<PlannedExpenseResponse> ForMonth(IEnumerable<PlannedExpenseResponse> items, int year, int month) =>
        items.Where(i => i.ScheduledDate.Year == year && i.ScheduledDate.Month == month && i.Status is "Projected" or "Due")
            .OrderBy(i => i.ScheduledDate).ThenBy(i => i.Name).ToList();
}
