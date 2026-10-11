using LifeOS.Application.Memory;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// AI-005.1: the AddJournalRetrievalV2Candidate migration against real PostgreSQL with pgvector. It only
// adds the journal-retrieval-v2 candidate function search_journal_memory_v2; search_journal_memory_v1
// stays the production default, unchanged. Its ranking is tested by the evaluation lab
// (tools/ai-evals/tests/test_journal_retrieval_v2.py) against this same migration.
[Collection(PostgreSqlCollection.Name)]
public class JournalRetrievalV2CandidateTests(PostgreSqlFixture fixture)
{
    private const string Signature =
        "(p_user_id uuid, p_query_embedding vector, p_query_text text, p_embedding_provider text, p_embedding_model text, p_chunking_version text, p_limit integer)";

    [Fact]
    public async Task Migration_AddsOnlyTheV2Function_AndV1StaysTheUnchangedDefault()
    {
        await using var scope = fixture.CreateScope();
        var database = Db(scope).Database;

        Assert.Equal("_AddJournalRetrievalV2Candidate", (await database.GetAppliedMigrationsAsync()).Last()[14..]);
        Assert.False(database.HasPendingModelChanges());
        Assert.Equal([$"search_journal_memory_v1{Signature}", $"search_journal_memory_v2{Signature}"],
            await Strings(database, "SELECT (proname || '(' || pg_get_function_arguments(oid) || ')') AS \"Value\" FROM pg_proc WHERE proname LIKE 'search_journal_memory%' ORDER BY proname"));
        Assert.Equal(["hnsw.iterative_scan=strict_order"],
            await Strings(database, "SELECT unnest(proconfig) AS \"Value\" FROM pg_proc WHERE proname = 'search_journal_memory_v2'"));

        // Only v2 prunes stop words from the lexical query; v1 is the AI-004 body.
        Assert.Equal(["search_journal_memory_v2"],
            await Strings(database, "SELECT proname::text AS \"Value\" FROM pg_proc WHERE proname LIKE 'search_journal_memory%' AND prosrc LIKE '%ts_lexize%'"));

        // The application still names and calls v1 only.
        Assert.Equal("journal-retrieval-v1", JournalMemoryPolicy.RetrievalVersion);
        Assert.Equal("search_journal_memory_v1", JournalMemoryPolicy.RetrievalFunction);
    }

    [Fact]
    public async Task V2_PrunesEnglishAndItalianStopWords_WithTheBuiltInDictionaries()
    {
        await using var scope = fixture.CreateScope();

        // The dictionaries v2 relies on exist in the server and classify stop words as an empty array.
        Assert.Equal(["after {}", "blood {blood}", "di {di}", "the {}"],
            await Strings(Db(scope).Database,
                "SELECT w || ' ' || ts_lexize('english_stem'::regdictionary, w)::text AS \"Value\" FROM unnest(ARRAY['after', 'blood', 'di', 'the']) AS w ORDER BY w"));
        Assert.Equal(["di {}", "ho {}", "isola {isol}", "qx7p2l {qx7p2l}"],
            await Strings(Db(scope).Database,
                "SELECT w || ' ' || ts_lexize('italian_stem'::regdictionary, w)::text AS \"Value\" FROM unnest(ARRAY['di', 'ho', 'isola', 'qx7p2l']) AS w ORDER BY w"));
    }

    // Down to AddJournalMemory (in a finally, back up again, so a failure can never leave the shared
    // database downgraded): only v2 goes; every table, index and v1 stay.
    [Fact]
    public async Task Migration_DownRemovesOnlyV2_AndUpRestoresIt()
    {
        await using var scope = fixture.CreateScope();
        var dbContext = Db(scope);
        var migrator = dbContext.GetService<IMigrator>();
        var applied = (await dbContext.Database.GetAppliedMigrationsAsync()).ToList();
        var tablesBefore = await TableCountAsync(dbContext.Database);
        var indexesBefore = await IndexDefinitionsAsync(dbContext.Database);
        var v1Before = await V1SourceAsync(dbContext.Database);

        try
        {
            await migrator.MigrateAsync(applied.Single(id => id.EndsWith("_AddJournalMemory", StringComparison.Ordinal)));

            Assert.Equal(["search_journal_memory_v1"],
                await Strings(dbContext.Database, "SELECT proname::text AS \"Value\" FROM pg_proc WHERE proname LIKE 'search_journal_memory%'"));
            Assert.Equal(tablesBefore, await TableCountAsync(dbContext.Database));
            Assert.Equal(indexesBefore, await IndexDefinitionsAsync(dbContext.Database));
            Assert.Equal(v1Before, await V1SourceAsync(dbContext.Database));
        }
        finally
        {
            await migrator.MigrateAsync();
        }

        Assert.Equal(applied, await dbContext.Database.GetAppliedMigrationsAsync());
        Assert.Equal(["search_journal_memory_v1", "search_journal_memory_v2"],
            await Strings(dbContext.Database, "SELECT proname::text AS \"Value\" FROM pg_proc WHERE proname LIKE 'search_journal_memory%' ORDER BY proname"));
        Assert.Equal(v1Before, await V1SourceAsync(dbContext.Database));
    }

    private static async Task<int> TableCountAsync(DatabaseFacade database) =>
        await database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'").SingleAsync();

    private static Task<List<string>> IndexDefinitionsAsync(DatabaseFacade database) =>
        Strings(database, "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE schemaname = 'public' ORDER BY indexdef");

    private static async Task<string> V1SourceAsync(DatabaseFacade database) =>
        (await Strings(database, "SELECT prosrc AS \"Value\" FROM pg_proc WHERE proname = 'search_journal_memory_v1'")).Single();

    private static LifeOSDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();

    private static Task<List<string>> Strings(DatabaseFacade database, string sql) =>
        database.SqlQueryRaw<string>(sql).ToListAsync();
}
