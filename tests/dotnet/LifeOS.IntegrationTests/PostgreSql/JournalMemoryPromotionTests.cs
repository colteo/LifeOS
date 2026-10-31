using LifeOS.Application.Journal;
using LifeOS.Application.Memory;
using LifeOS.Application.Persistence;
using LifeOS.Domain.Journal;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using LifeOS.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LifeOS.IntegrationTests.PostgreSql;

// AI-006.1: the data-only RequeueJournalMemoryForOpenAI migration against real PostgreSQL. The index
// identity moved from google / gemini-embedding-2 to openai / text-embedding-3-small (same chunking, same
// vector(1536)); the migration queues every journal entry once so the existing bounded sync rebuilds it,
// leaves the old chunks in place (invisible to the new identity) and calls no provider. Down queues every
// entry again for a rolled-back runtime.
//
// Each test steps the shared database down to AddJournalRetrievalV2Candidate, arranges the pre-promotion
// state there, and migrates up again in a finally (a failure can never leave the database downgraded).
// Both directions re-queue every entry of the shared database; every assertion is scoped to this test's
// own users.
[Collection(PostgreSqlCollection.Name)]
public class JournalMemoryPromotionTests(PostgreSqlFixture fixture)
{
    private const string PreviousMigration = "_AddJournalRetrievalV2Candidate";
    private const string PromotionMigration = "_RequeueJournalMemoryForOpenAI";
    // Later migrations that add new tables or columns only (HAB-001, REM-001, HYD-001, AI-007), and AI-008's
    // change to the assistant operations table.
    private static readonly string[] LaterMigrations =
        ["_AddHabits", "_AddReminders", "_AddReminderPreferences", "_AddHydrationIntakes", "_AddHydrationTargets", "_AddConditionalReminders",
         "_AddAssistantConversations", "_AddAssistantOperations", "_GeneralizeAssistantOperations"];

    private static readonly DateTimeOffset Day = new(2026, 7, 4, 18, 30, 0, TimeSpan.Zero);
    private static readonly JournalMemoryIndexIdentity OpenAI = JournalMemoryPolicy.Identity;
    private static readonly JournalMemoryIndexIdentity Gemini = new("journal-chunking-v1", "google", "gemini-embedding-2", 1536);

    [Fact]
    public async Task ThePromotionMigration_FollowsV2_OnlyNewTableMigrationsComeAfter_AndChangesNoModel()
    {
        await using var scope = fixture.CreateScope();
        var database = Db(scope).Database;
        var applied = (await database.GetAppliedMigrationsAsync()).ToList();

        // Only the additive migrations of later modules follow the promotion.
        Assert.Equal([PreviousMigration, PromotionMigration, .. LaterMigrations],
            applied.TakeLast(LaterMigrations.Length + 2).Select(id => id[14..]));
        Assert.False(database.HasPendingModelChanges());
        Assert.Equal(new JournalMemoryIndexIdentity("journal-chunking-v1", "openai", "text-embedding-3-small", 1536), OpenAI);
    }

