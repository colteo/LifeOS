using LifeOS.Application.Finance.Analytics;
using LifeOS.Application.Finance.Analytics.GetMonthlyAnalytics;
using LifeOS.Application.Gym.History;
using LifeOS.Application.Nutrition;
using LifeOS.Domain.WeeklyReviews;

namespace LifeOS.Application.WeeklyReviews;

// AUTO-002 §3: the weekly figures, read-only, from the modules' own use cases and calculations — no
// financial, nutrition or gym rule is reimplemented here:
//   Finance   — GetMonthlyAnalyticsHandler over the week's UTC range (MonthlyAnalyticsCalculator).
//   Gym       — GetWorkoutHistoryHandler pages (Completed sessions only) within the week.
//   Nutrition — the week's diary days in one read, each summarized by DailyNutritionSummary.From.
// It never estimates nutrition and never runs the lazy close: unanalyzed meals stay unanalyzed.
public sealed class WeeklyReviewSnapshotBuilder(
    GetMonthlyAnalyticsHandler analytics,
    GetWorkoutHistoryHandler workoutHistory,
    IMealNutritionRepository meals)
{
    public async Task<WeeklyReviewSnapshot> BuildAsync(Guid userId, WeeklyReviewPeriod period, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var finance = await analytics.HandleAsync(userId, new GetMonthlyAnalyticsQuery(period.StartUtc, period.EndUtc), cancellationToken);

        if (finance.Status != GetMonthlyAnalyticsStatus.Ok)
        {
            // Impossible for a valid week (a UTC range of 167–169 hours).
            throw new InvalidOperationException("The weekly finance range was rejected.");
        }

        var workouts = await ReadCompletedWorkoutsAsync(userId, period, cancellationToken);
        var days = await meals.GetDaysAsync(userId, period.StartDate, period.EndDate, cancellationToken);

        return Build(zone, finance.Currencies, workouts, days);
    }

    // Pure: the same inputs always give the same snapshot.
    public static WeeklyReviewSnapshot Build(
        TimeZoneInfo zone,
        IReadOnlyList<CurrencyAnalytics> currencies,
        IReadOnlyList<WorkoutHistoryItem> workouts,
        IReadOnlyList<MealWithNutrition> meals) =>
        new(BuildFinance(currencies), BuildGym(zone, workouts), BuildNutrition(meals));

    private static WeeklyFinanceSummary BuildFinance(IReadOnlyList<CurrencyAnalytics> currencies) =>
        new(currencies
            .OrderBy(currency => currency.Currency, StringComparer.Ordinal)
            .Select(currency => new WeeklyCurrencySummary(
                currency.Currency,
                currency.Expenses,
                currency.Income,
                currency.NetFlow,
                // Already in Analytics order (largest first, then name, then id).
                currency.ExpenseCategories.Select(category => new WeeklyExpenseCategory(category.Name, category.Amount)).ToList()))
            .ToList());

    private static WeeklyGymSummary BuildGym(TimeZoneInfo zone, IReadOnlyList<WorkoutHistoryItem> workouts)
    {
        var ordered = workouts
            .OrderBy(workout => workout.CompletedAtUtc)
            .ThenBy(workout => workout.Id)
            .Select(workout => new WeeklyWorkout(
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(workout.CompletedAtUtc, zone).DateTime),
                workout.WorkoutName,
                workout.ProgramName,
                DurationSeconds(workout),
                workout.CompletedSetCount,
                workout.PrescribedSetCount))
            .ToList();

        return new WeeklyGymSummary(
            ordered.Count,
            ordered.Sum(workout => workout.DurationSeconds),
            ordered.Sum(workout => workout.CompletedSets),
            ordered.Sum(workout => workout.PrescribedSets),
            ordered);
    }

    private static WeeklyNutritionSummary BuildNutrition(IReadOnlyList<MealWithNutrition> meals)
    {
        var days = meals
            .GroupBy(meal => meal.Meal.DiaryDate)
            .OrderBy(day => day.Key)
            .Select(day => DailyNutritionSummary.From(day.Key, day.ToList()))
            .ToList();

        return new WeeklyNutritionSummary(
            days.Count,
            days.Sum(day => day.MealCount),
            days.Sum(day => day.AnalyzedMealCount),
            days.Count(day => day.AllAnalyzed),
            days.Sum(day => day.CaloriesKcal),
            days.Sum(day => day.ProteinGrams),
            days.Sum(day => day.CarbsGrams),
            days.Sum(day => day.FatGrams),
            days.Select(day => new WeeklyNutritionDay(
                    day.Date, day.MealCount, day.AnalyzedMealCount, day.CaloriesKcal, day.ProteinGrams, day.CarbsGrams, day.FatGrams))
                .ToList());
    }

    // Whole seconds, as the session's own Duration (CompletedAtUtc - StartedAtUtc).
    private static long DurationSeconds(WorkoutHistoryItem workout) =>
        Math.Max(0L, (long)(workout.CompletedAtUtc - workout.StartedAtUtc).TotalSeconds);

    // Completed workouts with CompletedAtUtc inside the week, through the history use case: newest
    // first from the end of the week, page by page until a workout completed before the week.
    private async Task<IReadOnlyList<WorkoutHistoryItem>> ReadCompletedWorkoutsAsync(
        Guid userId,
        WeeklyReviewPeriod period,
        CancellationToken cancellationToken)
    {
        var inWeek = new List<WorkoutHistoryItem>();

        // Starts strictly before the end of the week: (EndUtc, empty id) excludes EndUtc itself.
        WorkoutHistoryCursor? after = new(period.EndUtc, Guid.Empty);

        while (after is not null)
        {
            var page = await workoutHistory.HandleAsync(userId, after, GetWorkoutHistoryHandler.MaxPageSize, cancellationToken);

            foreach (var item in page.Items)
            {
                if (item.CompletedAtUtc < period.StartUtc)
                {
                    return inWeek;
                }

                inWeek.Add(item);
            }

            after = page.Next;
        }

        return inWeek;
    }
}
