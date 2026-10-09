using System.Globalization;
using LifeOS.Application.Journal;
using LifeOS.Application.Memory;
using LifeOS.Application.Persistence;
using LifeOS.Domain.Journal;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LifeOS.IntegrationTests.PostgreSql;

// AI-004 against real PostgreSQL with pgvector: the migration (extension, tables, constraints,
// indexes, retrieval function, initial enqueue, Down), the journal write → queue rule in one
// transaction, leases and concurrent claims, stale/deleted results, atomic chunk replacement, hard
// delete cascades, ownership, and the production hybrid retrieval with synthetic 1536-dimensional
// vectors (no embedding provider).
//
// The database is shared by the whole PostgreSQL collection, so every assertion is scoped to this
// test's own users.
[Collection(PostgreSqlCollection.Name)]
public class JournalMemoryPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Day = new(2026, 7, 4, 18, 30, 0, TimeSpan.Zero);
    private static readonly JournalMemoryIndexIdentity Identity = JournalMemoryPolicy.Identity;

    // ---- Migration ----

    [Fact]
    public async Task Migration_CreatesTheExtension_Tables_Constraints_Indexes_AndTheRetrievalFunction()
    {
        await using var scope = fixture.CreateScope();
        var database = Db(scope).Database;

        Assert.Contains(await database.GetAppliedMigrationsAsync(), id => id.EndsWith("_AddJournalMemory", StringComparison.Ordinal));
        Assert.False(database.HasPendingModelChanges());
        Assert.Equal(["vector"], await Strings(database, "SELECT extname::text AS \"Value\" FROM pg_extension WHERE extname = 'vector'"));

        Assert.Equal(
            [
                "entry_id uuid NOT NULL", "user_id uuid NOT NULL", "source_updated_at timestamp with time zone NOT NULL",
                "requested_at_utc timestamp with time zone NOT NULL", "lease_token uuid NULL", "lease_until_utc timestamp with time zone NULL"
            ],
            await Columns(database, "journal_memory_index_queue"));
        Assert.Equal(
            [
                "id uuid NOT NULL", "entry_id uuid NOT NULL", "user_id uuid NOT NULL", "ordinal integer NOT NULL", "chunk_text text NOT NULL",
                "source_updated_at timestamp with time zone NOT NULL", "chunking_version character varying(64) NOT NULL",
                "embedding_provider character varying(64) NOT NULL", "embedding_model character varying(100) NOT NULL",
                "embedding_dimensions integer NOT NULL", "embedding vector(1536) NOT NULL", "created_at_utc timestamp with time zone NOT NULL"
            ],
            await Columns(database, "journal_memory_chunks"));

        // Both foreign keys of both tables cascade (confdeltype 'c').
        Assert.Equal(
            ["FK_journal_memory_index_queue_journal_entries_entry_id c", "FK_journal_memory_index_queue_users_user_id c", "ck_journal_memory_index_queue_lease"],
            await Constraints(database, "journal_memory_index_queue"));
        Assert.Equal(
            [
                "FK_journal_memory_chunks_journal_entries_entry_id c", "FK_journal_memory_chunks_users_user_id c", "ck_journal_memory_chunks_dimensions",
                "ck_journal_memory_chunks_identity", "ck_journal_memory_chunks_ordinal", "ck_journal_memory_chunks_text"
            ],
            await Constraints(database, "journal_memory_chunks"));

        Assert.Equal(
            [
                "CREATE INDEX ix_journal_memory_chunks_embedding_hnsw ON public.journal_memory_chunks USING hnsw (embedding vector_cosine_ops)",
                "CREATE INDEX ix_journal_memory_chunks_lexical ON public.journal_memory_chunks USING gin (to_tsvector('simple'::regconfig, chunk_text))",
                "CREATE INDEX ix_journal_memory_chunks_user_identity ON public.journal_memory_chunks USING btree (user_id, embedding_model, chunking_version)",
                "CREATE UNIQUE INDEX \"PK_journal_memory_chunks\" ON public.journal_memory_chunks USING btree (id)",
                "CREATE UNIQUE INDEX ux_journal_memory_chunks_entry_ordinal ON public.journal_memory_chunks USING btree (entry_id, ordinal)"
            ],
            (await Strings(database, "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'journal_memory_chunks'")).Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                "CREATE INDEX ix_journal_memory_index_queue_user_requested ON public.journal_memory_index_queue USING btree (user_id, requested_at_utc, entry_id)",
                "CREATE UNIQUE INDEX \"PK_journal_memory_index_queue\" ON public.journal_memory_index_queue USING btree (entry_id)"
            ],
            (await Strings(database, "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'journal_memory_index_queue'")).Order(StringComparer.Ordinal));

        // AI-005.1 adds the separate search_journal_memory_v2 candidate (JournalRetrievalV2CandidateTests).
        Assert.Equal(["search_journal_memory_v1(p_user_id uuid, p_query_embedding vector, p_query_text text, p_embedding_provider text, p_embedding_model text, p_chunking_version text, p_limit integer)"],
            await Strings(database, "SELECT (proname || '(' || pg_get_function_arguments(oid) || ')') AS \"Value\" FROM pg_proc WHERE proname = 'search_journal_memory_v1'"));
        // Filtered HNSW scans keep going until they have the user's candidates (pgvector 0.8+).
        Assert.Equal(["hnsw.iterative_scan=strict_order"],
            await Strings(database, "SELECT unnest(proconfig) AS \"Value\" FROM pg_proc WHERE proname = 'search_journal_memory_v1'"));
    }

    [Fact]
    public async Task InvalidRows_AreRejectedByTheDatabase()
    {
        var user = await NewUserAsync();
        var entry = await CreateAsync(user.Id, "Testo.");
        await IndexAsync(user.Id, entry, [("Testo.", Angle(0))]);

        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_journal_memory_chunks_text", () =>
            Execute($"UPDATE journal_memory_chunks SET chunk_text = '  ' WHERE entry_id = '{entry.Id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_journal_memory_chunks_ordinal", () =>
            Execute($"UPDATE journal_memory_chunks SET ordinal = -1 WHERE entry_id = '{entry.Id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_journal_memory_chunks_dimensions", () =>
            Execute($"UPDATE journal_memory_chunks SET embedding_dimensions = 3 WHERE entry_id = '{entry.Id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_journal_memory_chunks_identity", () =>
            Execute($"UPDATE journal_memory_chunks SET embedding_model = ' ' WHERE entry_id = '{entry.Id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.ForeignKeyViolation, "FK_journal_memory_chunks_journal_entries_entry_id", () =>
            Execute($"UPDATE journal_memory_chunks SET entry_id = '{Guid.CreateVersion7()}' WHERE entry_id = '{entry.Id}'"));

        // pgvector itself enforces the column's 1536 dimensions.
        var exception = await Assert.ThrowsAnyAsync<PostgresException>(() =>
            Execute($"UPDATE journal_memory_chunks SET embedding = '[1,2,3]' WHERE entry_id = '{entry.Id}'"));
        Assert.Contains("1536", exception.MessageText);

        await CreateAsync(user.Id, "In coda.");
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_journal_memory_index_queue_lease", () =>
            Execute($"UPDATE journal_memory_index_queue SET lease_token = '{Guid.NewGuid()}' WHERE user_id = '{user.Id}'"));
    }

    // Down to just before AddJournalMemory (in a finally, back up again, so a failure can never leave
    // the shared database downgraded): only the AI-004 objects go, the extension stays; Up re-creates
    // them and enqueues every existing entry without any provider call.
    [Fact]
    public async Task Migration_DownRemovesOnlyAi004Objects_AndUpEnqueuesEveryExistingEntry()
    {
        await using var scope = fixture.CreateScope();
        var dbContext = Db(scope);
        var migrator = dbContext.GetService<IMigrator>();
        var applied = (await dbContext.Database.GetAppliedMigrationsAsync()).ToList();
        var tablesBefore = await TableCountAsync(dbContext.Database);
        var user = await NewUserAsync();
        JournalEntry first, second;

        try
        {
            await migrator.MigrateAsync(applied[applied.FindIndex(id => id.EndsWith("_AddJournalMemory", StringComparison.Ordinal)) - 1]);

            Assert.Equal(tablesBefore - 2, await TableCountAsync(dbContext.Database));
            Assert.Empty(await Strings(dbContext.Database, "SELECT proname::text AS \"Value\" FROM pg_proc WHERE proname LIKE 'search_journal_memory%'"));
            Assert.Equal(["vector"], await Strings(dbContext.Database, "SELECT extname::text AS \"Value\" FROM pg_extension WHERE extname = 'vector'"));

            // Journal entries written while AI-004 does not exist (plain JRN-001 repository writes).
            first = JournalEntry.Create(user.Id, Day, "Prima", "Prima voce.", Day);
            second = JournalEntry.Create(user.Id, Day, null, "Seconda voce.", Day.AddMinutes(5));
            await using (var writes = fixture.CreateScope())
            {
                await Repository(writes).AddAsync(first, CancellationToken.None);
                await Repository(writes).AddAsync(second, CancellationToken.None);
            }
        }
        finally
        {
            await migrator.MigrateAsync();
        }

        Assert.Equal(applied, await dbContext.Database.GetAppliedMigrationsAsync());
        Assert.Equal(tablesBefore, await TableCountAsync(dbContext.Database));

        var queued = await Strings(dbContext.Database,
            $"SELECT entry_id::text || ' ' || (source_updated_at = (SELECT updated_at_utc FROM journal_entries WHERE id = entry_id))::text || ' ' || (lease_token IS NULL)::text AS \"Value\" FROM journal_memory_index_queue WHERE user_id = '{user.Id}'");
        Assert.Equal(new[] { $"{first.Id} true true", $"{second.Id} true true" }.Order(StringComparer.Ordinal), queued.Order(StringComparer.Ordinal));
        Assert.Equal(new JournalMemoryCounts(0, 2), await CountsAsync(user.Id));
    }

    // ---- Journal writes → queue ----

    [Fact]
    public async Task CreateAndUpdate_EnqueueTheLatestRevision_AndAnUpdateClearsALease()
    {
        var user = await NewUserAsync();
        var entry = await CreateAsync(user.Id, "Prima versione.");
        Assert.Equal(new JournalMemoryCounts(0, 1), await CountsAsync(user.Id));
        Assert.Equal(entry.UpdatedAtUtc, await QueuedRevisionAsync(entry.Id));

        Assert.NotNull(await ClaimAsync(user.Id));
        var updated = await UpdateAsync(user.Id, entry.Id, "Seconda versione.");

        Assert.Equal(updated.UpdatedAtUtc, await QueuedRevisionAsync(entry.Id));
        Assert.Equal(["true"], await QueryAsync($"SELECT (lease_token IS NULL AND lease_until_utc IS NULL)::text AS \"Value\" FROM journal_memory_index_queue WHERE entry_id = '{entry.Id}'"));
        Assert.Equal(new JournalMemoryCounts(0, 1), await CountsAsync(user.Id));
    }

    [Fact]
    public async Task AFailedEnqueue_RollsBackTheJournalWrite()
    {
        var user = await NewUserAsync();
        await using var scope = fixture.CreateScope();
        var handler = new CreateJournalEntryHandler(Repository(scope), new FailingQueue(), scope.ServiceProvider.GetRequiredService<IUnitOfWork>(), TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(user.Id, new CreateJournalEntryCommand(Day, null, "Mai salvata."), CancellationToken.None));

        Assert.Empty(await QueryAsync($"SELECT id::text AS \"Value\" FROM journal_entries WHERE user_id = '{user.Id}'"));
    }

    // ---- Claims and leases ----

    [Fact]
    public async Task ConcurrentClaims_NeverHandTheSameJobToTwoRuns()
    {
        var user = await NewUserAsync();
        for (var index = 0; index < 5; index++)
        {
            await CreateAsync(user.Id, $"Voce {index}.");
        }

        // Twelve runs race, each in its own scope (connection).
        var claims = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => ClaimAsync(user.Id))));

        var won = claims.Where(claim => claim is not null).Select(claim => claim!).ToList();
        Assert.Equal(5, won.Count);
        Assert.Equal(5, won.Select(claim => claim.EntryId).Distinct().Count());
        Assert.Equal(5, won.Select(claim => claim.LeaseToken).Distinct().Count());
        Assert.Null(await ClaimAsync(user.Id));
    }

    [Fact]
    public async Task AnExpiredLease_MakesTheJobClaimableAgain_AndTheOldRunCanNoLongerComplete()
    {
        var user = await NewUserAsync();
        var entry = await CreateAsync(user.Id, "Voce.");
        var now = DateTimeOffset.UtcNow;

        var crashed = await ClaimAsync(user.Id, now, TimeSpan.FromMinutes(1));
        Assert.NotNull(crashed);
        Assert.Null(await ClaimAsync(user.Id, now.AddSeconds(30), TimeSpan.FromMinutes(1)));

        var retry = await ClaimAsync(user.Id, now.AddMinutes(2), TimeSpan.FromMinutes(1));
        Assert.Equal(entry.Id, retry!.EntryId);
        Assert.NotEqual(crashed!.LeaseToken, retry.LeaseToken);

        Assert.Equal(JournalIndexCompletion.Stale, await CompleteAsync(crashed, [("Voce.", Angle(0))]));
        Assert.Equal(JournalIndexCompletion.Indexed, await CompleteAsync(retry, [("Voce.", Angle(0))]));
        Assert.Equal(new JournalMemoryCounts(1, 0), await CountsAsync(user.Id));
    }

    [Fact]
    public async Task Release_MakesTheJobClaimableNow_OnlyForTheLeaseHolder()
    {
        var user = await NewUserAsync();
        await CreateAsync(user.Id, "Voce.");
        var claim = (await ClaimAsync(user.Id))!;

        await using (var scope = fixture.CreateScope())
        {
            await Store(scope).ReleaseAsync(claim with { LeaseToken = Guid.NewGuid() }, CancellationToken.None);
        }

        Assert.Null(await ClaimAsync(user.Id));

        await using (var scope = fixture.CreateScope())
        {
            await Store(scope).ReleaseAsync(claim, CancellationToken.None);
        }

        Assert.NotNull(await ClaimAsync(user.Id));
    }

    [Fact]
    public async Task AnUpdateWhileEmbedding_InvalidatesTheLease_AndTheOldResultIsDiscarded()
    {
        var user = await NewUserAsync();
        var entry = await CreateAsync(user.Id, "Vecchio testo.");
        var claim = (await ClaimAsync(user.Id))!;

        var updated = await UpdateAsync(user.Id, entry.Id, "Nuovo testo.");

        Assert.Equal(JournalIndexCompletion.Stale, await CompleteAsync(claim, [("Vecchio testo.", Angle(0))]));
        Assert.Equal(new JournalMemoryCounts(0, 1), await CountsAsync(user.Id));
        Assert.Equal(updated.UpdatedAtUtc, await QueuedRevisionAsync(entry.Id));

        // The new revision is indexed by the next run.
        var next = (await ClaimAsync(user.Id))!;
        Assert.Equal(("Nuovo testo.", updated.UpdatedAtUtc), (next.Content, next.SourceUpdatedAtUtc));
        Assert.Equal(JournalIndexCompletion.Indexed, await CompleteAsync(next, [("Nuovo testo.", Angle(0))]));
    }

    [Fact]
    public async Task AStaleResult_NeverOverwritesNewerChunks()
    {
        var user = await NewUserAsync();
        var entry = await CreateAsync(user.Id, "Versione uno.");
        var slow = (await ClaimAsync(user.Id))!;
        await UpdateAsync(user.Id, entry.Id, "Versione due.");
        var fast = (await ClaimAsync(user.Id))!;

        Assert.Equal(JournalIndexCompletion.Indexed, await CompleteAsync(fast, [("Versione due.", Angle(0))]));
        Assert.Equal(JournalIndexCompletion.Stale, await CompleteAsync(slow, [("Versione uno.", Angle(10))]));

        Assert.Equal(["Versione due."], await ChunkTextsAsync(entry.Id));
    }

    [Fact]
    public async Task ADeleteWhileEmbedding_DiscardsTheResult_AndRecreatesNothing()
    {
        var user = await NewUserAsync();
        var entry = await CreateAsync(user.Id, "Da cancellare.");
        var claim = (await ClaimAsync(user.Id))!;

        await DeleteAsync(user.Id, entry.Id);

        Assert.Equal(JournalIndexCompletion.Gone, await CompleteAsync(claim, [("Da cancellare.", Angle(0))]));
        Assert.Equal(new JournalMemoryCounts(0, 0), await CountsAsync(user.Id));
        Assert.Empty(await ChunkTextsAsync(entry.Id));
    }

    [Fact]
    public async Task Completion_ReplacesEveryChunkAtomically_AndAFailureKeepsTheOldIndexAndTheJob()
    {
        var user = await NewUserAsync();
        var entry = await CreateAsync(user.Id, "Uno. Due. Tre.");
        await IndexAsync(user.Id, entry, [("Uno.", Angle(0)), ("Due.", Angle(5)), ("Tre.", Angle(10))]);
        var updated = await UpdateAsync(user.Id, entry.Id, "Quattro. Cinque.");
        var claim = (await ClaimAsync(user.Id))!;

        // A chunk set the database rejects (duplicate ordinal) fails the whole completion.
        await using (var scope = fixture.CreateScope())
        {
            var invalid = new JournalIndexedEntry(Identity,
                [new JournalIndexedChunk(0, "Quattro.", Angle(0)), new JournalIndexedChunk(0, "Cinque.", Angle(5))]);
            await PostgresAssert.ViolatesAsync(PostgresAssert.UniqueViolation, "ux_journal_memory_chunks_entry_ordinal", () =>
                Store(scope).CompleteAsync(claim, invalid, DateTimeOffset.UtcNow, CancellationToken.None));
        }

        Assert.Equal(["Uno.", "Due.", "Tre."], await ChunkTextsAsync(entry.Id));
        Assert.Equal(updated.UpdatedAtUtc, await QueuedRevisionAsync(entry.Id));

        // A valid completion replaces all three old chunks with the two new ones.
        Assert.Equal(JournalIndexCompletion.Indexed, await CompleteAsync(claim, [("Quattro.", Angle(0)), ("Cinque.", Angle(5))]));
        Assert.Equal(["Quattro.", "Cinque."], await ChunkTextsAsync(entry.Id));
        Assert.Equal(new JournalMemoryCounts(1, 0), await CountsAsync(user.Id));
    }

    // ---- Hard delete and ownership ----

    [Fact]
    public async Task HardDelete_CascadesToChunksAndQueue_AndTheEntryCanNeverBeRetrievedAgain()
    {
        var user = await NewUserAsync();
        var entry = await CreateAsync(user.Id, "Ricordo zanzibarico.");
        await IndexAsync(user.Id, entry, [("Ricordo zanzibarico.", Angle(0))]);
        await UpdateAsync(user.Id, entry.Id, "Ricordo zanzibarico, rivisto.");
        Assert.Single(await SearchAsync(user.Id, Angle(0), "zanzibarico", 8));

        await DeleteAsync(user.Id, entry.Id);

        Assert.Empty(await ChunkTextsAsync(entry.Id));
        Assert.Empty(await QueryAsync($"SELECT entry_id::text AS \"Value\" FROM journal_memory_index_queue WHERE entry_id = '{entry.Id}'"));
        Assert.Empty(await SearchAsync(user.Id, Angle(0), "zanzibarico", 8));
    }

    [Fact]
    public async Task UserDeletion_CascadesToTheMemoryIndex()
    {
        var user = await NewUserAsync();
        var entry = await CreateAsync(user.Id, "Voce.");
        await IndexAsync(user.Id, entry, [("Voce.", Angle(0))]);
        await CreateAsync(user.Id, "In coda.");

        await Execute($"DELETE FROM users WHERE id = '{user.Id}'");

        Assert.Equal(["0 0"], await QueryAsync($"SELECT (SELECT count(*) FROM journal_memory_chunks WHERE user_id = '{user.Id}')::text || ' ' || (SELECT count(*) FROM journal_memory_index_queue WHERE user_id = '{user.Id}')::text AS \"Value\""));
    }

    [Fact]
    public async Task EveryStoreOperation_IsScopedToItsUser()
    {
        var owner = await NewUserAsync();
        var other = await NewUserAsync();
        var entry = await CreateAsync(owner.Id, "Segreto del proprietario.");
        await IndexAsync(owner.Id, entry, [("Segreto del proprietario.", Angle(0))]);
        await CreateAsync(owner.Id, "In coda del proprietario.");

        Assert.Equal(new JournalMemoryCounts(0, 0), await CountsAsync(other.Id));
        Assert.Null(await ClaimAsync(other.Id));
        Assert.Empty(await SearchAsync(other.Id, Angle(0), "segreto proprietario", 40));

        // A claim forged for another user cannot complete or release the owner's job.
        var claim = (await ClaimAsync(owner.Id))!;
        var forged = claim with { UserId = other.Id };
        Assert.Equal(JournalIndexCompletion.Gone, await CompleteAsync(forged, [("x", Angle(0))]));
        await using (var scope = fixture.CreateScope())
        {
            await Store(scope).ReleaseAsync(forged, CancellationToken.None);
        }

        Assert.Null(await ClaimAsync(owner.Id));
        Assert.Equal(new JournalMemoryCounts(1, 1), await CountsAsync(owner.Id));

        // The function itself, called directly with another user's id, returns nothing of the owner.
        Assert.Empty(await QueryAsync($"SELECT entry_id::text AS \"Value\" FROM search_journal_memory_v1('{other.Id}', '{Literal(Angle(0))}'::vector, 'segreto', 'google', 'gemini-embedding-2', 'journal-chunking-v1', 40)"));
    }

    // ---- Hybrid retrieval (journal-retrieval-v1) ----

    [Fact]
    public async Task SemanticRanking_FollowsCosineDistance()
    {
        var user = await NewUserAsync();
        var far = await IndexedAsync(user.Id, "Lontano.", Angle(60));
        var near = await IndexedAsync(user.Id, "Vicino.", Angle(10));
        var middle = await IndexedAsync(user.Id, "Medio.", Angle(30));

        var hits = await SearchAsync(user.Id, Angle(0), "parolainesistente", 8);

        Assert.Equal([near.Id, middle.Id, far.Id], hits.Select(hit => hit.EntryId));
        Assert.Equal([1, 2, 3], hits.Select(hit => hit.Rank));
        Assert.Equal([1, 2, 3], hits.Select(hit => hit.VectorRank!.Value));
        Assert.All(hits, hit => Assert.Null(hit.LexicalRank));
        Assert.Equal(1.0 / 61, hits[0].Score, 12);
    }

    [Fact]
    public async Task ALexicalOnlyMatch_IsFound_EvenOutsideTheVectorCandidates()
    {
        var user = await NewUserAsync();
        for (var index = 0; index < 21; index++)
        {
            await IndexedAsync(user.Id, $"Giornata qualunque numero {index}.", Angle(index));
        }

        // Semantically the farthest chunk of all (outside the 20 vector candidates), lexically the only match.
        var rare = await IndexedAsync(user.Id, "Ho mangiato un tamarindo a Zanzibar.", Angle(90));

        var hits = await SearchAsync(user.Id, Angle(0), "tamarindo", 40);

        Assert.Equal(21, hits.Count);
        var hit = Assert.Single(hits, hit => hit.EntryId == rare.Id);
        Assert.Equal((null, 1), (hit.VectorRank, hit.LexicalRank));
        Assert.Equal(20, hits.Count(candidate => candidate.VectorRank is not null));
    }

    [Fact]
    public async Task ASemanticOnlyMatch_IsFound_WithoutSharedWords()
    {
        var user = await NewUserAsync();
        var semantic = await IndexedAsync(user.Id, "Una passeggiata sulla spiaggia al tramonto.", Angle(1));
        await IndexedAsync(user.Id, "Riunione di lavoro sul bilancio.", Angle(80));

        var hits = await SearchAsync(user.Id, Angle(0), "mare onde sabbia", 8);

        Assert.Equal(semantic.Id, hits[0].EntryId);
        Assert.Equal((1, (int?)null), (hits[0].VectorRank!.Value, hits[0].LexicalRank));
    }

    [Fact]
    public async Task AChunkInBothLists_GetsTheFusionBenefit()
    {
        var user = await NewUserAsync();
        var vectorOnly = await IndexedAsync(user.Id, "Pensieri sparsi della sera.", Angle(1));
        var both = await IndexedAsync(user.Id, "Il concerto di jazz al parco.", Angle(5));

        var hits = await SearchAsync(user.Id, Angle(0), "jazz", 8);

        Assert.Equal([both.Id, vectorOnly.Id], hits.Select(hit => hit.EntryId));
        Assert.Equal((2, 1), (hits[0].VectorRank!.Value, hits[0].LexicalRank!.Value));
        Assert.Equal(1.0 / 62 + 1.0 / 61, hits[0].Score, 12);
        Assert.Equal(1.0 / 61, hits[1].Score, 12);
    }

    [Fact]
    public async Task LexicalSearch_IsLanguageNeutral_AndMatchesAnyQuestionWord()
    {
        var user = await NewUserAsync();
        var italian = await IndexedAsync(user.Id, "Perché la città era così rumorosa?", Angle(70));
        var english = await IndexedAsync(user.Id, "The city was quiet today.", Angle(75));

        // 'simple' configuration: exact (lower-cased) words, no English stemming or stop words; OR semantics.
        var hits = await SearchAsync(user.Id, Angle(0), "Quando era rumorosa la città?", 8);

        Assert.Equal(italian.Id, hits.Single(hit => hit.LexicalRank == 1).EntryId);
        Assert.DoesNotContain(hits, hit => hit.EntryId == english.Id && hit.LexicalRank is not null);
    }

    [Fact]
    public async Task TheRequestedLimit_IsRespected_AndInvalidArgumentsAreRejected()
    {
        var user = await NewUserAsync();
        for (var index = 0; index < 5; index++)
        {
            await IndexedAsync(user.Id, $"Voce {index}.", Angle(index * 10));
        }

        Assert.Equal(2, (await SearchAsync(user.Id, Angle(0), "voce", 2)).Count);

        foreach (var limit in new[] { 0, 41 })
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => SearchAsync(user.Id, Angle(0), "voce", limit));
            Assert.Equal("22023", exception.SqlState);
        }

        var dimensions = await Assert.ThrowsAsync<PostgresException>(() =>
            QueryAsync($"SELECT entry_id::text AS \"Value\" FROM search_journal_memory_v1('{user.Id}', '[1,0,0]'::vector, 'voce', 'google', 'gemini-embedding-2', 'journal-chunking-v1', 5)"));
        Assert.Equal("22023", dimensions.SqlState);
    }

    [Fact]
    public async Task OnlyChunksOfTheRequestedIdentity_AreReturned()
    {
        var user = await NewUserAsync();
        var current = await IndexedAsync(user.Id, "Indice attuale.", Angle(0));
        var older = await CreateAsync(user.Id, "Indice di un altro modello.");
        await IndexAsync(user.Id, older, [("Indice di un altro modello.", Angle(0))], Identity with { EmbeddingModel = "text-embedding-3-large" });
        var oldChunking = await CreateAsync(user.Id, "Indice di un altro chunking.");
        await IndexAsync(user.Id, oldChunking, [("Indice di un altro chunking.", Angle(0))], Identity with { ChunkingVersion = "journal-chunking-v0" });

        Assert.Equal([current.Id], (await SearchAsync(user.Id, Angle(0), "indice", 40)).Select(hit => hit.EntryId));
        Assert.Equal(new JournalMemoryCounts(1, 0), await CountsAsync(user.Id));

        await using var scope = fixture.CreateScope();
        var other = await Store(scope).SearchAsync(user.Id, Angle(0), "indice", Identity with { EmbeddingModel = "text-embedding-3-large" }, 40, CancellationToken.None);
        Assert.Equal([older.Id], other.Select(hit => hit.EntryId));
    }

    [Fact]
    public async Task SearchHits_CarryTheEntrysTimeAndTitle_AndTheExactChunkText()
    {
        var user = await NewUserAsync();
        var entry = await CreateAsync(user.Id, "Prima parte. Seconda parte.", "Titolo");
        await IndexAsync(user.Id, entry, [("Prima parte.", Angle(20)), ("Seconda parte.", Angle(0))]);

        var hits = await SearchAsync(user.Id, Angle(0), "nessuna", 8);

        Assert.Equal(
            [(entry.Id, Day, "Titolo", 1, "Seconda parte."), (entry.Id, Day, "Titolo", 0, "Prima parte.")],
            hits.Select(hit => (hit.EntryId, hit.OccurredAtUtc, hit.Title!, hit.ChunkOrdinal, hit.ChunkText)));
    }

    [Fact]
    public async Task TheHnswAndLexicalIndexes_AreUsable()
    {
        var user = await NewUserAsync();
        await IndexedAsync(user.Id, "Indicizzata.", Angle(0));

        await using var scope = fixture.CreateScope();
        await using var connection = new NpgsqlConnection(Db(scope).Database.GetConnectionString());
        await connection.OpenAsync();
        await using (var off = new NpgsqlCommand("SET enable_seqscan = off", connection))
        {
            await off.ExecuteNonQueryAsync();
        }

        var vectorPlan = await PlanAsync(connection,
            $"EXPLAIN SELECT id FROM journal_memory_chunks ORDER BY embedding <=> '{Literal(Angle(0))}'::vector LIMIT 5");
        var lexicalPlan = await PlanAsync(connection,
            "EXPLAIN SELECT id FROM journal_memory_chunks WHERE to_tsvector('simple'::regconfig, chunk_text) @@ 'indicizzata'::tsquery");

        Assert.Contains("ix_journal_memory_chunks_embedding_hnsw", vectorPlan);
        Assert.Contains("ix_journal_memory_chunks_lexical", lexicalPlan);
    }

    // ---- Helpers ----

    // A unit vector in the plane of the first two axes, `degrees` away from axis 0: cosine distances
    // between such vectors grow with the angle between them.
    private static float[] Angle(double degrees)
    {
        var vector = new float[JournalMemoryPolicy.EmbeddingDimensions];
        vector[0] = (float)Math.Cos(degrees * Math.PI / 180);
        vector[1] = (float)Math.Sin(degrees * Math.PI / 180);
        return vector;
    }

    private static string Literal(float[] vector) =>
        "[" + string.Join(",", vector.Select(value => value.ToString("R", CultureInfo.InvariantCulture))) + "]";

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Day.AddDays(-30));
        await PostgresAssert.InsertAsync(fixture, user);
        return user;
    }

    private async Task<JournalEntry> CreateAsync(Guid userId, string content, string? title = null)
    {
        await using var scope = fixture.CreateScope();
        var result = await new CreateJournalEntryHandler(Repository(scope), scope.ServiceProvider.GetRequiredService<IJournalIndexQueue>(),
                scope.ServiceProvider.GetRequiredService<IUnitOfWork>(), TimeProvider.System)
            .HandleAsync(userId, new CreateJournalEntryCommand(Day, title, content), CancellationToken.None);
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

    private async Task<JournalIndexClaim?> ClaimAsync(Guid userId, DateTimeOffset? now = null, TimeSpan? lease = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        await using var scope = fixture.CreateScope();
        return await Store(scope).TryClaimNextAsync(userId, Guid.NewGuid(), at, at + (lease ?? JournalMemoryPolicy.LeaseDuration), CancellationToken.None);
    }

    private async Task<JournalIndexCompletion> CompleteAsync(JournalIndexClaim claim, IEnumerable<(string Text, float[] Vector)> chunks, JournalMemoryIndexIdentity? identity = null)
    {
        await using var scope = fixture.CreateScope();
        var indexed = new JournalIndexedEntry(identity ?? Identity,
            chunks.Select((chunk, ordinal) => new JournalIndexedChunk(ordinal, chunk.Text, chunk.Vector)).ToList());
        return await Store(scope).CompleteAsync(claim, indexed, DateTimeOffset.UtcNow, CancellationToken.None);
    }

    // Indexes the entry's pending revision with the given chunks (the only pending job of the user).
    private async Task IndexAsync(Guid userId, JournalEntry entry, IEnumerable<(string Text, float[] Vector)> chunks, JournalMemoryIndexIdentity? identity = null)
    {
        var claim = await ClaimAsync(userId);
        Assert.Equal(entry.Id, claim!.EntryId);
        Assert.Equal(JournalIndexCompletion.Indexed, await CompleteAsync(claim, chunks, identity));
    }

    private async Task<JournalEntry> IndexedAsync(Guid userId, string content, float[] vector)
    {
        var entry = await CreateAsync(userId, content);
        await IndexAsync(userId, entry, [(content, vector)]);
        return entry;
    }

    private async Task<IReadOnlyList<JournalMemoryHit>> SearchAsync(Guid userId, float[] query, string text, int limit)
    {
        await using var scope = fixture.CreateScope();
        return await Store(scope).SearchAsync(userId, query, text, Identity, limit, CancellationToken.None);
    }

    private async Task<JournalMemoryCounts> CountsAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();
        return await Store(scope).GetCountsAsync(userId, Identity, CancellationToken.None);
    }

    private async Task<DateTimeOffset> QueuedRevisionAsync(Guid entryId)
    {
        await using var scope = fixture.CreateScope();
        return await Db(scope).Database
            .SqlQueryRaw<DateTimeOffset>($"SELECT source_updated_at AS \"Value\" FROM journal_memory_index_queue WHERE entry_id = '{entryId}'")
            .SingleAsync();
    }

    private Task<List<string>> ChunkTextsAsync(Guid entryId) =>
        QueryAsync($"SELECT chunk_text AS \"Value\" FROM journal_memory_chunks WHERE entry_id = '{entryId}' ORDER BY ordinal");

    private static async Task<string> PlanAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var lines = new List<string>();

        while (await reader.ReadAsync())
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join("\n", lines);
    }

    private async Task Execute(string sql)
    {
        await using var scope = fixture.CreateScope();
        await Db(scope).Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<List<string>> QueryAsync(string sql)
    {
        await using var scope = fixture.CreateScope();
        return await Strings(Db(scope).Database, sql);
    }

    private static async Task<int> TableCountAsync(DatabaseFacade database) =>
        await database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'").SingleAsync();

    private static IJournalEntryRepository Repository(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IJournalEntryRepository>();

    private static IJournalMemoryStore Store(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IJournalMemoryStore>();

    private static LifeOSDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();

    private static Task<List<string>> Columns(DatabaseFacade database, string table) =>
        Strings(database,
            $"""
            SELECT attname || ' ' || format_type(atttypid, atttypmod) || CASE WHEN attnotnull THEN ' NOT NULL' ELSE ' NULL' END AS "Value"
            FROM pg_attribute WHERE attrelid = '{table}'::regclass AND attnum > 0 AND NOT attisdropped ORDER BY attnum
            """);

    private static async Task<List<string>> Constraints(DatabaseFacade database, string table) =>
        (await Strings(database,
            $"""
            SELECT conname::text || CASE WHEN contype = 'f' THEN ' ' || confdeltype::text ELSE '' END AS "Value" FROM pg_constraint
            WHERE conrelid = '{table}'::regclass AND contype IN ('c', 'f')
            """)).Order(StringComparer.Ordinal).ToList();

    private static Task<List<string>> Strings(DatabaseFacade database, string sql) =>
        database.SqlQueryRaw<string>(sql).ToListAsync();

    private sealed class FailingQueue : IJournalIndexQueue
    {
        public Task EnqueueAsync(Guid userId, Guid entryId, DateTimeOffset sourceUpdatedAtUtc, DateTimeOffset requestedAtUtc, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The queue is unavailable.");
    }
}