    // A: an entry fully indexed with Gemini (chunks, no job) gets exactly one job for its current revision;
    // its Gemini chunks stay but are invisible to the OpenAI identity (search and counts).
    // F: completing that job with OpenAI replaces every old chunk; only 1536-dimensional OpenAI chunks remain.
    [Fact]
    public async Task AGeminiIndexedEntry_IsQueuedOnce_KeepsItsInvisibleChunks_AndAnOpenAICompletionReplacesThem()
    {
        var user = await NewUserAsync();
        JournalEntry entry = null!;
        DateTimeOffset before = default;
        string source = "";

        await PromoteAsync(async () =>
        {
            entry = await CreateAsync(user.Id, "Il mare. Giornata di sole a Zanzibar.");
            await IndexAsync(user.Id, entry, [("Il mare.", Angle(0)), ("Giornata di sole a Zanzibar.", Angle(5))], Gemini);

            Assert.Empty(await JobsAsync(user.Id));
            Assert.Equal(["google gemini-embedding-2", "google gemini-embedding-2"], await ChunkIdentitiesAsync(entry.Id));
            source = await JournalSourceAsync(user.Id);
            before = await DatabaseNowAsync();
        });

        var job = Assert.Single(await JobsAsync(user.Id));
        Assert.Equal((entry.Id, entry.UpdatedAtUtc), (job.EntryId, job.SourceUpdatedAt));
        Assert.Null(job.LeaseToken);
        Assert.Null(job.LeaseUntilUtc);
        Assert.True(job.RequestedAtUtc >= before);
        Assert.Equal(["google gemini-embedding-2", "google gemini-embedding-2"], await ChunkIdentitiesAsync(entry.Id));
        Assert.Equal(source, await JournalSourceAsync(user.Id));

        // Status reports the OpenAI index only: nothing indexed yet, one entry pending (index incomplete).
        Assert.Equal(new JournalMemoryCounts(0, 1), await CountsAsync(user.Id));
        // Search never falls back to the Gemini rows, lexically or semantically; they exist only for their
        // own identity.
        Assert.Empty(await SearchAsync(user.Id, Angle(0), "Zanzibar", OpenAI));
        Assert.Equal(2, (await SearchAsync(user.Id, Angle(0), "Zanzibar", Gemini)).Count);

        // The existing sync path: claim, then complete with the OpenAI identity.
        var claim = await ClaimAsync(user.Id);
        Assert.Equal((entry.Id, entry.UpdatedAtUtc), (claim!.EntryId, claim.SourceUpdatedAtUtc));
        Assert.Equal(JournalIndexCompletion.Indexed, await CompleteAsync(claim, [("Il mare. Giornata di sole a Zanzibar.", Angle(1))], OpenAI));

        Assert.Equal(["openai text-embedding-3-small"], await ChunkIdentitiesAsync(entry.Id));
        Assert.Equal(["1536 1536"], await QueryAsync(
            $"SELECT embedding_dimensions::text || ' ' || vector_dims(embedding)::text AS \"Value\" FROM journal_memory_chunks WHERE entry_id = '{entry.Id}'"));
        Assert.Empty(await JobsAsync(user.Id));
        Assert.Equal(new JournalMemoryCounts(1, 0), await CountsAsync(user.Id));
        Assert.Equal([entry.Id], (await SearchAsync(user.Id, Angle(0), "Zanzibar", OpenAI)).Select(hit => hit.EntryId));
        Assert.Empty(await SearchAsync(user.Id, Angle(0), "Zanzibar", Gemini));
    }

    // B: an entry already pending (never indexed, or edited since) keeps exactly one job, for the journal's
    // current revision, with the lease cleared and the request time renewed.
    // D: after the promotion, creating and editing entries queue them exactly as before.
    [Fact]
    public async Task APendingEntry_KeepsExactlyOneJob_ForItsCurrentRevision_AndLaterWritesQueueAsBefore()
    {
        var user = await NewUserAsync();
        JournalEntry pending = null!, edited = null!;

        await PromoteAsync(async () =>
        {
            var indexed = await CreateAsync(user.Id, "Prima versione.");
            await IndexAsync(user.Id, indexed, [("Prima versione.", Angle(0))], Gemini);
            edited = await UpdateAsync(user.Id, indexed.Id, "Seconda versione.");
            pending = await CreateAsync(user.Id, "Mai indicizzata.");

            Assert.Equal(2, (await JobsAsync(user.Id)).Count);
        });

        var jobs = await JobsAsync(user.Id);
        Assert.Equal(
            new[] { (pending.Id, pending.UpdatedAtUtc), (edited.Id, edited.UpdatedAtUtc) }.OrderBy(job => job.Item1),
            jobs.Select(job => (job.EntryId, job.SourceUpdatedAt)).OrderBy(job => job.Item1));
        Assert.All(jobs, job => Assert.True(job.LeaseToken is null && job.LeaseUntilUtc is null));
        Assert.Equal(new JournalMemoryCounts(0, 2), await CountsAsync(user.Id));

        // Journal writes after the promotion: unchanged rules (one job per entry, latest revision, no lease).
        var claimed = await ClaimAsync(user.Id);
        var rewritten = await UpdateAsync(user.Id, claimed!.EntryId, "Terza versione.");
        var created = await CreateAsync(user.Id, "Nuova voce.");

        var after = (await JobsAsync(user.Id)).ToDictionary(job => job.EntryId);
        Assert.Equal(3, after.Count);
        Assert.Equal((rewritten.UpdatedAtUtc, (Guid?)null), (after[rewritten.Id].SourceUpdatedAt, after[rewritten.Id].LeaseToken));
        Assert.Equal((created.UpdatedAtUtc, (Guid?)null), (after[created.Id].SourceUpdatedAt, after[created.Id].LeaseToken));
        Assert.Equal(JournalIndexCompletion.Stale, await CompleteAsync(claimed, [(claimed.Content, Angle(0))], OpenAI));
    }

