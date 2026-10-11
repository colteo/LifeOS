using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    // AI-006.1: the Journal Memory index identity moves from google / gemini-embedding-2 to openai /
    // text-embedding-3-small (same journal-chunking-v1, same vector(1536)) and production retrieval from
    // search_journal_memory_v1 to search_journal_memory_v2 (docs/tasks/ai/AI-006.1.md). DATA ONLY: no
    // table, column, vector dimension, index or function changes; the EF model is unchanged.
    //
    // Up queues every journal entry once for the new identity; the explicit, bounded sync
    // (POST /api/journal/memory/sync) rebuilds the index. Pure database work: no provider is called.
    // - One job per entry (upsert on the entry id), for the entry's current revision, with any lease
    //   cleared: a run that claimed the job before the migration can no longer complete it (its lease
    //   token no longer matches), and nothing is duplicated.
    // - The journal rows are locked FOR SHARE first, the same journal-then-queue lock order as journal
    //   writes and index completion: a concurrent edit is either committed before (its revision is
    //   read) or waits until after (it upserts its own, newer revision); a concurrently deleted entry
    //   is skipped.
    // - Chunks of the old identity are NOT deleted here. Search and status filter by identity, so they
    //   are invisible and never mixed with OpenAI vectors; completing an entry's index deletes all of
    //   its chunks and inserts the new ones in one transaction.
    //
    // Down queues every entry again in exactly the same way, so a rolled-back runtime (old identity)
    // rebuilds its own index through the same sync. No vector is reconstructed and no journal row is
    // read for anything but its id, owner and revision.
    /// <inheritdoc />
    public partial class RequeueJournalMemoryForOpenAI : Migration
    {
        // Public so the PostgreSQL tests can race this exact statement against concurrent journal writes.
        public const string RequeueEveryEntry = """
            INSERT INTO journal_memory_index_queue (entry_id, user_id, source_updated_at, requested_at_utc, lease_token, lease_until_utc)
            SELECT e.id, e.user_id, e.updated_at_utc, now(), NULL, NULL
            FROM journal_entries e
            FOR SHARE OF e
            ON CONFLICT (entry_id) DO UPDATE SET
                source_updated_at = EXCLUDED.source_updated_at,
                requested_at_utc = EXCLUDED.requested_at_utc,
                lease_token = NULL,
                lease_until_utc = NULL;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RequeueEveryEntry);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RequeueEveryEntry);
        }
    }
}
