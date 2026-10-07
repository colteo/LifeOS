using LifeOS.Application.Journal;
using LifeOS.Domain.Journal;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Journal;

// Every query filters by user_id; a foreign id behaves exactly like a missing one.
internal sealed class JournalEntryRepository(LifeOSDbContext dbContext) : IJournalEntryRepository
{
    // Keyset paging over ix_journal_entries_user_timeline: rows strictly after the cursor triple.
    public async Task<IReadOnlyList<JournalEntry>> GetPageAsync(Guid userId, JournalCursor? after, int take, CancellationToken cancellationToken)
    {
        var entries = dbContext.JournalEntries
            .AsNoTracking()
            .Where(entry => entry.UserId == userId);

        if (after is not null)
        {
            var occurredAt = after.OccurredAtUtc.ToUniversalTime();
            var createdAt = after.CreatedAtUtc.ToUniversalTime();
            var id = after.Id;

            entries = entries.Where(entry =>
                entry.OccurredAtUtc < occurredAt
                || (entry.OccurredAtUtc == occurredAt
                    && (entry.CreatedAtUtc < createdAt
                        || (entry.CreatedAtUtc == createdAt && entry.Id.CompareTo(id) < 0))));
        }

        return await entries
            .OrderByDescending(entry => entry.OccurredAtUtc)
            .ThenByDescending(entry => entry.CreatedAtUtc)
            .ThenByDescending(entry => entry.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public Task<JournalEntry?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        dbContext.JournalEntries
            .AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.UserId == userId && entry.Id == id, cancellationToken);

    public async Task AddAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        dbContext.JournalEntries.Add(entry);
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.Entry(entry).State = EntityState.Detached;
    }

    // The owner and creation time never change, so only the edited columns are written.
    public async Task<bool> UpdateAsync(JournalEntry entry, CancellationToken cancellationToken) =>
        await dbContext.JournalEntries
            .Where(stored => stored.UserId == entry.UserId && stored.Id == entry.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(stored => stored.OccurredAtUtc, entry.OccurredAtUtc)
                .SetProperty(stored => stored.Title, entry.Title)
                .SetProperty(stored => stored.Content, entry.Content)
                .SetProperty(stored => stored.UpdatedAtUtc, entry.UpdatedAtUtc), cancellationToken) == 1;

    public async Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        await dbContext.JournalEntries
            .Where(entry => entry.UserId == userId && entry.Id == id)
            .ExecuteDeleteAsync(cancellationToken) == 1;
}