    // C: a job claimed (leased) by a run in flight across the migration is claimable again at once, and that
    // run's late completion is discarded both before and after the promoted result exists.
    [Fact]
    public async Task ALeasedJob_IsReleasedByTheMigration_AndTheOldRunCanNeverOverwriteThePromotedIndex()
    {
        var user = await NewUserAsync();
        JournalEntry entry = null!;
        JournalIndexClaim inFlight = null!;

        await PromoteAsync(async () =>
        {
            entry = await CreateAsync(user.Id, "Voce in volo.");
            inFlight = (await ClaimAsync(user.Id))!;
            Assert.NotNull(Assert.Single(await JobsAsync(user.Id)).LeaseToken);
        });

        var job = Assert.Single(await JobsAsync(user.Id));
        Assert.Equal(entry.UpdatedAtUtc, job.SourceUpdatedAt);
        Assert.True(job.LeaseToken is null && job.LeaseUntilUtc is null);

        // The old (Gemini) run finishes after the migration: discarded, nothing written, still pending.
        Assert.Equal(JournalIndexCompletion.Stale, await CompleteAsync(inFlight, [("Voce in volo.", Angle(0))], Gemini));
        Assert.Empty(await ChunkIdentitiesAsync(entry.Id));
        Assert.Single(await JobsAsync(user.Id));

        // Claimable now (no waiting for the old lease), and the promoted result is written.
        var promoted = await ClaimAsync(user.Id);
        Assert.NotEqual(inFlight.LeaseToken, promoted!.LeaseToken);
        Assert.Equal(JournalIndexCompletion.Indexed, await CompleteAsync(promoted, [("Voce in volo.", Angle(0))], OpenAI));

        // The old run's completion arriving even later still cannot replace it.
        Assert.Equal(JournalIndexCompletion.Stale, await CompleteAsync(inFlight, [("Voce in volo.", Angle(9))], Gemini));
        Assert.Equal(["openai text-embedding-3-small"], await ChunkIdentitiesAsync(entry.Id));
        Assert.Equal(new JournalMemoryCounts(1, 0), await CountsAsync(user.Id));
    }

    // E: a hard delete after the promotion removes the entry's job and its (old-identity) chunks, as before.
    [Fact]
    public async Task HardDelete_AfterThePromotion_RemovesTheJobAndTheOldChunks()
    {
        var user = await NewUserAsync();
        JournalEntry entry = null!;

        await PromoteAsync(async () =>
        {
            entry = await CreateAsync(user.Id, "Da cancellare dopo.");
            await IndexAsync(user.Id, entry, [("Da cancellare dopo.", Angle(0))], Gemini);
        });

        Assert.Single(await JobsAsync(user.Id));
        await DeleteAsync(user.Id, entry.Id);

        Assert.Empty(await JobsAsync(user.Id));
        Assert.Empty(await ChunkIdentitiesAsync(entry.Id));
        Assert.Equal(new JournalMemoryCounts(0, 0), await CountsAsync(user.Id));
        Assert.Null(await ClaimAsync(user.Id));
    }

