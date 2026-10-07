using LifeOS.Domain.Journal;

namespace LifeOS.Application.Journal;

// Every method is scoped to userId: another user's entry is indistinguishable from a missing one.
public interface IJournalEntryRepository
{
    // Up to take of the user's entries in timeline order (OccurredAtUtc, CreatedAtUtc, Id, all
    // descending), starting strictly after the cursor when one is given.
    Task<IReadOnlyList<JournalEntry>> GetPageAsync(Guid userId, JournalCursor? after, int take, CancellationToken cancellationToken);

    Task<JournalEntry?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    Task AddAsync(JournalEntry entry, CancellationToken cancellationToken);

    // Saves an entry previously read through GetAsync. Returns false when it no longer exists.
    Task<bool> UpdateAsync(JournalEntry entry, CancellationToken cancellationToken);

    // Hard delete. Returns false when the user has no entry with this id.
    Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken cancellationToken);
}
