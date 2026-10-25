using LifeOS.Domain.Assistant;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// AI-007 F1 §9.1-9.2 against real PostgreSQL: the assistant schema, the ownership foreign keys, the
// idempotency and ordering indexes, the optimistic concurrency tokens (sequence allocation and turn
// fencing) and the cascades. There are no repositories or handlers yet (F1.1): the tests use the
// DbContext directly, one scope per simulated request.
//
// The database is shared by the whole PostgreSQL collection, so every assertion is scoped to this
// test's own users and conversations.
[Collection(PostgreSqlCollection.Name)]
public class AssistantPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Lease = Now.AddSeconds(90);

    // ---- Schema ----

    [Fact]
    public async Task Migration_CreatesTheAssistantTables()
    {
        await using var scope = fixture.CreateScope();
        var database = Db(scope).Database;

        Assert.Contains(await database.GetAppliedMigrationsAsync(), id => id.EndsWith("_AddAssistantConversations", StringComparison.Ordinal));
        Assert.False(database.HasPendingModelChanges());

        Assert.Equal(
            [
                "id uuid NOT NULL", "user_id uuid NOT NULL", "client_conversation_id uuid NOT NULL", "next_sequence integer NOT NULL",
                "created_at_utc timestamp with time zone NOT NULL", "updated_at_utc timestamp with time zone NOT NULL"
            ],
            await Columns(database, "assistant_conversations"));
        Assert.Equal(
            ["FK_assistant_conversations_users_user_id c", "ck_assistant_conversations_next_sequence", "ck_assistant_conversations_updated_at"],
            await Constraints(database, "assistant_conversations"));

        Assert.Equal(
            [
                "id uuid NOT NULL", "conversation_id uuid NOT NULL", "user_id uuid NOT NULL", "sequence integer NOT NULL",
                "role character varying(16) NOT NULL", "content text NOT NULL", "client_message_id uuid NULL", "reply_to_message_id uuid NULL",
                "turn_status character varying(16) NULL", "turn_attempts integer NULL", "turn_lease_until_utc timestamp with time zone NULL",
                "failure_code character varying(64) NULL", "created_at_utc timestamp with time zone NOT NULL"
            ],
            await Columns(database, "assistant_messages"));
        Assert.Equal(
            [
                "FK_assistant_messages_conversations c", "FK_assistant_messages_reply_to c", "ck_assistant_messages_content",
                "ck_assistant_messages_failure_code", "ck_assistant_messages_role", "ck_assistant_messages_sequence", "ck_assistant_messages_shape",
                "ck_assistant_messages_turn", "ck_assistant_messages_turn_status"
            ],
            await Constraints(database, "assistant_messages"));

        var indexes = await Strings(database,
            "SELECT indexname || ': ' || indexdef AS \"Value\" FROM pg_indexes WHERE tablename LIKE 'assistant_%' ORDER BY indexname COLLATE \"C\"");
        Assert.Contains(indexes, index => index.StartsWith("ux_assistant_conversations_user_client: CREATE UNIQUE INDEX", StringComparison.Ordinal)
            && index.EndsWith("(user_id, client_conversation_id)", StringComparison.Ordinal));
        Assert.Contains(indexes, index => index.StartsWith("ix_assistant_conversations_user_updated:", StringComparison.Ordinal)
            && index.EndsWith("(user_id, updated_at_utc DESC, id DESC)", StringComparison.Ordinal));
        Assert.Contains(indexes, index => index.StartsWith("ux_assistant_messages_conversation_sequence: CREATE UNIQUE INDEX", StringComparison.Ordinal)
            && index.EndsWith("(conversation_id, sequence)", StringComparison.Ordinal));
        Assert.Contains(indexes, index => index.StartsWith("ux_assistant_messages_conversation_client: CREATE UNIQUE INDEX", StringComparison.Ordinal)
            && index.EndsWith("(conversation_id, client_message_id)", StringComparison.Ordinal));
        Assert.Contains(indexes, index => index.StartsWith("ux_assistant_messages_conversation_reply: CREATE UNIQUE INDEX", StringComparison.Ordinal)
            && index.EndsWith("(conversation_id, reply_to_message_id)", StringComparison.Ordinal));
        Assert.Contains(indexes, index => index.StartsWith("ux_assistant_messages_conversation_processing: CREATE UNIQUE INDEX", StringComparison.Ordinal)
            && index.EndsWith("(conversation_id) WHERE ((turn_status)::text = 'Processing'::text)", StringComparison.Ordinal));
    }

    // Down drops exactly the two assistant tables; Up recreates them. The collection runs one test at a
    // time, so the shared schema is complete again before the next test.
    [Fact]
    public async Task Migration_DownAndUp_TouchOnlyTheAssistantTables()
    {
        var user = await NewUserAsync();
        await AddAsync(AssistantConversation.Create(user.Id, Guid.CreateVersion7(), Now));

        await using var scope = fixture.CreateScope();
        var database = Db(scope).Database;
        var migrator = database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        var migrations = database.GetMigrations().ToList();
        var migration = migrations.Single(id => id.EndsWith("_AddAssistantConversations", StringComparison.Ordinal));
        var previous = migrations[migrations.IndexOf(migration) - 1];
        var tablesBefore = await Tables(database);

        try
        {
            await migrator.MigrateAsync(previous);

            // AI-007 F2's assistant_operations (a later migration) goes too.
            Assert.Equal(tablesBefore.Except(["assistant_conversations", "assistant_messages", "assistant_operations"]), await Tables(database));
            Assert.True(await Db(scope).Users.AnyAsync(stored => stored.Id == user.Id));
        }
        finally
        {
            await migrator.MigrateAsync();
        }

        Assert.Equal(tablesBefore, await Tables(database));
        Assert.Empty(await database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task InvalidRows_AreRejectedByTheDatabase()
    {
        var (conversation, user, reply) = await NewCompletedTurnAsync();
        var (u, a) = (user.Id, reply.Id);

        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_assistant_messages_content",
            () => Execute($"UPDATE assistant_messages SET content = repeat('x', {AssistantMessage.MaxUserContentLength + 1}) WHERE id = '{u}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_assistant_messages_content",
            () => Execute($"UPDATE assistant_messages SET content = repeat('x', {AssistantMessage.MaxAssistantContentLength + 1}) WHERE id = '{a}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_assistant_messages_content",
            () => Execute($"UPDATE assistant_messages SET content = '   ' WHERE id = '{u}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_assistant_messages_role",
            () => Execute($"UPDATE assistant_messages SET role = 'System' WHERE id = '{a}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_assistant_messages_shape",
            () => Execute($"UPDATE assistant_messages SET client_message_id = NULL WHERE id = '{u}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_assistant_messages_shape",
            () => Execute($"UPDATE assistant_messages SET turn_status = 'Completed', turn_attempts = 1 WHERE id = '{a}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_assistant_messages_shape",
            () => Execute($"UPDATE assistant_messages SET turn_attempts = 0 WHERE id = '{u}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_assistant_messages_turn",
            () => Execute($"UPDATE assistant_messages SET turn_status = 'Processing' WHERE id = '{u}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_assistant_messages_turn",
            () => Execute($"UPDATE assistant_messages SET turn_status = 'Failed' WHERE id = '{u}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_assistant_messages_turn",
            () => Execute($"UPDATE assistant_messages SET failure_code = 'ai_unavailable' WHERE id = '{u}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_assistant_messages_turn_status",
            () => Execute($"UPDATE assistant_messages SET turn_status = 'Accepted' WHERE id = '{u}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_assistant_messages_failure_code",
            () => Execute($"UPDATE assistant_messages SET turn_status = 'Failed', failure_code = 'Provider said: 500' WHERE id = '{u}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_assistant_messages_sequence",
            () => Execute($"UPDATE assistant_messages SET sequence = 0 WHERE id = '{u}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_assistant_conversations_next_sequence",
            () => Execute($"UPDATE assistant_conversations SET next_sequence = 0 WHERE id = '{conversation.Id}'"));
    }

    // ---- Round trip and ordering ----

    [Fact]
    public async Task Messages_RoundTrip_InSequenceOrder_AtTheLengthLimits()
    {
        var owner = await NewUserAsync();
        var conversation = AssistantConversation.Create(owner.Id, Guid.CreateVersion7(), Now);
        var clientId = Guid.CreateVersion7();
        var user = conversation.AppendUserMessage(clientId, new string('u', AssistantMessage.MaxUserContentLength), Now, Lease);
        var reply = conversation.CompleteTurn(user, 1, new string('a', AssistantMessage.MaxAssistantContentLength), Now.AddSeconds(2));
        var next = conversation.AppendUserMessage(Guid.CreateVersion7(), "Ieri ho speso 10 euro\nal supermercato", Now.AddSeconds(5), Lease.AddSeconds(5));

        // Inserted out of order: the order is the sequence, never the insertion or the timestamps.
        await AddAsync(conversation);
        await AddAsync(next, reply, user);

        await using var scope = fixture.CreateScope();
        var stored = await Db(scope).AssistantMessages.AsNoTracking()
            .Where(message => message.ConversationId == conversation.Id && message.UserId == owner.Id)
            .OrderBy(message => message.Sequence)
            .ToListAsync();

        Assert.Equal([user.Id, reply.Id, next.Id], stored.Select(message => message.Id));
        Assert.Equal([1, 2, 3], stored.Select(message => message.Sequence));
        Assert.Equal((AssistantMessageRole.User, clientId, AssistantTurnStatus.Completed, 1),
            (stored[0].Role, stored[0].ClientMessageId, stored[0].TurnStatus, stored[0].TurnAttempts));
        Assert.Equal((AssistantMessageRole.Assistant, user.Id), (stored[1].Role, stored[1].ReplyToMessageId));
        Assert.Equal((AssistantMessage.MaxUserContentLength, AssistantMessage.MaxAssistantContentLength), (stored[0].Content.Length, stored[1].Content.Length));
        Assert.Equal("Ieri ho speso 10 euro\nal supermercato", stored[2].Content);
        Assert.Equal((AssistantTurnStatus.Processing, Lease.AddSeconds(5)), (stored[2].TurnStatus, stored[2].TurnLeaseUntilUtc));

        var storedConversation = await Db(scope).AssistantConversations.AsNoTracking().SingleAsync(c => c.Id == conversation.Id);
        Assert.Equal((4, Now.AddSeconds(5)), (storedConversation.NextSequence, storedConversation.UpdatedAtUtc));
    }

    [Fact]
    public async Task ADuplicateSequence_IsRejected()
    {
        var (conversation, user, _) = await NewCompletedTurnAsync();

        await PostgresAssert.ViolatesAsync(PostgresAssert.UniqueViolation, AssistantConfigurationNames.Sequence,
            () => Execute(
                $"""
                INSERT INTO assistant_messages (id, conversation_id, user_id, sequence, role, content, client_message_id, turn_status, turn_attempts, created_at_utc)
                VALUES ('{Guid.CreateVersion7()}', '{conversation.Id}', '{user.UserId}', 1, 'User', 'Altro', '{Guid.CreateVersion7()}', 'Completed', 1, now())
                """));
    }

    // ---- Ownership ----

    [Fact]
    public async Task AMessage_CannotBelongToAnotherUserOrPointIntoAnotherConversation()
    {
        var (conversation, user, reply) = await NewCompletedTurnAsync();
        var other = await NewUserAsync();
        var (otherConversation, otherUser, _) = await NewCompletedTurnAsync(other);
        var pending = AssistantConversation.Create(conversation.UserId, Guid.CreateVersion7(), Now);
        var unanswered = pending.AppendUserMessage(Guid.CreateVersion7(), "Ciao", Now, Lease);
        await AddAsync(pending, unanswered);

        // The message's owner must be the conversation's owner: another owner on the same conversation,
        // or the same owner on another user's conversation (sequence moved to stay clear of the other indexes).
        await PostgresAssert.ViolatesAsync(PostgresAssert.ForeignKeyViolation, AssistantConfigurationNames.MessageConversation,
            () => Execute($"UPDATE assistant_messages SET user_id = '{other.Id}' WHERE id = '{unanswered.Id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.ForeignKeyViolation, AssistantConfigurationNames.MessageConversation,
            () => Execute($"UPDATE assistant_messages SET conversation_id = '{otherConversation.Id}', sequence = 99 WHERE id = '{unanswered.Id}'"));

        // A reply can only answer a message of its own conversation.
        await PostgresAssert.ViolatesAsync(PostgresAssert.ForeignKeyViolation, AssistantConfigurationNames.Reply,
            () => Execute($"UPDATE assistant_messages SET reply_to_message_id = '{otherUser.Id}' WHERE id = '{reply.Id}'"));

        await using var scope = fixture.CreateScope();
        Assert.Equal(2, await Db(scope).AssistantMessages.CountAsync(message => message.ConversationId == conversation.Id && message.UserId == user.UserId));
    }

    [Fact]
    public async Task ConversationsOfOneUser_AreInvisibleToAnotherUser()
    {
        var (conversation, _, _) = await NewCompletedTurnAsync();
        var other = await NewUserAsync();

        await using var scope = fixture.CreateScope();
        var db = Db(scope);

        Assert.False(await db.AssistantConversations.AnyAsync(c => c.Id == conversation.Id && c.UserId == other.Id));
        Assert.Empty(await db.AssistantMessages.Where(m => m.ConversationId == conversation.Id && m.UserId == other.Id).ToListAsync());
    }

    // ---- Idempotency ----

    [Fact]
    public async Task ClientConversationId_IsUniquePerUser()
    {
        var user = await NewUserAsync();
        var other = await NewUserAsync();
        var clientId = Guid.CreateVersion7();
        await AddAsync(AssistantConversation.Create(user.Id, clientId, Now));

        await PostgresAssert.InsertViolatesAsync(fixture, PostgresAssert.UniqueViolation, AssistantConfigurationNames.ClientConversation,
            AssistantConversation.Create(user.Id, clientId, Now));

        // The key is scoped to its owner: another user may use the same value.
        await AddAsync(AssistantConversation.Create(other.Id, clientId, Now));
    }

    [Fact]
    public async Task AReplayedClientMessageId_NeverStoresASecondMessage()
    {
        var (conversation, user, _) = await NewCompletedTurnAsync();

        // A fresh request that read the current conversation: only the client id index can stop it.
        await using (var scope = fixture.CreateScope())
        {
            var db = Db(scope);
            var current = await db.AssistantConversations.SingleAsync(c => c.Id == conversation.Id);
            db.Add(current.AppendUserMessage(user.ClientMessageId!.Value, "Ciao", Now.AddMinutes(1), Lease.AddMinutes(1)));

            await PostgresAssert.ViolatesAsync(PostgresAssert.UniqueViolation, AssistantConfigurationNames.ClientMessage, () => db.SaveChangesAsync());
        }

        await using (var scope = fixture.CreateScope())
        {
            var db = Db(scope);
            Assert.Equal(1, await db.AssistantMessages.CountAsync(m => m.ConversationId == conversation.Id && m.ClientMessageId == user.ClientMessageId));
            Assert.Equal(3, (await db.AssistantConversations.SingleAsync(c => c.Id == conversation.Id)).NextSequence);
        }

        // The key is scoped to its conversation.
        var another = AssistantConversation.Create(conversation.UserId, Guid.CreateVersion7(), Now);
        await AddAsync(another, another.AppendUserMessage(user.ClientMessageId!.Value, "Ciao", Now, Lease));
    }

    [Fact]
    public async Task ConcurrentRequestsWithTheSameClientMessageId_StoreExactlyOneMessage()
    {
        var conversation = await NewConversationAsync();
        var clientId = Guid.CreateVersion7();

        var outcomes = await RaceAsync(8, db => AppendAsync(db, conversation.Id, clientId));

        Assert.Equal(1, outcomes.Count(outcome => outcome is null));
        Assert.All(outcomes.OfType<Exception>(), exception => Assert.IsAssignableFrom<DbUpdateException>(exception));
        await AssertMessagesAsync(conversation.Id, expectedCount: 1);
    }

    // ---- Concurrency: sequence allocation and one turn at a time ----

    // A writer that read next_sequence before another writer moved it saves nothing (concurrency token).
    [Fact]
    public async Task AStaleSequence_IsRejected_AndNothingIsSaved()
    {
        var conversation = await NewConversationAsync();

        await using var scope = fixture.CreateScope();
        var db = Db(scope);
        var stale = await db.AssistantConversations.SingleAsync(c => c.Id == conversation.Id);
        var message = stale.AppendUserMessage(Guid.CreateVersion7(), "Ciao", Now, Lease);
        db.Add(message);

        await Execute($"UPDATE assistant_conversations SET next_sequence = next_sequence + 1 WHERE id = '{conversation.Id}'");

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());

        // The message insert was rolled back with the failed conversation update.
        await using var check = fixture.CreateScope();
        Assert.False(await Db(check).AssistantMessages.AnyAsync(m => m.ConversationId == conversation.Id));
    }

    // Concurrent messages: exactly one turn is accepted; every other request fails as a whole (stale
    // sequence, duplicate sequence or a second Processing turn), so no sequence is ever duplicated.
    [Fact]
    public async Task ConcurrentMessages_AcceptExactlyOneTurn()
    {
        var conversation = await NewConversationAsync();

        var outcomes = await RaceAsync(8, db => AppendAsync(db, conversation.Id, Guid.CreateVersion7()));

        Assert.Equal(1, outcomes.Count(outcome => outcome is null));
        Assert.All(outcomes.OfType<Exception>(), exception => Assert.IsAssignableFrom<DbUpdateException>(exception));
        await AssertMessagesAsync(conversation.Id, expectedCount: 1);
    }

    // While a turn is Processing, a request that read the current sequence still cannot open a second one.
    [Fact]
    public async Task ASecondProcessingTurn_IsRejected()
    {
        var conversation = await NewConversationAsync();
        await using (var scope = fixture.CreateScope())
        {
            await AppendAsync(Db(scope), conversation.Id, Guid.CreateVersion7());
        }

        await using (var scope = fixture.CreateScope())
        {
            var db = Db(scope);
            await PostgresAssert.ViolatesAsync(PostgresAssert.UniqueViolation, AssistantConfigurationNames.ProcessingTurn,
                () => AppendAsync(db, conversation.Id, Guid.CreateVersion7()));
        }

        await AssertMessagesAsync(conversation.Id, expectedCount: 1);
    }

    // Lazy recovery: the expired turn is failed and the next message accepted in one SaveChanges.
    [Fact]
    public async Task AnExpiredTurn_CanBeInterrupted_AndTheNextMessageAccepted_InOneSave()
    {
        var conversation = await NewConversationAsync();
        Guid first;
        await using (var scope = fixture.CreateScope())
        {
            first = await AppendAsync(Db(scope), conversation.Id, Guid.CreateVersion7());
        }

        await using (var scope = fixture.CreateScope())
        {
            var db = Db(scope);
            var current = await db.AssistantConversations.SingleAsync(c => c.Id == conversation.Id);
            var expired = await db.AssistantMessages.SingleAsync(m => m.Id == first);

            Assert.True(expired.InterruptIfLeaseExpired(Lease));
            db.Add(current.AppendUserMessage(Guid.CreateVersion7(), "Riprovo", Lease, Lease.AddSeconds(90)));
            await db.SaveChangesAsync();
        }

        await using (var scope = fixture.CreateScope())
        {
            var stored = await Db(scope).AssistantMessages.AsNoTracking()
                .Where(m => m.ConversationId == conversation.Id).OrderBy(m => m.Sequence).ToListAsync();

            Assert.Equal([(1, AssistantTurnStatus.Failed, AssistantMessage.Interrupted), (2, AssistantTurnStatus.Processing, null)],
                stored.Select(m => (m.Sequence, m.TurnStatus!.Value, m.FailureCode)));
        }
    }

    // ---- Concurrency: turn fencing ----

    // A process whose attempt was taken over while it waited (lease expired, recovery retried the turn)
    // cannot complete it: the turn_attempts/turn_status tokens reject its write, and its reply is not stored.
    [Fact]
    public async Task ATakenOverAttempt_CannotCompleteTheTurn()
    {
        var conversation = await NewConversationAsync();
        Guid messageId;
        await using (var scope = fixture.CreateScope())
        {
            messageId = await AppendAsync(Db(scope), conversation.Id, Guid.CreateVersion7());
        }

        // The slow process read the turn while it still held attempt 1.
        await using var slowScope = fixture.CreateScope();
        var slowDb = Db(slowScope);
        var slowConversation = await slowDb.AssistantConversations.SingleAsync(c => c.Id == conversation.Id);
        var slowMessage = await slowDb.AssistantMessages.SingleAsync(m => m.Id == messageId);

        // Recovery: the lease expired, the turn is interrupted and retried as attempt 2.
        await using (var scope = fixture.CreateScope())
        {
            var db = Db(scope);
            var message = await db.AssistantMessages.SingleAsync(m => m.Id == messageId);
            Assert.True(message.InterruptIfLeaseExpired(Lease));
            Assert.Equal(2, message.RetryTurn(AssistantTurnRetryMode.Automatic, Lease, Lease.AddSeconds(90)));
            await db.SaveChangesAsync();
        }

        // By its own (skewed) clock the slow process is still inside its lease, so the domain check passes;
        // the database tokens are what reject it.
        slowDb.Add(slowConversation.CompleteTurn(slowMessage, 1, "Risposta tardiva", Lease.AddSeconds(-1)));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => slowDb.SaveChangesAsync());

        await AssertMessagesAsync(conversation.Id, expectedCount: 1);

        // The current attempt completes normally.
        await using (var scope = fixture.CreateScope())
        {
            var db = Db(scope);
            var current = await db.AssistantConversations.SingleAsync(c => c.Id == conversation.Id);
            var message = await db.AssistantMessages.SingleAsync(m => m.Id == messageId);
            Assert.Equal((AssistantTurnStatus.Processing, 2), (message.TurnStatus, message.TurnAttempts));

            db.Add(current.CompleteTurn(message, 2, "Risposta", Lease.AddSeconds(2)));
            await db.SaveChangesAsync();
        }

        await AssertMessagesAsync(conversation.Id, expectedCount: 2);
    }

    [Fact]
    public async Task ASecondReplyToTheSameUserMessage_IsRejected()
    {
        var (conversation, user, _) = await NewCompletedTurnAsync();

        await PostgresAssert.ViolatesAsync(PostgresAssert.UniqueViolation, AssistantConfigurationNames.ReplyIndex,
            () => Execute(
                $"""
                INSERT INTO assistant_messages (id, conversation_id, user_id, sequence, role, content, reply_to_message_id, created_at_utc)
                VALUES ('{Guid.CreateVersion7()}', '{conversation.Id}', '{user.UserId}', 3, 'Assistant', 'Di nuovo', '{user.Id}', now())
                """));
    }

    // ---- Deletion ----

    [Fact]
    public async Task DeletingAConversation_DeletesOnlyItsMessages()
    {
        var owner = await NewUserAsync();
        var (deleted, _, _) = await NewCompletedTurnAsync(owner);
        var (kept, _, _) = await NewCompletedTurnAsync(owner);

        await using var scope = fixture.CreateScope();
        var db = Db(scope);
        Assert.Equal(1, await db.AssistantConversations.Where(c => c.Id == deleted.Id && c.UserId == owner.Id).ExecuteDeleteAsync());

        Assert.False(await db.AssistantMessages.AnyAsync(m => m.ConversationId == deleted.Id));
        Assert.Equal(2, await db.AssistantMessages.CountAsync(m => m.ConversationId == kept.Id));
    }

    [Fact]
    public async Task DeletingAUser_DeletesTheirConversationsAndMessages()
    {
        var owner = await NewUserAsync();
        var (conversation, _, _) = await NewCompletedTurnAsync(owner);

        await using var scope = fixture.CreateScope();
        var db = Db(scope);
        Assert.Equal(1, await db.Users.Where(u => u.Id == owner.Id).ExecuteDeleteAsync());

        Assert.False(await db.AssistantConversations.AnyAsync(c => c.Id == conversation.Id));
        Assert.False(await db.AssistantMessages.AnyAsync(m => m.ConversationId == conversation.Id));
    }

    // ---- Helpers ----

    // One simulated "send message" request: read the conversation, accept the message, save.
    private static async Task<Guid> AppendAsync(LifeOSDbContext db, Guid conversationId, Guid clientMessageId)
    {
        var conversation = await db.AssistantConversations.SingleAsync(c => c.Id == conversationId);
        var message = conversation.AppendUserMessage(clientMessageId, "Ciao", Now, Lease);
        db.Add(message);
        await db.SaveChangesAsync();

        return message.Id;
    }

    // Runs `count` requests at once, each in its own scope; null marks a success.
    private async Task<Exception?[]> RaceAsync(int count, Func<LifeOSDbContext, Task> request)
    {
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, count).Select(_ => Task.Run(async () =>
        {
            await using var scope = fixture.CreateScope();
            start.Wait();
            try
            {
                await request(Db(scope));
                return (Exception?)null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        })).ToList();

        start.Set();

        return await Task.WhenAll(tasks);
    }

    // The stored messages have consecutive sequences 1..n and the conversation's next sequence is n + 1.
    private async Task AssertMessagesAsync(Guid conversationId, int expectedCount)
    {
        await using var scope = fixture.CreateScope();
        var db = Db(scope);
        var sequences = await db.AssistantMessages.Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.Sequence).Select(m => m.Sequence).ToListAsync();
        var conversation = await db.AssistantConversations.AsNoTracking().SingleAsync(c => c.Id == conversationId);

        Assert.Equal(Enumerable.Range(1, expectedCount), sequences);
        Assert.Equal(expectedCount + 1, conversation.NextSequence);
    }

    private async Task<AssistantConversation> NewConversationAsync()
    {
        var owner = await NewUserAsync();
        var conversation = AssistantConversation.Create(owner.Id, Guid.CreateVersion7(), Now);
        await AddAsync(conversation);

        return conversation;
    }

    private async Task<(AssistantConversation Conversation, AssistantMessage User, AssistantMessage Reply)> NewCompletedTurnAsync(User? owner = null)
    {
        owner ??= await NewUserAsync();
        var conversation = AssistantConversation.Create(owner.Id, Guid.CreateVersion7(), Now);
        var user = conversation.AppendUserMessage(Guid.CreateVersion7(), "Ciao", Now, Lease);
        var reply = conversation.CompleteTurn(user, 1, "L'assistente non è ancora attivo.", Now.AddSeconds(1));
        await AddAsync(conversation, user, reply);

        return (conversation, user, reply);
    }

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now.AddDays(-30));
        await PostgresAssert.InsertAsync(fixture, user);

        return user;
    }

    private Task AddAsync(params object[] entities) => PostgresAssert.InsertAsync(fixture, entities);

    private async Task Execute(string sql)
    {
        await using var scope = fixture.CreateScope();
        await Db(scope).Database.ExecuteSqlRawAsync(sql);
    }

    private static LifeOSDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();

    private static Task<List<string>> Tables(DatabaseFacade database) =>
        Strings(database, "SELECT tablename::text AS \"Value\" FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename COLLATE \"C\"");

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

    // The constraint names of AssistantConfiguration (internal to Infrastructure).
    private static class AssistantConfigurationNames
    {
        public const string MessageConversation = "FK_assistant_messages_conversations";
        public const string Reply = "FK_assistant_messages_reply_to";
        public const string ClientConversation = "ux_assistant_conversations_user_client";
        public const string Sequence = "ux_assistant_messages_conversation_sequence";
        public const string ClientMessage = "ux_assistant_messages_conversation_client";
        public const string ProcessingTurn = "ux_assistant_messages_conversation_processing";
        public const string ReplyIndex = "ux_assistant_messages_conversation_reply";
    }
}