    // G: Down (rollback) queues every entry again for the rolled-back runtime: one unleased job per entry for
    // its current revision. No journal row changes and no chunk is deleted; the old runtime's sync replaces
    // them. Up afterwards queues them again for OpenAI.
    [Fact]
    public async Task Down_RequeuesEveryEntry_ForTheRolledBackRuntime_WithoutTouchingJournalData()
    {
        var user = await NewUserAsync();
        var first = await CreateAsync(user.Id, "Prima voce indicizzata.");
        await IndexAsync(user.Id, first, [("Prima voce indicizzata.", Angle(0))], OpenAI);
        var second = await CreateAsync(user.Id, "Seconda voce, in coda.");
        var leased = await ClaimAsync(user.Id);
        Assert.Equal(second.Id, leased!.EntryId);
        var source = await JournalSourceAsync(user.Id);

        await using var scope = fixture.CreateScope();
        var dbContext = Db(scope);
        var migrator = dbContext.GetService<IMigrator>();
        var applied = (await dbContext.Database.GetAppliedMigrationsAsync()).ToList();

        try
        {
            await migrator.MigrateAsync(applied.Single(id => id.EndsWith(PreviousMigration, StringComparison.Ordinal)));

            var jobs = await JobsAsync(user.Id);
            Assert.Equal(
                new[] { (first.Id, first.UpdatedAtUtc), (second.Id, second.UpdatedAtUtc) }.OrderBy(job => job.Item1),
                jobs.Select(job => (job.EntryId, job.SourceUpdatedAt)).OrderBy(job => job.Item1));
            Assert.All(jobs, job => Assert.True(job.LeaseToken is null && job.LeaseUntilUtc is null));
            Assert.Equal(["openai text-embedding-3-small"], await ChunkIdentitiesAsync(first.Id));
            Assert.Equal(source, await JournalSourceAsync(user.Id));

            // The rolled-back runtime (Gemini identity) rebuilds an entry through the same sync path.
            var rollback = await ClaimAsync(user.Id);
            Assert.Equal(JournalIndexCompletion.Indexed, await CompleteAsync(rollback!, [(rollback!.Content, Angle(0))], Gemini));
            Assert.Equal(["google gemini-embedding-2"], await ChunkIdentitiesAsync(rollback.EntryId));
            Assert.Equal(JournalIndexCompletion.Stale, await CompleteAsync(leased, [(leased.Content, Angle(0))], OpenAI));
        }
        finally
        {
            await migrator.MigrateAsync();
        }

        Assert.Equal(applied, await dbContext.Database.GetAppliedMigrationsAsync());
        Assert.Equal(2, (await JobsAsync(user.Id)).Count);
        Assert.Equal(source, await JournalSourceAsync(user.Id));
    }

    // The re-queue locks journal rows before the queue (FOR SHARE), like journal writes and completion. An
    // edit in flight when it runs is waited for, and its newer revision is what stays queued: the snapshot's
    // older revision can never overwrite it (that would leave a job no completion could ever satisfy).
    [Fact]
    public async Task TheRequeue_WaitsForAnEditInFlight_AndQueuesItsNewerRevision()
    {
        var user = await NewUserAsync();
        var entry = await CreateAsync(user.Id, "Versione uno.");
        var newer = entry.UpdatedAtUtc.AddMinutes(1);

        await using var edit = await OpenAsync();
        await using var editTransaction = await edit.BeginTransactionAsync();
        await ExecuteAsync(edit, $"UPDATE journal_entries SET content = 'Versione due.', updated_at_utc = '{newer:O}' WHERE id = '{entry.Id}'");
        await ExecuteAsync(edit, $"""
            INSERT INTO journal_memory_index_queue (entry_id, user_id, source_updated_at, requested_at_utc)
            VALUES ('{entry.Id}', '{user.Id}', '{newer:O}', '{newer:O}')
            ON CONFLICT (entry_id) DO UPDATE SET source_updated_at = EXCLUDED.source_updated_at, requested_at_utc = EXCLUDED.requested_at_utc,
                lease_token = NULL, lease_until_utc = NULL
            """);

        var requeue = RequeueInOwnConnectionAsync();
        await WaitUntilBlockedAsync();
        Assert.False(requeue.IsCompleted);

        await editTransaction.CommitAsync();
        await requeue;

        Assert.Equal(newer, Assert.Single(await JobsAsync(user.Id)).SourceUpdatedAt);
        var claim = await ClaimAsync(user.Id);
        Assert.Equal(("Versione due.", newer), (claim!.Content, claim.SourceUpdatedAtUtc));
        Assert.Equal(JournalIndexCompletion.Indexed, await CompleteAsync(claim, [("Versione due.", Angle(0))], OpenAI));
    }

