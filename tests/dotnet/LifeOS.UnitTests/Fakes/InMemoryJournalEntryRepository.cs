using System.Reflection;
using LifeOS.Application.Journal;
using LifeOS.Domain.Journal;

namespace LifeOS.UnitTests.Fakes;

// Every operation filters by userId, like the EF Core repository; ownership tests depend on it.
// Entries are stored and returned as copies, so a caller cannot change stored state without saving.
internal sealed class InMemoryJournalEntryRepository : IJournalEntryRepository
{
    private static readonly MethodInfo CloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly Lock _lock = new();

    public List<JournalEntry> Entries { get; } = [];

    public Task<IReadOnlyList<JournalEntry>> GetPageAsync(Guid userId, JournalCursor? after, int take, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<JournalEntry>>(Entries
                .Where(entry => entry.UserId == userId && (after is null || IsAfter(entry, after)))
                .OrderByDescending(entry => entry.OccurredAtUtc)
                .ThenByDescending(entry => entry.CreatedAtUtc)
                .ThenByDescending(entry => entry.Id)
                .Take(take)
                .Select(Clone)
                .ToList());
        }
    }

    public Task<JournalEntry?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var entry = Entries.SingleOrDefault(entry => entry.UserId == userId && entry.Id == id);

            return Task.FromResult(entry is null ? null : Clone(entry));
        }
    }

    public Task AddAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Entries.Add(Clone(entry));
        }

        return Task.CompletedTask;
    }

    public Task<bool> UpdateAsync(JournalEntry entry, CancellationToken cancellationToken)
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

    // Strictly after the cursor in timeline (descending) order.
    private static bool IsAfter(JournalEntry entry, JournalCursor cursor) =>
        entry.OccurredAtUtc < cursor.OccurredAtUtc
        || (entry.OccurredAtUtc == cursor.OccurredAtUtc
            && (entry.CreatedAtUtc < cursor.CreatedAtUtc
                || (entry.CreatedAtUtc == cursor.CreatedAtUtc && entry.Id.CompareTo(cursor.Id) < 0)));

    private static T Clone<T>(T value) where T : class => (T)CloneMethod.Invoke(value, null)!;
}
