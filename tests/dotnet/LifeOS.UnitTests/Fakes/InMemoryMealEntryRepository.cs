using System.Reflection;
using LifeOS.Application.Nutrition;
using LifeOS.Domain.Nutrition;

namespace LifeOS.UnitTests.Fakes;

// Every operation filters by userId, like the EF Core repository; ownership tests depend on it.
// Entries are stored and returned as copies, so a caller cannot change stored state without saving.
internal sealed class InMemoryMealEntryRepository : IMealEntryRepository
{
    private static readonly MethodInfo CloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly Lock _lock = new();

    public List<MealEntry> Entries { get; } = [];

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

    public Task<bool> UpdateAsync(MealEntry entry, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var index = Entries.FindIndex(stored => stored.UserId == entry.UserId && stored.Id == entry.Id);

            if (index < 0)
            {
                return Task.FromResult(false);
            }

            Entries[index] = Clone(entry);

            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Entries.RemoveAll(entry => entry.UserId == userId && entry.Id == id) == 1);
        }
    }

    private static MealEntry Clone(MealEntry entry) => (MealEntry)CloneMethod.Invoke(entry, null)!;
}
