using LifeOS.Application.Journal;
using LifeOS.Application.Memory;

namespace LifeOS.UnitTests.Fakes;

// AI-004: the journal's derived memory index in memory: the index queue (written by the Journal
// handlers) and the memory store (used by the memory handlers) share this state, like the two tables
// in PostgreSQL. Jobs and chunks of entries that no longer exist in the journal are dropped first on
// every operation, simulating the ON DELETE CASCADE foreign keys. Every operation filters by user.
//
// Retrieval here is a deterministic stand-in (user + identity filter, word overlap, then entry order):
// the real hybrid retrieval is the PostgreSQL function, tested against PostgreSQL.
internal sealed class InMemoryJournalMemory(InMemoryJournalEntryRepository journal) : IJournalIndexQueue, IJournalMemoryStore
{
    private readonly Lock _lock = new();

    public Dictionary<Guid, Job> Jobs { get; } = [];

    public List<StoredChunk> Chunks { get; } = [];

    public int Enqueued { get; private set; }

    public int Searches { get; private set; }

    public Task EnqueueAsync(Guid userId, Guid entryId, DateTimeOffset sourceUpdatedAtUtc, DateTimeOffset requestedAtUtc, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Jobs[entryId] = new Job(userId, entryId, sourceUpdatedAtUtc, requestedAtUtc, null, null);
            Enqueued++;
        }

        return Task.CompletedTask;
    }

    public Task<JournalMemoryCounts> GetCountsAsync(Guid userId, JournalMemoryIndexIdentity identity, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Cascade();

            return Task.FromResult(new JournalMemoryCounts(
                Chunks.Where(chunk => chunk.UserId == userId && chunk.Identity == identity).Select(chunk => chunk.EntryId).Distinct().Count(),
                Jobs.Values.Count(job => job.UserId == userId)));
        }
    }

    public Task<JournalIndexClaim?> TryClaimNextAsync(Guid userId, Guid leaseToken, DateTimeOffset nowUtc, DateTimeOffset leaseUntilUtc, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Cascade();

            var job = Jobs.Values
                .Where(job => job.UserId == userId && (job.LeaseUntilUtc is null || job.LeaseUntilUtc <= nowUtc))
                .OrderBy(job => job.RequestedAtUtc).ThenBy(job => job.EntryId)
                .FirstOrDefault();

            if (job is null)
            {
                return Task.FromResult<JournalIndexClaim?>(null);
            }

            Jobs[job.EntryId] = job with { LeaseToken = leaseToken, LeaseUntilUtc = leaseUntilUtc };
            var entry = journal.Entries.Single(entry => entry.Id == job.EntryId && entry.UserId == userId);

            return Task.FromResult<JournalIndexClaim?>(new JournalIndexClaim(userId, job.EntryId, job.SourceUpdatedAtUtc, leaseToken, entry.Title, entry.Content));
        }
    }

    public Task ReleaseAsync(JournalIndexClaim claim, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (Jobs.TryGetValue(claim.EntryId, out var job) && job.UserId == claim.UserId && job.LeaseToken == claim.LeaseToken)
            {
                Jobs[claim.EntryId] = job with { LeaseToken = null, LeaseUntilUtc = null };
            }
        }

        return Task.CompletedTask;
    }

    public Task<JournalIndexCompletion> CompleteAsync(JournalIndexClaim claim, JournalIndexedEntry indexed, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Cascade();

            var entry = journal.Entries.SingleOrDefault(entry => entry.Id == claim.EntryId && entry.UserId == claim.UserId);

            if (entry is null)
            {
                return Task.FromResult(JournalIndexCompletion.Gone);
            }

            if (!Jobs.TryGetValue(claim.EntryId, out var job)
                || job.LeaseToken != claim.LeaseToken
                || job.SourceUpdatedAtUtc != claim.SourceUpdatedAtUtc
                || entry.UpdatedAtUtc != claim.SourceUpdatedAtUtc
                || entry.Title != claim.Title
                || entry.Content != claim.Content)
            {
                return Task.FromResult(JournalIndexCompletion.Stale);
            }

            Chunks.RemoveAll(chunk => chunk.EntryId == claim.EntryId);
            Chunks.AddRange(indexed.Chunks.Select(chunk => new StoredChunk(claim.UserId, claim.EntryId, chunk.Ordinal, chunk.Text, claim.SourceUpdatedAtUtc, indexed.Identity)));
            Jobs.Remove(claim.EntryId);

            return Task.FromResult(JournalIndexCompletion.Indexed);
        }
    }

    public Task<IReadOnlyList<JournalMemoryHit>> SearchAsync(Guid userId, float[] queryEmbedding, string queryText, JournalMemoryIndexIdentity identity, int limit, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Cascade();
            Searches++;

            var words = queryText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => word.Trim('?', '.', '!').ToLowerInvariant()).ToHashSet();

            var hits = Chunks
                .Where(chunk => chunk.UserId == userId && chunk.Identity == identity)
                .Select(chunk => (Chunk: chunk, Overlap: chunk.Text.Split(' ').Count(word => words.Contains(word.Trim('?', '.', '!', ',').ToLowerInvariant()))))
                .Where(candidate => candidate.Overlap > 0)
                .OrderByDescending(candidate => candidate.Overlap).ThenBy(candidate => candidate.Chunk.EntryId).ThenBy(candidate => candidate.Chunk.Ordinal)
                .Take(limit)
                .Select((candidate, index) =>
                {
                    var entry = journal.Entries.Single(entry => entry.Id == candidate.Chunk.EntryId);
                    return new JournalMemoryHit(entry.Id, entry.OccurredAtUtc, entry.Title, candidate.Chunk.Ordinal, candidate.Chunk.Text,
                        index + 1, index + 1, null, 1.0 / (61 + index));
                })
                .ToList();

            return Task.FromResult<IReadOnlyList<JournalMemoryHit>>(hits);
        }
    }

    // Jobs and chunks of deleted entries disappear (ON DELETE CASCADE).
    private void Cascade()
    {
        var existing = journal.Entries.Select(entry => entry.Id).ToHashSet();

        foreach (var entryId in Jobs.Keys.Where(id => !existing.Contains(id)).ToList())
        {
            Jobs.Remove(entryId);
        }

        Chunks.RemoveAll(chunk => !existing.Contains(chunk.EntryId));
    }

    internal sealed record Job(Guid UserId, Guid EntryId, DateTimeOffset SourceUpdatedAtUtc, DateTimeOffset RequestedAtUtc, Guid? LeaseToken, DateTimeOffset? LeaseUntilUtc);

    internal sealed record StoredChunk(Guid UserId, Guid EntryId, int Ordinal, string Text, DateTimeOffset SourceUpdatedAtUtc, JournalMemoryIndexIdentity Identity);
}
