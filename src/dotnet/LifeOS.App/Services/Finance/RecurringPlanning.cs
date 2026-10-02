using LifeOS.Contracts.Finance.Recurring;

namespace LifeOS.App.Services.Finance;

public static class RecurringPlanning
{
    // The server's bounded projection applies the rule's inclusive start/end range.
    // Processed months belong in Recurring; confirmed movements already appear in history.
    public static IReadOnlyList<RecurringOccurrenceResponse> ForMonth(
        IEnumerable<RecurringOccurrenceResponse> occurrences, int year, int month) =>
        occurrences.Where(o => o.Year == year && o.Month == month && o.Status is "Due" or "Projected")
            .OrderBy(o => o.ScheduledDate).ToList();
}
