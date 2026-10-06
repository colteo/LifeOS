namespace LifeOS.Domain.WeeklyReviews;

// AUTO-002: the saved content of a weekly review, data version 1. A deterministic copy of the
// Finance, Gym and Nutrition figures available when the review was generated; it never changes
// afterwards, whatever happens to the source data. Amounts are exact decimals; every list is in a
// fixed order. Created only by the Application layer from existing module calculations.
public sealed record WeeklyReviewSnapshot(
    WeeklyFinanceSummary Finance,
    WeeklyGymSummary Gym,
    WeeklyNutritionSummary Nutrition);

// One entry per currency (currencies are never added together), ordered by currency code.
public sealed record WeeklyFinanceSummary(IReadOnlyList<WeeklyCurrencySummary> Currencies);

// Expenses and Income are positive; NetFlow = Income - Expenses. Transfers are not cash flow.
// Expense categories are the top-level groups of Finance Analytics (largest first), with the
// category names as they were at generation time.
public sealed record WeeklyCurrencySummary(
    string Currency,
    decimal Expenses,
    decimal Income,
    decimal NetFlow,
    IReadOnlyList<WeeklyExpenseCategory> ExpenseCategories);

public sealed record WeeklyExpenseCategory(string Name, decimal Amount);

// Completed workouts only (in-progress workouts are not history), ordered by completion time.
public sealed record WeeklyGymSummary(
    int CompletedWorkouts,
    long TotalDurationSeconds,
    int CompletedSets,
    int PrescribedSets,
    IReadOnlyList<WeeklyWorkout> Workouts);

// Date: the local date the workout was completed. Names as snapshotted when it started.
public sealed record WeeklyWorkout(
    DateOnly Date,
    string WorkoutName,
    string ProgramName,
    long DurationSeconds,
    int CompletedSets,
    int PrescribedSets);

// Totals are the sums of ANALYZED meals only and always come with the meal counts: unanalyzed meals
// are counted, never estimated. Days lists only diary days with at least one meal, by date.
public sealed record WeeklyNutritionSummary(
    int DaysWithMeals,
    int MealCount,
    int AnalyzedMealCount,
    int FullyAnalyzedDays,
    decimal AnalyzedCaloriesKcal,
    decimal AnalyzedProteinGrams,
    decimal AnalyzedCarbsGrams,
    decimal AnalyzedFatGrams,
    IReadOnlyList<WeeklyNutritionDay> Days);

public sealed record WeeklyNutritionDay(
    DateOnly Date,
    int MealCount,
    int AnalyzedMealCount,
    decimal CaloriesKcal,
    decimal ProteinGrams,
    decimal CarbsGrams,
    decimal FatGrams);
