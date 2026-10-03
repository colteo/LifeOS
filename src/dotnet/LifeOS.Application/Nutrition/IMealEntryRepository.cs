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
    Task<bool> UpdateAsync(MealEntry entry, CancellationToken cancellationToken);

    // Returns false when the user has no meal with this id.
    Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken cancellationToken);
}