    // A delete in flight is waited for too: the deleted entry is skipped, never re-queued (no foreign-key
    // failure aborts the migration, nothing is recreated).
    [Fact]
    public async Task TheRequeue_WaitsForADeleteInFlight_AndSkipsTheDeletedEntry()
    {
        var user = await NewUserAsync();
        var entry = await CreateAsync(user.Id, "Sta per sparire.");

        await using var delete = await OpenAsync();
        await using var deleteTransaction = await delete.BeginTransactionAsync();
        await ExecuteAsync(delete, $"DELETE FROM journal_entries WHERE id = '{entry.Id}'");

        var requeue = RequeueInOwnConnectionAsync();
        await WaitUntilBlockedAsync();
        Assert.False(requeue.IsCompleted);

        await deleteTransaction.CommitAsync();
        await requeue;

        Assert.Empty(await JobsAsync(user.Id));
        Assert.Empty(await QueryAsync($"SELECT id::text AS \"Value\" FROM journal_entries WHERE id = '{entry.Id}'"));
    }

    // ---- Helpers ----

    // Down to AddJournalRetrievalV2Candidate, arrange the pre-promotion state, then up again (always).
    private async Task PromoteAsync(Func<Task> arrangeBeforePromotion)
    {
        await using var scope = fixture.CreateScope();
        var dbContext = Db(scope);
        var migrator = dbContext.GetService<IMigrator>();
        var applied = (await dbContext.Database.GetAppliedMigrationsAsync()).ToList();
        Assert.EndsWith(PromotionMigration, applied[^(LaterMigrations.Length + 1)], StringComparison.Ordinal);

        try
        {
            await migrator.MigrateAsync(applied.Single(id => id.EndsWith(PreviousMigration, StringComparison.Ordinal)));
            await arrangeBeforePromotion();
        }
        finally
        {
            await migrator.MigrateAsync();
        }

        Assert.Equal(applied, await dbContext.Database.GetAppliedMigrationsAsync());
    }

    private async Task RequeueInOwnConnectionAsync()
    {
        await using var connection = await OpenAsync();
        await ExecuteAsync(connection, RequeueJournalMemoryForOpenAI.RequeueEveryEntry);
    }

