using LifeOS.Application.Journal;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Memory;

// AI-004: the journal write's memory-index request, on the same DbContext (and so the same transaction)
// as the journal write. Writes to one entry serialize on its row lock, and this upsert runs after the
// entry's write in that transaction, so the last write's revision is what the queue holds.
internal sealed class JournalIndexQueue(LifeOSDbContext dbContext) : IJournalIndexQueue
{
    public async Task EnqueueAsync(Guid userId, Guid entryId, DateTimeOffset sourceUpdatedAtUtc, DateTimeOffset requestedAtUtc, CancellationToken cancellationToken)
    {
        var source = sourceUpdatedAtUtc.ToUniversalTime();
        var requested = requestedAtUtc.ToUniversalTime();

        await dbContext.Database.ExecuteSqlAsync($"""
            INSERT INTO journal_memory_index_queue (entry_id, user_id, source_updated_at, requested_at_utc, lease_token, lease_until_utc)
            VALUES ({entryId}, {userId}, {source}, {requested}, NULL, NULL)
            ON CONFLICT (entry_id) DO UPDATE SET
                source_updated_at = EXCLUDED.source_updated_at,
                requested_at_utc = EXCLUDED.requested_at_utc,
                lease_token = NULL,
                lease_until_utc = NULL
            """, cancellationToken);
    }
}
