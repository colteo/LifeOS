using LifeOS.Application.Finance.Assistant;
using LifeOS.Domain.Assistant;
using LifeOS.Domain.Finance.Assistant;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using FinanceOperation = LifeOS.Domain.Assistant.AssistantOperation<LifeOS.Domain.Finance.Assistant.FinanceTransactionDraft>;

namespace LifeOS.IntegrationTests.PostgreSql;

// AI-007 F2: the assistant_operations table on the real migration chain — columns and constraints, composite
// ownership, one open operation per conversation, the Executed ⇔ transaction invariant, the versioned jsonb
// draft, cascades (that never reach Finance) and the migration's Down/Up. Rows are written with SQL where the
// point is what the database itself refuses.
[Collection(PostgreSqlCollection.Name)]
public class AssistantOperationPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    private const string OwnershipForeignKey = "FK_assistant_operations_conversations";
    private const string OpenIndex = "ux_assistant_operations_conversation_open";
    private const string ResultIndex = "ux_assistant_operations_result_transaction";

    [Fact]
    public async Task Migration_CreatesTheOperationsTable()
    {
        await using var scope = fixture.CreateScope();
        var database = Db(scope).Database;

        Assert.Contains(await database.GetAppliedMigrationsAsync(), id => id.EndsWith("_AddAssistantOperations", StringComparison.Ordinal));
        Assert.False(database.HasPendingModelChanges());

        Assert.Equal(
            [
                "id uuid NOT NULL", "user_id uuid NOT NULL", "conversation_id uuid NOT NULL", "kind character varying(64) NOT NULL",
                "status character varying(32) NOT NULL", "draft_version integer NOT NULL", "payload_version integer NOT NULL", "draft jsonb NOT NULL",
                "expires_at_utc timestamp with time zone NOT NULL", "result_transaction_id uuid NULL", "created_at_utc timestamp with time zone NOT NULL",
                "updated_at_utc timestamp with time zone NOT NULL", "executed_at_utc timestamp with time zone NULL"
            ],
            await Strings(database,
                """
                SELECT attname || ' ' || format_type(atttypid, atttypmod) || CASE WHEN attnotnull THEN ' NOT NULL' ELSE ' NULL' END AS "Value"
                FROM pg_attribute WHERE attrelid = 'assistant_operations'::regclass AND attnum > 0 AND NOT attisdropped ORDER BY attnum
                """));

        // One foreign key only (the conversation, cascading): none to transactions.
        Assert.Equal(
            [
                "FK_assistant_operations_conversations c assistant_conversations", "ck_assistant_operations_draft", "ck_assistant_operations_kind",
                "ck_assistant_operations_result", "ck_assistant_operations_status", "ck_assistant_operations_times"
            ],
            (await Strings(database,
                """
                SELECT conname::text || CASE WHEN contype = 'f' THEN ' ' || confdeltype::text || ' ' || confrelid::regclass::text ELSE '' END AS "Value"
                FROM pg_constraint WHERE conrelid = 'assistant_operations'::regclass AND contype IN ('c', 'f')
                """)).Order(StringComparer.Ordinal));

        var indexes = await Strings(database,
            "SELECT indexname || ': ' || indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'assistant_operations' ORDER BY indexname COLLATE \"C\"");
        Assert.Contains(indexes, index => index.StartsWith($"{OpenIndex}: CREATE UNIQUE INDEX", StringComparison.Ordinal)
            && index.EndsWith("(conversation_id) WHERE ((status)::text = ANY ((ARRAY['Collecting'::character varying, 'AwaitingConfirmation'::character varying])::text[]))",
                StringComparison.Ordinal));
        Assert.Contains(indexes, index => index.StartsWith($"{ResultIndex}: CREATE UNIQUE INDEX", StringComparison.Ordinal)
            && index.EndsWith("(result_transaction_id) WHERE (result_transaction_id IS NOT NULL)", StringComparison.Ordinal));
        Assert.Contains(indexes, index => index.StartsWith("ix_assistant_operations_conversation:", StringComparison.Ordinal)
            && index.EndsWith("(conversation_id, user_id, created_at_utc)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnOperation_CannotReferenceAnotherUsersConversation()
    {
        var owner = await NewUserAsync();
        var intruder = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);

        await PostgresAssert.ViolatesAsync(PostgresAssert.ForeignKeyViolation, OwnershipForeignKey,
            () => InsertRowAsync(intruder.Id, conversation));
        await PostgresAssert.ViolatesAsync(PostgresAssert.ForeignKeyViolation, OwnershipForeignKey,
            () => InsertRowAsync(owner.Id, Guid.CreateVersion7()));

        // Through the repository: the same refusal, reported as a missing conversation.
        await using var scope = fixture.CreateScope();
        Assert.Equal(FinanceOperationAddOutcome.ConversationNotFound,
            await Repository(scope).TryAddAsync(NewOperation(intruder.Id, conversation), default));
        Assert.Equal(0, await CountAsync(conversation));
    }

    [Fact]
    public async Task AConversation_HasAtMostOneOpenOperation()
    {
        var owner = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);
        var first = await InsertRowAsync(owner.Id, conversation, "Collecting");

        await PostgresAssert.ViolatesAsync(PostgresAssert.UniqueViolation, OpenIndex,
            () => InsertRowAsync(owner.Id, conversation, "AwaitingConfirmation"));

        // The repository's insert loses quietly to the existing open operation.
        await using (var scope = fixture.CreateScope())
        {
            Assert.Equal(FinanceOperationAddOutcome.OpenOperationExists, await Repository(scope).TryAddAsync(NewOperation(owner.Id, conversation), default));
        }

        // Closed operations do not count: any number of them, and a new open one.
        await Execute($"UPDATE assistant_operations SET status = 'Cancelled' WHERE id = '{first}'");
        await InsertRowAsync(owner.Id, conversation, "Expired");
        await InsertRowAsync(owner.Id, conversation, "Collecting");

        Assert.Equal(3, await CountAsync(conversation));
    }

    [Fact]
    public async Task InvalidRows_AreRejectedByTheDatabase()
    {
        var owner = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);
        var transaction = Guid.CreateVersion7();

        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_assistant_operations_result",
            () => InsertRowAsync(owner.Id, conversation, "Executed"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_assistant_operations_result",
            () => InsertRowAsync(owner.Id, conversation, "Cancelled", resultTransactionId: transaction));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_assistant_operations_status",
            () => InsertRowAsync(owner.Id, conversation, "Failed"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_assistant_operations_kind",
            () => InsertRowAsync(owner.Id, conversation, kind: "finance.transfer.create"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_assistant_operations_draft",
            () => InsertRowAsync(owner.Id, conversation, draft: "[]"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_assistant_operations_draft",
            () => InsertRowAsync(owner.Id, conversation, draftVersion: 0));

        // One transaction is the result of at most one operation.
        await InsertRowAsync(owner.Id, conversation, "Executed", resultTransactionId: transaction);
        var other = await NewConversationAsync(owner);
        await PostgresAssert.ViolatesAsync(PostgresAssert.UniqueViolation, ResultIndex,
            () => InsertRowAsync(owner.Id, other, "Executed", resultTransactionId: transaction));
    }

    [Fact]
    public async Task TheDraft_IsAVersionedJsonbDocument_ReadBackExactly()
    {
        var owner = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);
        var draft = FinanceTransactionDraft.Create(TransactionType.Expense, 5.00m, "EUR", Guid.CreateVersion7(), Guid.CreateVersion7(),
            FinanceDraftCategorySource.Suggested, "colazione con Pamela", new DateOnly(2026, 10, 23), null,
            FinanceDraftOccurrence.Create(new DateOnly(2026, 10, 23), FinanceDraftOccurrence.Noon, FinanceDraftDateSource.Explicit,
                FinanceDraftTimeSource.Noon, "Europe/Rome", new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero)));
        var operation = FinanceOperation.Create(owner.Id, conversation, AssistantOperationKinds.FinanceTransactionCreate, 1, draft, Now);

        await using (var scope = fixture.CreateScope())
        {
            Assert.Equal(FinanceOperationAddOutcome.Added, await Repository(scope).TryAddAsync(operation, default));
        }

        await using (var scope = fixture.CreateScope())
        {
            var stored = (await Repository(scope).GetAsync(owner.Id, operation.Id, default))!;
            Assert.Equal(draft, stored.Draft);
            Assert.Equal("5.00", stored.Draft.Amount!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); // the scale survives
            Assert.Equal((operation.ExpiresAtUtc, operation.CreatedAtUtc, 1, 1), (stored.ExpiresAtUtc, stored.CreatedAtUtc, stored.DraftVersion, stored.PayloadVersion));
            Assert.Null(await Repository(scope).GetAsync(Guid.CreateVersion7(), operation.Id, default));
        }

        // Amounts are strings in the document (no floating point), enums are names.
        await using var jsonScope = fixture.CreateScope();
        var json = await Strings(Db(jsonScope).Database,
            $"SELECT draft ->> 'amount' || '|' || (draft ->> 'type') || '|' || (draft -> 'occurrence' ->> 'timeSource') || '|' || jsonb_typeof(draft -> 'amount') AS \"Value\" FROM assistant_operations WHERE id = '{operation.Id}'");
        Assert.Equal(["5.00|Expense|Noon|string"], json);

        // An unknown payload version is never guessed.
        await Execute($"UPDATE assistant_operations SET payload_version = 2 WHERE id = '{operation.Id}'");
        await using (var scope = fixture.CreateScope())
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => Repository(scope).GetAsync(owner.Id, operation.Id, default));
        }
    }

    [Fact]
    public async Task ConditionalUpdates_WriteOnlyOverTheStateTheyRead()
    {
        var owner = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);
        var operation = NewOperation(owner.Id, conversation);

        await using var scope = fixture.CreateScope();
        var repository = Repository(scope);
        Assert.Equal(FinanceOperationAddOutcome.Added, await repository.TryAddAsync(operation, default));

        var (status, version) = (operation.Status, operation.DraftVersion);
        Assert.True(operation.ReviseDraft(FinanceTransactionDraft.Create(TransactionType.Income, 7m, null, null, null, null, null, null, null, null), Now.AddMinutes(1)));
        Assert.True(await repository.TryUpdateAsync(operation, status, version, default));

        // A writer that read version 1 loses; so does one that read another status.
        Assert.False(await repository.TryUpdateAsync(operation, status, version, default));
        Assert.False(await repository.TryUpdateAsync(operation, AssistantOperationStatus.AwaitingConfirmation, 2, default));
        // Another user's id never matches.
        var foreign = FinanceOperation.Restore(operation.Id, Guid.CreateVersion7(), conversation, operation.Kind, operation.Status, 3, 1, operation.Draft,
            operation.ExpiresAtUtc, null, operation.CreatedAtUtc, operation.UpdatedAtUtc, null);
        Assert.False(await repository.TryUpdateAsync(foreign, AssistantOperationStatus.Collecting, 2, default));

        var stored = (await repository.GetAsync(owner.Id, operation.Id, default))!;
        Assert.Equal((2, TransactionType.Income, Now.AddMinutes(1).AddHours(24)), (stored.DraftVersion, stored.Draft.Type, stored.ExpiresAtUtc));

        // The confirmation lock is a unit-of-work operation only.
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.TryLockForConfirmationAsync(owner.Id, operation.Id, 2, Now, default));
    }

    [Fact]
    public async Task DeletingAConversation_DeletesItsOperations()
    {
        var owner = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);
        var transaction = Guid.CreateVersion7();
        await InsertRowAsync(owner.Id, conversation, "Executed", resultTransactionId: transaction);
        await InsertRowAsync(owner.Id, conversation, "Collecting");

        await Execute($"DELETE FROM assistant_conversations WHERE id = '{conversation}'");

        Assert.Equal(0, await CountAsync(conversation));
    }

    // Down drops exactly assistant_operations; Up recreates it. The collection runs one test at a time, so the
    // shared schema is complete again before the next test.
    [Fact]
    public async Task Migration_DownAndUp_TouchOnlyTheOperationsTable()
    {
        var owner = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);

        await using var scope = fixture.CreateScope();
        var database = Db(scope).Database;
        var migrator = database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        var migrations = database.GetMigrations().ToList();
        var migration = migrations.Single(id => id.EndsWith("_AddAssistantOperations", StringComparison.Ordinal));
        var previous = migrations[migrations.IndexOf(migration) - 1];
        var tablesBefore = await Tables(database);

        Assert.EndsWith("_AddAssistantConversations", previous, StringComparison.Ordinal);

        try
        {
            await migrator.MigrateAsync(previous);

            Assert.Equal(tablesBefore.Except(["assistant_operations"]), await Tables(database));
            Assert.True(await Db(scope).AssistantConversations.AnyAsync(stored => stored.Id == conversation));
        }
        finally
        {
            await migrator.MigrateAsync();
        }

        Assert.Equal(tablesBefore, await Tables(database));
        Assert.Empty(await database.GetPendingMigrationsAsync());
    }

    // ---- Helpers ----

    private static FinanceOperation NewOperation(Guid userId, Guid conversationId) =>
        FinanceOperation.Create(userId, conversationId, AssistantOperationKinds.FinanceTransactionCreate, 1,
            FinanceTransactionDraft.Create(TransactionType.Expense, 5m, null, null, null, null, null, null, null, null), Now);

    private async Task<Guid> InsertRowAsync(Guid userId, Guid conversationId, string status = "Collecting", Guid? resultTransactionId = null,
        string kind = AssistantOperationKinds.FinanceTransactionCreate, string draft = "{}", int draftVersion = 1)
    {
        var id = Guid.CreateVersion7();
        DateTimeOffset? executedAt = status == "Executed" ? Now : null;

        await using var scope = fixture.CreateScope();
        await Db(scope).Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO assistant_operations (id, user_id, conversation_id, kind, status, draft_version, payload_version, draft,
                expires_at_utc, result_transaction_id, created_at_utc, updated_at_utc, executed_at_utc)
            VALUES ({id}, {userId}, {conversationId}, {kind}, {status}, {draftVersion}, 1, CAST({draft} AS jsonb), {Now.AddHours(24)},
                {resultTransactionId}, {Now}, {Now}, {executedAt})
            """);

        return id;
    }

    private async Task<int> CountAsync(Guid conversationId)
    {
        await using var scope = fixture.CreateScope();
        return await Db(scope).Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM assistant_operations WHERE conversation_id = {conversationId}").SingleAsync();
    }

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now.AddDays(-30));
        await PostgresAssert.InsertAsync(fixture, user);

        return user;
    }

    private async Task<Guid> NewConversationAsync(User owner)
    {
        var conversation = AssistantConversation.Create(owner.Id, Guid.CreateVersion7(), Now);
        await PostgresAssert.InsertAsync(fixture, conversation);

        return conversation.Id;
    }

    private async Task Execute(string sql)
    {
        await using var scope = fixture.CreateScope();
        await Db(scope).Database.ExecuteSqlRawAsync(sql);
    }

    private static IFinanceOperationRepository Repository(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IFinanceOperationRepository>();

    private static LifeOSDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();

    private static Task<List<string>> Tables(DatabaseFacade database) =>
        Strings(database, "SELECT tablename::text AS \"Value\" FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename COLLATE \"C\"");

    private static Task<List<string>> Strings(DatabaseFacade database, string sql) =>
        database.SqlQueryRaw<string>(sql).ToListAsync();
}
