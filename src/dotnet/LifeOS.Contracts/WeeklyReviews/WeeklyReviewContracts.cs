namespace LifeOS.Contracts.WeeklyReviews;

// One page of saved weekly reviews, newest week first. NextCursor continues the list (pass it as
// "cursor"); null on the last page.
public sealed record WeeklyReviewPageResponse(IReadOnlyList<WeeklyReviewListItemResponse> Items, string? NextCursor);

// WeekStartDate/WeekEndDate: the local Monday and Sunday of the review.
public sealed record WeeklyReviewListItemResponse(Guid Id, DateOnly WeekStartDate, DateOnly WeekEndDate, DateTimeOffset GeneratedAtUtc);

// A saved weekly review exactly as generated (a snapshot: later edits of the source data never
// change it). DataVersion is the snapshot's shape version.
public sealed record WeeklyReviewResponse(
    Guid Id,
    DateOnly WeekStartDate,
    DateOnly WeekEndDate,
    string TimeZoneId,
    DateTimeOffset GeneratedAtUtc,
    int DataVersion,
    WeeklyFinanceResponse Finance,
    WeeklyGymResponse Gym,
    WeeklyNutritionResponse Nutrition);

// One entry per currency; currencies are never added together.
public sealed record WeeklyFinanceResponse(IReadOnlyList<WeeklyCurrencyResponse> Currencies);

// Expenses and Income are positive; NetFlow = Income - Expenses. Categories: top-level expense
// groups, largest first.
public sealed record WeeklyCurrencyResponse(
    string Currency,
    decimal Expenses,
    decimal Income,
    decimal NetFlow,
    IReadOnlyList<WeeklyExpenseCategoryResponse> ExpenseCategories);

public sealed record WeeklyExpenseCategoryResponse(string Name, decimal Amount);

// Completed workouts of the week, in completion order.
public sealed record WeeklyGymResponse(
    int CompletedWorkouts,
    long TotalDurationSeconds,
    int CompletedSets,
    int PrescribedSets,
    IReadOnlyList<WeeklyWorkoutResponse> Workouts);

public sealed record WeeklyWorkoutResponse(
    DateOnly Date,
    string WorkoutName,
    string ProgramName,
    long DurationSeconds,
    int CompletedSets,
    int PrescribedSets);

// Totals cover ANALYZED meals only; show them with AnalyzedMealCount of MealCount. Days: diary days
// with at least one meal.
public sealed record WeeklyNutritionResponse(
    int DaysWithMeals,
    int MealCount,
    int AnalyzedMealCount,
    int FullyAnalyzedDays,
    decimal AnalyzedCaloriesKcal,
    decimal AnalyzedProteinGrams,
    decimal AnalyzedCarbsGrams,
    decimal AnalyzedFatGrams,
    IReadOnlyList<WeeklyNutritionDayResponse> Days);

public sealed record WeeklyNutritionDayResponse(
    DateOnly Date,
    int MealCount,
    int AnalyzedMealCount,
    decimal CaloriesKcal,
    decimal ProteinGrams,
    decimal CarbsGrams,
    decimal FatGrams);

// The automatic weekly review (Sunday 20:00 local time). Enabled unless the user turned it off.
public sealed record WeeklyReviewSettingsResponse(bool Enabled);

// Enabled is required.
public sealed record SetWeeklyReviewSettingsRequest(bool? Enabled);
