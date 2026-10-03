using LifeOS.Domain.Nutrition;

namespace LifeOS.Application.Nutrition;

// A meal with its current nutrition snapshot, if any.
public sealed record MealWithNutrition(MealEntry Meal, MealNutritionSnapshot? Nutrition);

// Meal nutrition snapshots (NUT-002). Every method is scoped to userId through the owning meal: another
// user's meal is indistinguishable from a missing one. At most one snapshot exists per meal.
public interface IMealNutritionRepository
{
    // The user's meals on one diary day with their snapshots, newest first (as the journal).
    Task<IReadOnlyList<MealWithNutrition>> GetDayAsync(Guid userId, DateOnly diaryDate, CancellationToken cancellationToken);

    Task<MealWithNutrition?> GetMealAsync(Guid userId, Guid mealEntryId, CancellationToken cancellationToken);

    // Meals on diary days before beforeDate that have no snapshot, at most limit of them, in a
    // deterministic order: most recent diary day first, then time and id ascending within a day.
    Task<IReadOnlyList<MealEntry>> GetUnanalyzedBeforeAsync(Guid userId, DateOnly beforeDate, int limit, CancellationToken cancellationToken);

    // An explicit decision: inserts the snapshot or replaces the meal's current one (keeping its
    // creation time). Only sources that MayReplaceExisting are accepted. False when the user has no
    // such meal.
    Task<bool> SaveAsync(Guid userId, MealNutritionSnapshot snapshot, CancellationToken cancellationToken);

    // Bulk/lazy analysis: inserts the snapshot only if the meal still exists, is the user's, still has
    // exactly the description that was estimated, and has no snapshot yet. Never overwrites. False
    // when nothing was inserted.
    Task<bool> AddIfMissingAsync(Guid userId, MealNutritionSnapshot snapshot, string estimatedDescription, CancellationToken cancellationToken);
}
