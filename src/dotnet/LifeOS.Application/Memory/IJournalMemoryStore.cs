namespace LifeOS.Application.Memory;

// AI-004: the derived memory index of the journal (chunks with embeddings, and the queue of entries
// to (re)index). Never the source of truth: every row is rebuilt from journal_entries and cascades
// with it. Every method is scoped to one user: another user's queue rows and chunks are invisible.
//
// The store has no write path to journal entries.
public interface IJournalMemoryStore
{
    // Entries with chunks of this index identity, and entries waiting to be (re)indexed.
    Task<JournalMemoryCounts> GetCountsAsync(Guid userId, JournalMemoryIndexIdentity identity, CancellationToken cancellationToken);

    // Claims the user's oldest unleased (or lease-expired) job with leaseToken until leaseUntilUtc, in
    // one short statement that concurrent runs cannot both win, and returns it with the entry's
    // current source. Null when nothing is claimable.
    Task<JournalIndexClaim?> TryClaimNextAsync(Guid userId, Guid leaseToken, DateTimeOffset nowUtc, DateTimeOffset leaseUntilUtc, CancellationToken cancellationToken);

    // Makes a claimed job claimable again now, if this claim still holds its lease.
    Task ReleaseAsync(JournalIndexClaim claim, CancellationToken cancellationToken);

    // In one short transaction (no external call inside): writes the chunks ONLY when this claim still
    // holds the job's lease, the job still asks for the claimed revision and the entry still has that
    // revision and exactly the claimed text. Then it replaces all of the entry's chunks and deletes the
    // job atomically. Otherwise it changes nothing (Stale, or Gone when the entry no longer exists).
    Task<JournalIndexCompletion> CompleteAsync(JournalIndexClaim claim, JournalIndexedEntry indexed, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    // The production hybrid retrieval (RetrievalFunction): the user's best chunks of this identity,
    // best first. Never another user's chunks.
    Task<IReadOnlyList<JournalMemoryHit>> SearchAsync(Guid userId, float[] queryEmbedding, string queryText, JournalMemoryIndexIdentity identity, int limit, CancellationToken cancellationToken);
}

public sealed record JournalMemoryCounts(int IndexedEntries, int PendingEntries);

// A leased job with the source it must index: the entry's title and content as they were when claimed.
public sealed record JournalIndexClaim(Guid UserId, Guid EntryId, DateTimeOffset SourceUpdatedAtUtc, Guid LeaseToken, string? Title, string Content);

public enum JournalIndexCompletion
{
    Indexed,

    // The entry changed (or another run finished the job) while this one was embedding: discarded.
    Stale,

    // The entry was deleted meanwhile: discarded, nothing recreated.
    Gone
}

// One retrieved chunk. Rank is 1-based; VectorRank/LexicalRank are the component ranks (null when the
// chunk was not a candidate of that list); Score is the fused RRF score.
public sealed record JournalMemoryHit(
    Guid EntryId,
    DateTimeOffset OccurredAtUtc,
    string? Title,
    int ChunkOrdinal,
    string ChunkText,
    int Rank,
    int? VectorRank,
    int? LexicalRank,
    double Score);
