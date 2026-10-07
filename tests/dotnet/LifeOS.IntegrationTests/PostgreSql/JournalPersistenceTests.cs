using LifeOS.Application.Journal;
using LifeOS.Domain.Journal;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LifeOS.IntegrationTests.PostgreSql;

// JRN-001 against real PostgreSQL: the journal_entries schema, the repository (round trip, ownership,
// update, hard delete, keyset paging in timeline order), the timeline index and user deletion.
//
// The database is shared by the whole PostgreSQL collection, so every assertion is scoped to this
// test's own users.
[Collection(PostgreSqlCollection.Name)]
public class JournalPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    // ---- Schema ----

    [Fact]
    public async Task Migration_CreatesTheJournalTable()
    {
        await using var scope = fixture.CreateScope();
        var database = Db(scope).Database;

        Assert.Contains(await database.GetAppliedMigrationsAsync(), id => id.EndsWith("_AddJournalEntries", StringComparison.Ordinal));
        Assert.False(database.HasPendingModelChanges());

        Assert.Equal(
            [
                "id uuid NOT NULL", "user_id uuid NOT NULL", "occurred_at_utc timestamp with time zone NOT NULL",
                "title character varying(200) NULL", "content text NOT NULL",
                "created_at_utc timestamp with time zone NOT NULL", "updated_at_utc timestamp with time zone NOT NULL"
            ],
            await Columns(database, "journal_entries"));

        Assert.Equal(
            [
                "CREATE UNIQUE INDEX \"PK_journal_entries\" ON public.journal_entries USING btree (id)",
                "CREATE INDEX ix_journal_entries_user_timeline ON public.journal_entries USING btree (user_id, occurred_at_utc DESC, created_at_utc DESC, id DESC)"
            ],
            await Strings(database, "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'journal_entries' ORDER BY indexname"));

        // The user FK cascades (confdeltype 'c'); no vector, embedding or chunk columns exist.
        Assert.Equal(
            ["FK_journal_entries_users_user_id c", "ck_journal_entries_content", "ck_journal_entries_occurred_at", "ck_journal_entries_title"],
            await Constraints(database, "journal_entries"));
    }

    [Fact]
    public async Task InvalidRows_AreRejectedByTheDatabase()
    {
        var user = await NewUserAsync();
        var entry = await AddAsync(JournalEntry.Create(user.Id, Day, "Titolo", "Testo", Now));

        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_journal_entries_content", () =>
            Execute($"UPDATE journal_entries SET content = '   ' WHERE id = '{entry.Id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_journal_entries_content", () =>
            Execute($"UPDATE journal_entries SET content = repeat('c', {JournalEntry.ContentMaxLength + 1}) WHERE id = '{entry.Id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_journal_entries_title", () =>
            Execute($"UPDATE journal_entries SET title = ' ' WHERE id = '{entry.Id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_journal_entries_occurred_at", () =>
            Execute($"UPDATE journal_entries SET occurred_at_utc = '-infinity' WHERE id = '{entry.Id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.ForeignKeyViolation, "FK_journal_entries_users_user_id", () =>
            Execute($"UPDATE journal_entries SET user_id = '{Guid.CreateVersion7()}' WHERE id = '{entry.Id}'"));
    }

    // ---- Repository ----

    [Fact]
    public async Task RoundTrip_KeepsEveryField()
    {
        var user = await NewUserAsync();
        var entry = await AddAsync(JournalEntry.Create(user.Id,
            new DateTimeOffset(2026, 10, 6, 23, 15, 0, TimeSpan.FromHours(2)).AddTicks(1234567), "Una sera",
            "Riga uno\n\nRiga due — àèìòù 🌙", Now.AddTicks(9)));

        var stored = await GetAsync(user.Id, entry.Id);

        Assert.NotNull(stored);
        Assert.Equal((entry.Id, user.Id, entry.OccurredAtUtc, "Una sera", "Riga uno\n\nRiga due — àèìòù 🌙", entry.CreatedAtUtc, entry.UpdatedAtUtc),
            (stored.Id, stored.UserId, stored.OccurredAtUtc, stored.Title, stored.Content, stored.CreatedAtUtc, stored.UpdatedAtUtc));
        Assert.Equal(TimeSpan.Zero, stored.OccurredAtUtc.Offset);

        var untitled = await AddAsync(JournalEntry.Create(user.Id, Day, null, "Senza titolo", Now));
        Assert.Null((await GetAsync(user.Id, untitled.Id))!.Title);
    }

    [Fact]
    public async Task EveryOperation_IsScopedToTheOwner()
    {
        var owner = await NewUserAsync();
        var other = await NewUserAsync();
        var entry = await AddAsync(JournalEntry.Create(owner.Id, Day, null, "Privato", Now));

        Assert.Null(await GetAsync(other.Id, entry.Id));
        Assert.Empty(await PageAsync(other.Id, null, 10));

        // An update carrying another user id touches nothing.
        var hijack = JournalEntry.Create(other.Id, Day, null, "Rubato", Now);
        typeof(JournalEntry).GetProperty(nameof(JournalEntry.Id))!.SetValue(hijack, entry.Id);
        await using (var scope = fixture.CreateScope())
        {
            Assert.False(await Repository(scope).UpdateAsync(hijack, CancellationToken.None));
            Assert.False(await Repository(scope).DeleteAsync(other.Id, entry.Id, CancellationToken.None));
        }

        Assert.Equal("Privato", (await GetAsync(owner.Id, entry.Id))!.Content);
    }

    [Fact]
    public async Task Update_WritesTheEditedFields_AndDelete_IsHard()
    {
        var user = await NewUserAsync();
        var entry = await AddAsync(JournalEntry.Create(user.Id, Day, "Titolo", "Prima", Now));

        entry.Update(Day.AddDays(-2), null, "Dopo", Now.AddHours(1));

        await using (var scope = fixture.CreateScope())
        {
            Assert.True(await Repository(scope).UpdateAsync(entry, CancellationToken.None));
        }

        var stored = (await GetAsync(user.Id, entry.Id))!;
        Assert.Equal((Day.AddDays(-2), (string?)null, "Dopo", Now, Now.AddHours(1)),
            (stored.OccurredAtUtc, stored.Title, stored.Content, stored.CreatedAtUtc, stored.UpdatedAtUtc));

        await using (var scope = fixture.CreateScope())
        {
            Assert.True(await Repository(scope).DeleteAsync(user.Id, entry.Id, CancellationToken.None));
            Assert.False(await Repository(scope).DeleteAsync(user.Id, entry.Id, CancellationToken.None));
            Assert.False(await Repository(scope).UpdateAsync(entry, CancellationToken.None));
        }

        await using var check = fixture.CreateScope();
        Assert.False(await Db(check).JournalEntries.AnyAsync(row => row.Id == entry.Id));
    }

    [Fact]
    public async Task Pages_FollowTheTimeline_AndKeysetPagingCoversEveryRowOnce()
    {
        var user = await NewUserAsync();
        var entries = new List<JournalEntry>();

        // Several entries share an occurred-at instant and some also a created-at instant, so both
        // tie-breaks (created_at_utc, then id) are exercised by PostgreSQL's own ordering.
        for (var i = 0; i < 9; i++)
        {
            entries.Add(await AddAsync(JournalEntry.Create(user.Id, Day.AddDays(-(i / 3)), null, $"entry {i}", Now.AddSeconds(i % 2))));
        }

        var expected = entries
            .OrderByDescending(entry => entry.OccurredAtUtc)
            .ThenByDescending(entry => entry.CreatedAtUtc)
            .Select(entry => entry.Id)
            .ToList();

        var all = (await PageAsync(user.Id, null, 50)).Select(entry => entry.Id).ToList();
        Assert.Equal(9, all.Count);

        // Same as the expected order, except that the id tie-break is PostgreSQL's uuid order.
        Assert.Equal(
            expected.Select(id => entries.Single(entry => entry.Id == id)).Select(entry => (entry.OccurredAtUtc, entry.CreatedAtUtc)),
            all.Select(id => entries.Single(entry => entry.Id == id)).Select(entry => (entry.OccurredAtUtc, entry.CreatedAtUtc)));

        var walked = new List<Guid>();
        JournalCursor? cursor = null;

        while (true)
        {
            var page = await PageAsync(user.Id, cursor, 4);
            walked.AddRange(page.Select(entry => entry.Id));

            if (page.Count < 4)
            {
                break;
            }

            cursor = JournalCursor.After(page[^1]);
        }

        Assert.Equal(all, walked);
    }

    [Fact]
    public async Task TimelineQuery_UsesTheTimelineIndex()
    {
        var user = await NewUserAsync();
        await AddAsync(JournalEntry.Create(user.Id, Day, null, "Testo", Now));

        await using var scope = fixture.CreateScope();
        await using var connection = new NpgsqlConnection(Db(scope).Database.GetConnectionString());
        await connection.OpenAsync();

        // A tiny table would be sequentially scanned; the planner must be able to serve the user's
        // newest-first page from the timeline index without a sort.
        await using (var disable = new NpgsqlCommand("SET enable_seqscan = off", connection))
        {
            await disable.ExecuteNonQueryAsync();
        }

        await using var explain = new NpgsqlCommand(
            $"""
            EXPLAIN SELECT * FROM journal_entries WHERE user_id = '{user.Id}'
            ORDER BY occurred_at_utc DESC, created_at_utc DESC, id DESC LIMIT 21
            """, connection);
        var lines = new List<string>();

        await using (var reader = await explain.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                lines.Add(reader.GetString(0));
            }
        }

        var plan = string.Join('\n', lines);

        Assert.Contains("ix_journal_entries_user_timeline", plan);
        Assert.DoesNotContain("Sort", plan);
    }

    [Fact]
    public async Task DeletingTheUser_DeletesTheirJournal_Only()
    {
        var user = await NewUserAsync();
        var other = await NewUserAsync();
        var entry = await AddAsync(JournalEntry.Create(user.Id, Day, null, "Mio", Now));
        var kept = await AddAsync(JournalEntry.Create(other.Id, Day, null, "Altrui", Now));

        await using (var scope = fixture.CreateScope())
        {
            Assert.Equal(1, await Db(scope).Users.Where(row => row.Id == user.Id).ExecuteDeleteAsync());
        }

        await using var check = fixture.CreateScope();
        Assert.False(await Db(check).JournalEntries.AnyAsync(row => row.Id == entry.Id));
        Assert.True(await Db(check).JournalEntries.AnyAsync(row => row.Id == kept.Id));
    }

    // ---- Helpers ----

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now.AddDays(-30));
        await PostgresAssert.InsertAsync(fixture, user);

        return user;
    }

    private async Task<JournalEntry> AddAsync(JournalEntry entry)
    {
        await using var scope = fixture.CreateScope();
        await Repository(scope).AddAsync(entry, CancellationToken.None);

        return entry;
    }

    private async Task<JournalEntry?> GetAsync(Guid userId, Guid id)
    {
        await using var scope = fixture.CreateScope();

        return await Repository(scope).GetAsync(userId, id, CancellationToken.None);
    }

    private async Task<IReadOnlyList<JournalEntry>> PageAsync(Guid userId, JournalCursor? after, int take)
    {
        await using var scope = fixture.CreateScope();

        return await Repository(scope).GetPageAsync(userId, after, take, CancellationToken.None);
    }

    private async Task Execute(string sql)
    {
        await using var scope = fixture.CreateScope();
        await Db(scope).Database.ExecuteSqlRawAsync(sql);
    }

    private static IJournalEntryRepository Repository(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IJournalEntryRepository>();

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
}