    // State check, not a timed sleep: the re-queue statement is waiting on a row lock.
    private async Task WaitUntilBlockedAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while ((await QueryAsync(
                   "SELECT pid::text AS \"Value\" FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE '%INSERT INTO journal_memory_index_queue%FOR SHARE OF e%'"))
               .Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "The re-queue never waited for the journal row lock.");
            await Task.Delay(20);
        }
    }

    private static float[] Angle(double degrees)
    {
        var vector = new float[JournalMemoryPolicy.EmbeddingDimensions];
        vector[0] = (float)Math.Cos(degrees * Math.PI / 180);
        vector[1] = (float)Math.Sin(degrees * Math.PI / 180);
        return vector;
    }

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Day.AddDays(-30));
        await PostgresAssert.InsertAsync(fixture, user);
        return user;
    }

    private async Task<JournalEntry> CreateAsync(Guid userId, string content)
    {
        await using var scope = fixture.CreateScope();
        var result = await new CreateJournalEntryHandler(Repository(scope), scope.ServiceProvider.GetRequiredService<IJournalIndexQueue>(),
                scope.ServiceProvider.GetRequiredService<IUnitOfWork>(), TimeProvider.System)
            .HandleAsync(userId, new CreateJournalEntryCommand(Day, null, content), CancellationToken.None);
        Assert.Equal(JournalResultStatus.Ok, result.Status);
        return result.Entry!;
    }

    private async Task<JournalEntry> UpdateAsync(Guid userId, Guid id, string content)
    {
        await using var scope = fixture.CreateScope();
        var result = await new UpdateJournalEntryHandler(Repository(scope), scope.ServiceProvider.GetRequiredService<IJournalIndexQueue>(),
                scope.ServiceProvider.GetRequiredService<IUnitOfWork>(), TimeProvider.System)
            .HandleAsync(userId, id, new UpdateJournalEntryCommand(Day, null, content), CancellationToken.None);
        Assert.Equal(JournalResultStatus.Ok, result.Status);
        return result.Entry!;
    }

    private async Task DeleteAsync(Guid userId, Guid id)
    {
        await using var scope = fixture.CreateScope();
        Assert.Equal(JournalResultStatus.Ok, await new DeleteJournalEntryHandler(Repository(scope)).HandleAsync(userId, id, CancellationToken.None));
    }

    private async Task<JournalIndexClaim?> ClaimAsync(Guid userId)
    {
        var now = DateTimeOffset.UtcNow;
        await using var scope = fixture.CreateScope();
        return await Store(scope).TryClaimNextAsync(userId, Guid.NewGuid(), now, now + JournalMemoryPolicy.LeaseDuration, CancellationToken.None);
    }

    private async Task<JournalIndexCompletion> CompleteAsync(JournalIndexClaim claim, IEnumerable<(string Text, float[] Vector)> chunks, JournalMemoryIndexIdentity identity)
    {
        await using var scope = fixture.CreateScope();
        var indexed = new JournalIndexedEntry(identity,
            chunks.Select((chunk, ordinal) => new JournalIndexedChunk(ordinal, chunk.Text, chunk.Vector)).ToList());
        return await Store(scope).CompleteAsync(claim, indexed, DateTimeOffset.UtcNow, CancellationToken.None);
    }

    // Indexes the entry's pending revision (the only pending job of the user) with the given identity.
    private async Task IndexAsync(Guid userId, JournalEntry entry, IEnumerable<(string Text, float[] Vector)> chunks, JournalMemoryIndexIdentity identity)
    {
        var claim = await ClaimAsync(userId);
        Assert.Equal(entry.Id, claim!.EntryId);
        Assert.Equal(JournalIndexCompletion.Indexed, await CompleteAsync(claim, chunks, identity));
    }

    private async Task<IReadOnlyList<JournalMemoryHit>> SearchAsync(Guid userId, float[] query, string text, JournalMemoryIndexIdentity identity)
    {
        await using var scope = fixture.CreateScope();
        return await Store(scope).SearchAsync(userId, query, text, identity, 40, CancellationToken.None);
    }

    private async Task<JournalMemoryCounts> CountsAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();
        return await Store(scope).GetCountsAsync(userId, OpenAI, CancellationToken.None);
    }

    private async Task<List<JobRow>> JobsAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();
        return await Db(scope).Database.SqlQueryRaw<JobRow>($"""
            SELECT entry_id AS "EntryId", source_updated_at AS "SourceUpdatedAt", requested_at_utc AS "RequestedAtUtc",
                   lease_token AS "LeaseToken", lease_until_utc AS "LeaseUntilUtc"
            FROM journal_memory_index_queue WHERE user_id = '{userId}'
            """).ToListAsync();
    }

    private Task<List<string>> ChunkIdentitiesAsync(Guid entryId) =>
        QueryAsync($"SELECT embedding_provider || ' ' || embedding_model AS \"Value\" FROM journal_memory_chunks WHERE entry_id = '{entryId}' ORDER BY ordinal");

    // Every journal column of the user's entries, in a stable order: the source must never change.
    private async Task<string> JournalSourceAsync(Guid userId) =>
        (await QueryAsync($"SELECT coalesce(string_agg(row_to_json(e)::text, '|' ORDER BY e.id), '') AS \"Value\" FROM journal_entries e WHERE e.user_id = '{userId}'")).Single();

    private async Task<DateTimeOffset> DatabaseNowAsync()
    {
        await using var scope = fixture.CreateScope();
        return await Db(scope).Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync();
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        await using var scope = fixture.CreateScope();
        var connection = new NpgsqlConnection(Db(scope).Database.GetConnectionString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> QueryAsync(string sql)
    {
        await using var scope = fixture.CreateScope();
        return await Db(scope).Database.SqlQueryRaw<string>(sql).ToListAsync();
    }

    private static IJournalEntryRepository Repository(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IJournalEntryRepository>();

    private static IJournalMemoryStore Store(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IJournalMemoryStore>();

    private static LifeOSDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();

    private sealed record JobRow(Guid EntryId, DateTimeOffset SourceUpdatedAt, DateTimeOffset RequestedAtUtc, Guid? LeaseToken, DateTimeOffset? LeaseUntilUtc);
}
