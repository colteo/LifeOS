using LifeOS.Application.Memory;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Memory;

// AI-004: the derived memory index in PostgreSQL (journal_memory_chunks, journal_memory_index_queue and
// the production retrieval function search_journal_memory_v1). Every statement filters by user_id.
//
// Concurrency: a claim is ONE statement (FOR UPDATE SKIP LOCKED + lease), so two runs never hold the
// same job; nothing here is open while the AI service is called; completion is one short transaction
// that locks the journal row first (FOR SHARE) and then the job (FOR UPDATE). Journal updates and
// deletes lock the journal row before the job too, so the lock order is the same everywhere and a
// completion cannot deadlock with them.
internal sealed class JournalMemoryStore(LifeOSDbContext dbContext) : IJournalMemoryStore
{
    public async Task<JournalMemoryCounts> GetCountsAsync(Guid userId, JournalMemoryIndexIdentity identity, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Database.SqlQuery<CountsRow>($"""
            SELECT
                (SELECT count(DISTINCT c.entry_id)::int
                 FROM journal_memory_chunks c
                 WHERE c.user_id = {userId}
                   AND c.chunking_version = {identity.ChunkingVersion}
                   AND c.embedding_provider = {identity.EmbeddingProvider}
                   AND c.embedding_model = {identity.EmbeddingModel}) AS "IndexedEntries",
                (SELECT count(*)::int
                 FROM journal_memory_index_queue q
                 WHERE q.user_id = {userId}) AS "PendingEntries"
            """).ToListAsync(cancellationToken);

        return new JournalMemoryCounts(rows[0].IndexedEntries, rows[0].PendingEntries);
    }

    public async Task<JournalIndexClaim?> TryClaimNextAsync(Guid userId, Guid leaseToken, DateTimeOffset nowUtc, DateTimeOffset leaseUntilUtc, CancellationToken cancellationToken)
    {
        var now = nowUtc.ToUniversalTime();
        var until = leaseUntilUtc.ToUniversalTime();

        // Oldest request first; jobs leased by a live run are skipped (lease) and so are rows another
        // claim is taking right now (SKIP LOCKED). The entry's source is read in the same statement.
        var rows = await dbContext.Database.SqlQuery<ClaimRow>($"""
            WITH candidate AS (
                SELECT q.entry_id
                FROM journal_memory_index_queue q
                WHERE q.user_id = {userId}
                  AND (q.lease_until_utc IS NULL OR q.lease_until_utc <= {now})
                ORDER BY q.requested_at_utc, q.entry_id
                LIMIT 1
                FOR UPDATE OF q SKIP LOCKED
            )
            UPDATE journal_memory_index_queue AS q
            SET lease_token = {leaseToken}, lease_until_utc = {until}
            FROM candidate, journal_entries e
            WHERE q.entry_id = candidate.entry_id
              AND e.id = q.entry_id
              AND e.user_id = q.user_id
            RETURNING q.entry_id AS "EntryId", q.source_updated_at AS "SourceUpdatedAtUtc", e.title AS "Title", e.content AS "Content"
            """).ToListAsync(cancellationToken);

        return rows.Count == 0
            ? null
            : new JournalIndexClaim(userId, rows[0].EntryId, rows[0].SourceUpdatedAtUtc, leaseToken, rows[0].Title, rows[0].Content);
    }

