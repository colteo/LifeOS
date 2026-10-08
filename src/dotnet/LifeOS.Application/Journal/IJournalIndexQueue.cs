namespace LifeOS.Application.Journal;

// AI-004: records, in the same unit of work as a journal write, that the entry's derived memory index
// must be (re)built. Pure database work: it never reaches the AI service, so saving an entry never
// depends on AI availability, and the memory index cannot silently miss a write.
//
// Deletes need nothing here: the request and every derived chunk cascade with the entry.
public interface IJournalIndexQueue
{
    // Inserts or replaces the entry's request: the revision of this write (its UpdatedAtUtc) wins, the
    // request time is refreshed and any lease held by an indexing run is cleared, because the source
    // it was reading has changed.
    Task EnqueueAsync(Guid userId, Guid entryId, DateTimeOffset sourceUpdatedAtUtc, DateTimeOffset requestedAtUtc, CancellationToken cancellationToken);
}
