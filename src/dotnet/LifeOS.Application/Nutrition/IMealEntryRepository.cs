using LifeOS.Domain.Nutrition;

namespace LifeOS.Application.Nutrition;

// Every method is scoped to userId: another user's meal is indistinguishable from a missing one.
public interface IMealEntryRepository
{
    // The user's meals on one diary day, newest first (diary time, then id, descending).
    Task<IReadOnlyList<MealEntry>> GetForDiaryDateAsync(Guid userId, DateOnly diaryDate, CancellationToken cancellationToken);

    Task<MealEntry?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    Task AddAsync(MealEntry entry, CancellationToken cancellationToken);

    // Saves a meal previously read through GetAsync. Returns false when it no longer exists.
    // clearNutrition also removes the meal's nutrition snapshot, atomically with the update (NUT-002:
    // a changed description invalidates its nutrition).
    Task<bool> UpdateAsync(MealEntry entry, bool clearNutrition, CancellationToken cancellationToken);

    // Returns false when the user has no meal with this id. Its nutrition snapshot goes with it.
    Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken cancellationToken);
}