    public async Task ReleaseAsync(JournalIndexClaim claim, CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlAsync($"""
            UPDATE journal_memory_index_queue
            SET lease_token = NULL, lease_until_utc = NULL
            WHERE entry_id = {claim.EntryId} AND user_id = {claim.UserId} AND lease_token = {claim.LeaseToken}
            """, cancellationToken);

    public async Task<JournalIndexCompletion> CompleteAsync(JournalIndexClaim claim, JournalIndexedEntry indexed, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var database = dbContext.Database;
        await using var transaction = await database.BeginTransactionAsync(cancellationToken);

        // 1. The journal row, locked against concurrent updates/deletes until commit.
        var entries = await database.SqlQuery<SourceRow>($"""
            SELECT e.updated_at_utc AS "UpdatedAtUtc", e.title AS "Title", e.content AS "Content"
            FROM journal_entries e
            WHERE e.id = {claim.EntryId} AND e.user_id = {claim.UserId}
            FOR SHARE
            """).ToListAsync(cancellationToken);

        if (entries.Count == 0)
        {
            // Deleted while embedding: its job and chunks are gone with it; nothing is recreated.
            await transaction.RollbackAsync(CancellationToken.None);
            return JournalIndexCompletion.Gone;
        }

        // 2. The job, locked.
        var jobs = await database.SqlQuery<JobRow>($"""
            SELECT q.source_updated_at AS "SourceUpdatedAtUtc", q.lease_token AS "LeaseToken"
            FROM journal_memory_index_queue q
            WHERE q.entry_id = {claim.EntryId} AND q.user_id = {claim.UserId}
            FOR UPDATE
            """).ToListAsync(cancellationToken);

        var source = entries[0];

        // 3. Still this run's lease, still the claimed revision, still exactly the text that was embedded.
        if (jobs.Count == 0
            || jobs[0].LeaseToken != claim.LeaseToken
            || jobs[0].SourceUpdatedAtUtc != claim.SourceUpdatedAtUtc
            || source.UpdatedAtUtc != claim.SourceUpdatedAtUtc
            || !string.Equals(source.Title, claim.Title, StringComparison.Ordinal)
            || !string.Equals(source.Content, claim.Content, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return JournalIndexCompletion.Stale;
        }

        // 4. Replace every chunk of the entry and finish the job, atomically.
        var identity = indexed.Identity;
        var ids = indexed.Chunks.Select(_ => Guid.CreateVersion7()).ToArray();
        var ordinals = indexed.Chunks.Select(chunk => chunk.Ordinal).ToArray();
        var texts = indexed.Chunks.Select(chunk => chunk.Text).ToArray();
        var vectors = indexed.Chunks.Select(chunk => JournalMemorySchema.VectorLiteral(chunk.Embedding)).ToArray();
        var revision = claim.SourceUpdatedAtUtc.ToUniversalTime();
        var now = nowUtc.ToUniversalTime();

        await database.ExecuteSqlAsync($"""
            DELETE FROM journal_memory_chunks WHERE entry_id = {claim.EntryId}
            """, cancellationToken);

        await database.ExecuteSqlAsync($"""
            INSERT INTO journal_memory_chunks (id, entry_id, user_id, ordinal, chunk_text, source_updated_at, chunking_version,
                                               embedding_provider, embedding_model, embedding_dimensions, embedding, created_at_utc)
            SELECT c.id, {claim.EntryId}, {claim.UserId}, c.ordinal, c.chunk_text, {revision}, {identity.ChunkingVersion},
                   {identity.EmbeddingProvider}, {identity.EmbeddingModel}, {identity.EmbeddingDimensions}, c.embedding::vector, {now}
            FROM unnest({ids}, {ordinals}, {texts}, {vectors}) AS c(id, ordinal, chunk_text, embedding)
            """, cancellationToken);

        await database.ExecuteSqlAsync($"""
            DELETE FROM journal_memory_index_queue
            WHERE entry_id = {claim.EntryId} AND lease_token = {claim.LeaseToken}
            """, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return JournalIndexCompletion.Indexed;
    }

    // The production retrieval function: hybrid semantic + lexical search fused with RRF, user-scoped.
    public async Task<IReadOnlyList<JournalMemoryHit>> SearchAsync(Guid userId, float[] queryEmbedding, string queryText, JournalMemoryIndexIdentity identity, int limit, CancellationToken cancellationToken)
    {
        var vector = JournalMemorySchema.VectorLiteral(queryEmbedding);

        var rows = await dbContext.Database.SqlQuery<HitRow>($"""
            SELECT s.entry_id AS "EntryId", s.occurred_at_utc AS "OccurredAtUtc", s.title AS "Title",
                   s.chunk_ordinal AS "ChunkOrdinal", s.chunk_text AS "ChunkText", s.retrieval_rank AS "Rank",
                   s.vector_rank AS "VectorRank", s.lexical_rank AS "LexicalRank", s.rrf_score AS "Score"
            FROM search_journal_memory_v1({userId}, {vector}::vector, {queryText}, {identity.EmbeddingProvider},
                                          {identity.EmbeddingModel}, {identity.ChunkingVersion}, {limit}) AS s
            ORDER BY s.retrieval_rank
            """).ToListAsync(cancellationToken);

        return rows
            .Select(row => new JournalMemoryHit(row.EntryId, row.OccurredAtUtc, row.Title, row.ChunkOrdinal, row.ChunkText,
                row.Rank, row.VectorRank, row.LexicalRank, row.Score))
            .ToList();
    }

    private sealed record CountsRow(int IndexedEntries, int PendingEntries);

    private sealed record ClaimRow(Guid EntryId, DateTimeOffset SourceUpdatedAtUtc, string? Title, string Content);

    private sealed record SourceRow(DateTimeOffset UpdatedAtUtc, string? Title, string Content);

    private sealed record JobRow(DateTimeOffset SourceUpdatedAtUtc, Guid? LeaseToken);

    private sealed record HitRow(Guid EntryId, DateTimeOffset OccurredAtUtc, string? Title, int ChunkOrdinal, string ChunkText,
        int Rank, int? VectorRank, int? LexicalRank, double Score);
}
