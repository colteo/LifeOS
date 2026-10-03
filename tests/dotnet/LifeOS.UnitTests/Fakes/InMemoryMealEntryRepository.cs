using System.Reflection;
using LifeOS.Application.Nutrition;
using LifeOS.Domain.Nutrition;

namespace LifeOS.UnitTests.Fakes;

// Every operation filters by userId, like the EF Core repositories; ownership tests depend on it.
// Entries and snapshots are stored and returned as copies, so a caller cannot change stored state
// without saving. Implements both Nutrition ports over the same state, like the shared database:
// deleting a meal or changing its description (clearNutrition) removes its snapshot.
internal sealed class InMemoryMealEntryRepository : IMealEntryRepository, IMealNutritionRepository
{
    private static readonly MethodInfo CloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly Lock _lock = new();

    public List<MealEntry> Entries { get; } = [];

    public List<MealNutritionSnapshot> Snapshots { get; } = [];

    // Insertion order, deliberately not the journal order: the handler must order.
    public Task<IReadOnlyList<MealEntry>> GetForDiaryDateAsync(Guid userId, DateOnly diaryDate, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<MealEntry>>(Entries
                .Where(entry => entry.UserId == userId && entry.DiaryDate == diaryDate)
                .Select(Clone)
                .ToList());
        }
    }

    public Task<MealEntry?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var entry = Entries.SingleOrDefault(entry => entry.UserId == userId && entry.Id == id);

            return Task.FromResult(entry is null ? null : Clone(entry));
        }
    }

    public Task AddAsync(MealEntry entry, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Entries.Add(Clone(entry));
        }

        return Task.CompletedTask;
    }

    public Task<bool> UpdateAsync(MealEntry entry, bool clearNutrition, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var index = Entries.FindIndex(stored => stored.UserId == entry.UserId && stored.Id == entry.Id);

            if (index < 0)
            {
                return Task.FromResult(false);
            }

            Entries[index] = Clone(entry);

            if (clearNutrition)
            {
                Snapshots.RemoveAll(snapshot => snapshot.MealEntryId == entry.Id);
            }

            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var deleted = Entries.RemoveAll(entry => entry.UserId == userId && entry.Id == id) == 1;

            if (deleted)
            {
                Snapshots.RemoveAll(snapshot => snapshot.MealEntryId == id);
            }

            return Task.FromResult(deleted);
        }
    }

    // ---- IMealNutritionRepository ----

    // Insertion order, deliberately: handlers that need an order must apply it.
    public Task<IReadOnlyList<MealWithNutrition>> GetDayAsync(Guid userId, DateOnly diaryDate, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<MealWithNutrition>>(Entries
                .Where(entry => entry.UserId == userId && entry.DiaryDate == diaryDate)
                .Select(WithNutrition)
                .ToList());
        }
    }

    public Task<MealWithNutrition?> GetMealAsync(Guid userId, Guid mealEntryId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var entry = Entries.SingleOrDefault(entry => entry.UserId == userId && entry.Id == mealEntryId);

            return Task.FromResult(entry is null ? null : WithNutrition(entry));
        }
    }

    public Task<IReadOnlyList<MealEntry>> GetUnanalyzedBeforeAsync(Guid userId, DateOnly beforeDate, int limit, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<MealEntry>>(Entries
                .Where(entry => entry.UserId == userId && entry.DiaryDate < beforeDate && SnapshotOf(entry.Id) is null)
                .OrderByDescending(entry => entry.DiaryDate)
                .ThenBy(entry => entry.DiaryTime)
                .ThenBy(entry => entry.Id)
                .Take(limit)
                .Select(Clone)
                .ToList());
        }
    }

    public Task<bool> SaveAsync(Guid userId, MealNutritionSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!MealNutritionSnapshot.MayReplaceExisting(snapshot.Source))
        {
            throw new ArgumentException("Only explicit sources replace a snapshot.", nameof(snapshot));
        }

        lock (_lock)
        {
            if (!Entries.Any(entry => entry.UserId == userId && entry.Id == snapshot.MealEntryId))
            {
                return Task.FromResult(false);
            }

            if (SnapshotOf(snapshot.MealEntryId) is { } current)
            {
                current.Replace(snapshot.Values, snapshot.Source, snapshot.UpdatedAtUtc);
            }
            else
            {
                Snapshots.Add(Clone(snapshot));
            }

            return Task.FromResult(true);
        }
    }

    public Task<bool> AddIfMissingAsync(Guid userId, MealNutritionSnapshot snapshot, string estimatedDescription, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var meal = Entries.SingleOrDefault(entry => entry.UserId == userId && entry.Id == snapshot.MealEntryId);

            if (meal is null || meal.Description != estimatedDescription || SnapshotOf(meal.Id) is not null)
            {
                return Task.FromResult(false);
            }

            Snapshots.Add(Clone(snapshot));

            return Task.FromResult(true);
        }
    }

    public MealNutritionSnapshot? SnapshotOf(Guid mealEntryId)
    {
        lock (_lock)
        {
            return Snapshots.SingleOrDefault(snapshot => snapshot.MealEntryId == mealEntryId);
        }
    }

    private MealWithNutrition WithNutrition(MealEntry entry) =>
        new(Clone(entry), SnapshotOf(entry.Id) is { } snapshot ? Clone(snapshot) : null);

    private static T Clone<T>(T value) where T : class => (T)CloneMethod.Invoke(value, null)!;
}
