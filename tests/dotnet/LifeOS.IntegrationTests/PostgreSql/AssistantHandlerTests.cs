using LifeOS.Application.Assistant;
using LifeOS.Domain.Assistant;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// AI-007 F1.2-A: the assistant use cases on the real repository and PostgreSQL — idempotency, user
// isolation, concurrency (races made deterministic with a rendezvous before the save), lease fencing,
// lazy recovery, retries and deletion. Each scope is one simulated request; the clock is manual.
//
// The database is shared by the whole PostgreSQL collection, so every assertion is scoped to this
// test's own users and conversations.
[Collection(PostgreSqlCollection.Name)]
public class AssistantHandlerTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan LeaseDuration = AssistantTurnPolicy.LeaseDuration;

    private readonly ManualTimeProvider _clock = new(Now);

    // ---- Conversations ----

    [Fact]
    public async Task CreateConversation_IsIdempotentPerUser_AlsoUnderConcurrency()
    {
        var owner = await NewUserAsync();
        var other = await NewUserAsync();
        var clientId = Guid.CreateVersion7();
        var rendezvous = new Rendezvous(4);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await using var scope = fixture.CreateScope();
            var repository = new PausingAssistantRepository(Repository(scope), rendezvous.ArriveAndWaitAsync, pauseBeforeAdd: true);
            return await new CreateAssistantConversationHandler(repository, _clock).HandleAsync(owner.Id, clientId, default);
        })));

        Assert.All(results, result => Assert.Equal(AssistantResultStatus.Ok, result.Status));
        Assert.Single(results, result => result.Value!.Created);
        Assert.Single(results.Select(result => result.Value!.Conversation.Id).Distinct());

        await using (var scope = fixture.CreateScope())
        {
            var replay = await new CreateAssistantConversationHandler(Repository(scope), _clock).HandleAsync(owner.Id, clientId, default);
            Assert.Equal((false, results[0].Value!.Conversation.Id), (replay.Value!.Created, replay.Value.Conversation.Id));

            // The same client id of another user is another conversation.
            var foreign = await new CreateAssistantConversationHandler(Repository(scope), _clock).HandleAsync(other.Id, clientId, default);
            Assert.True(foreign.Value!.Created);
            Assert.NotEqual(replay.Value.Conversation.Id, foreign.Value.Conversation.Id);

            var invalid = await new CreateAssistantConversationHandler(Repository(scope), _clock).HandleAsync(owner.Id, Guid.Empty, default);
            Assert.Equal((AssistantResultStatus.Invalid, "clientConversationId"), (invalid.Status, invalid.Field));
        }

        Assert.Equal(1, await CountAsync(db => db.AssistantConversations.CountAsync(c => c.UserId == owner.Id)));
    }

    [Fact]
    public async Task ListConversations_MostRecentFirst_WithAStableCursor()
    {
        var owner = await NewUserAsync();
        var first = await NewConversationAsync(owner);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var second = await NewConversationAsync(owner);
        var third = await NewConversationAsync(owner); // same instant as second: ordered by id
        _clock.Advance(TimeSpan.FromMinutes(1));
        await SendAsync(owner, first, "Ciao"); // first becomes the most recently active
        await NewConversationAsync(await NewUserAsync()); // another user's

        await using var scope = fixture.CreateScope();
        var handler = new ListAssistantConversationsHandler(Repository(scope));

        var all = (await handler.HandleAsync(owner.Id, null, null, default)).Value!;
        var page1 = (await handler.HandleAsync(owner.Id, null, 2, default)).Value!;
        var last = page1[^1];
        var page2 = (await handler.HandleAsync(owner.Id, new AssistantConversationCursor(last.UpdatedAtUtc, last.Id), 2, default)).Value!;

        // The two conversations of the same instant are ordered by id (in PostgreSQL's uuid order); the
        // pages, joined, are exactly the unpaged list.
        Assert.Equal(first, all[0].Id);
        Assert.Equal([first, second, third], all.Select(c => c.Id).Order());
        Assert.Equal(all.Select(c => c.Id), [.. page1.Select(c => c.Id), .. page2.Select(c => c.Id)]);
        Assert.Equal(AssistantResultStatus.Invalid, (await handler.HandleAsync(owner.Id, null, AssistantPaging.MaxConversations + 1, default)).Status);
        Assert.Equal(AssistantResultStatus.Invalid, (await handler.HandleAsync(owner.Id, null, 0, default)).Status);
    }

    // ---- User isolation ----

    [Fact]
    public async Task AnotherUsersConversationAndMessages_AreIndistinguishableFromMissingOnes()
    {
        var owner = await NewUserAsync();
        var intruder = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);
        var sent = await SendAsync(owner, conversation, "Speso 5€ per colazione");
        var message = sent.Value!.UserMessage;

        await using (var scope = fixture.CreateScope())
        {
            var repository = Repository(scope);
            var missing = Guid.CreateVersion7();

            foreach (var id in new[] { conversation, missing })
            {
                Assert.Equal(AssistantResultStatus.NotFound,
                    (await new GetAssistantConversationMessagesHandler(repository, _clock).HandleAsync(intruder.Id, id, null, null, default)).Status);
                Assert.Equal(AssistantResultStatus.NotFound, (await new SendAssistantMessageHandler(repository, _clock)
                    .HandleAsync(intruder.Id, id, new SendAssistantMessageCommand(Guid.CreateVersion7(), "Ciao"), default)).Status);
                Assert.Equal(AssistantResultStatus.NotFound,
                    (await new RetryAssistantTurnHandler(repository, _clock).HandleAsync(intruder.Id, id == conversation ? message.Id : id, default)).Status);
                Assert.Equal(AssistantResultStatus.NotFound, (await new CompleteAssistantTurnHandler(repository, _clock)
                    .HandleAsync(intruder.Id, id, new CompleteAssistantTurnCommand(message.Id, 1, "Risposta"), default)).Status);
                Assert.Equal(AssistantResultStatus.NotFound, (await new FailAssistantTurnHandler(repository, _clock)
                    .HandleAsync(intruder.Id, id, new FailAssistantTurnCommand(message.Id, 1, "ai_unavailable"), default)).Status);
                Assert.Equal(AssistantResultStatus.NotFound,
                    await new DeleteAssistantConversationHandler(repository).HandleAsync(intruder.Id, id, default));
            }

            Assert.Empty((await new ListAssistantConversationsHandler(repository).HandleAsync(intruder.Id, null, null, default)).Value!);
        }

        // Nothing of the owner's changed.
        var stored = await MessagesAsync(conversation);
        Assert.Equal([(1, AssistantTurnStatus.Processing, 1)], stored.Select(m => (m.Sequence, m.TurnStatus!.Value, m.TurnAttempts!.Value)));
    }

    // ---- Send: idempotency ----

    [Fact]
    public async Task Send_AcceptsTheMessage_AsAProcessingTurn_WithoutAnyAnswer()
    {
        var owner = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);

        var result = await SendAsync(owner, conversation, "  Speso 5€ per colazione con Pamela  ");

        Assert.Equal(AssistantResultStatus.Ok, result.Status);
        var turn = result.Value!;
        Assert.False(turn.Replayed);
        Assert.Null(turn.Reply);
        Assert.Equal(("Speso 5€ per colazione con Pamela", AssistantTurnStatus.Processing, 1, Now + LeaseDuration),
            (turn.UserMessage.Content, turn.UserMessage.TurnStatus!.Value, turn.UserMessage.TurnAttempts!.Value, turn.UserMessage.TurnLeaseUntilUtc!.Value));
        Assert.Single(await MessagesAsync(conversation));
    }

    [Fact]
    public async Task Send_WithTheSameClientMessageId_NeverStoresASecondMessage()
    {
        var owner = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);
        var clientId = Guid.CreateVersion7();

        var first = await SendAsync(owner, conversation, "Ciao", clientId);
        var sameText = await SendAsync(owner, conversation, " Ciao ", clientId);
        var otherText = await SendAsync(owner, conversation, "Ciao!", clientId);

        Assert.Equal((AssistantResultStatus.InProgress, AssistantErrors.TurnInProgress, true),
            (sameText.Status, sameText.Code, sameText.Value!.Replayed));
        Assert.Equal(first.Value!.UserMessage.Id, sameText.Value.UserMessage.Id);
        Assert.Equal((AssistantResultStatus.Conflict, AssistantErrors.ClientMessageIdReused), (otherText.Status, otherText.Code));
        Assert.Single(await MessagesAsync(conversation));
    }

    [Fact]
    public async Task Send_ReplayAfterCompletion_ReturnsTheStoredReply()
    {
        var owner = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);
        var clientId = Guid.CreateVersion7();
        var message = (await SendAsync(owner, conversation, "Ciao", clientId)).Value!.UserMessage;
        var completed = await CompleteAsync(owner, conversation, message.Id, 1, "Risposta");

        var replay = await SendAsync(owner, conversation, "Ciao", clientId);

        Assert.Equal((AssistantResultStatus.Ok, true, AssistantTurnStatus.Completed),
            (replay.Status, replay.Value!.Replayed, replay.Value.UserMessage.TurnStatus!.Value));
        Assert.Equal((completed.Value!.Reply!.Id, "Risposta"), (replay.Value.Reply!.Id, replay.Value.Reply.Content));
        Assert.Equal(2, (await MessagesAsync(conversation)).Count);
    }

    [Fact]
    public async Task Send_RejectsInvalidInput_AndStoresNothing()
    {
        var owner = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);

        var tooLong = await SendAsync(owner, conversation, new string('a', AssistantMessage.MaxUserContentLength + 1));
        var blank = await SendAsync(owner, conversation, "   ");
        var noId = await SendAsync(owner, conversation, "Ciao", Guid.Empty);

        Assert.Equal((AssistantResultStatus.Invalid, "text"), (tooLong.Status, tooLong.Field));
        Assert.Equal((AssistantResultStatus.Invalid, "text"), (blank.Status, blank.Field));
        Assert.Equal((AssistantResultStatus.Invalid, "clientMessageId"), (noId.Status, noId.Field));
        Assert.Empty(await MessagesAsync(conversation));
        Assert.Equal(AssistantConversation.FirstSequence, await CountAsync(db => db.AssistantConversations.Where(c => c.Id == conversation)
            .Select(c => c.NextSequence).SingleAsync()));
    }

    // ---- Send: concurrency ----

    // Requests that all saw "no such message yet" save at the same moment: one stores the message, the
    // others lose the race, read again and get the stored turn.
    [Fact]
    public async Task ConcurrentSends_WithTheSameClientMessageId_StoreOneMessage()
    {
        var owner = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);
        var clientId = Guid.CreateVersion7();

        var results = await RaceAsync(4, repository => new SendAssistantMessageHandler(repository, _clock)
            .HandleAsync(owner.Id, conversation, new SendAssistantMessageCommand(clientId, "Ciao"), default));

        Assert.Single(results, result => result.Status == AssistantResultStatus.Ok);
        Assert.All(results.Where(result => result.Status != AssistantResultStatus.Ok), result =>
            Assert.Equal((AssistantResultStatus.InProgress, true), (result.Status, result.Value!.Replayed)));
        Assert.Single(results.Select(result => result.Value!.UserMessage.Id).Distinct());
        Assert.Single(await MessagesAsync(conversation));
    }

    // Two different messages at once: one turn is accepted, the other request is told a turn is in progress.
    [Fact]
    public async Task ConcurrentSends_OfDifferentMessages_AcceptExactlyOneTurn()
    {
        var owner = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);

        var results = await RaceAsync(2, repository => new SendAssistantMessageHandler(repository, _clock)
            .HandleAsync(owner.Id, conversation, new SendAssistantMessageCommand(Guid.CreateVersion7(), "Ciao"), default));

        Assert.Single(results, result => result.Status == AssistantResultStatus.Ok);
        var inProgress = Assert.Single(results, result => result.Status == AssistantResultStatus.InProgress);
        var accepted = results.Single(result => result.Status == AssistantResultStatus.Ok).Value!.UserMessage;
        Assert.Equal(accepted.Id, inProgress.Value!.UserMessage.Id);

        var stored = Assert.Single(await MessagesAsync(conversation));
        Assert.Equal((1, accepted.Id), (stored.Sequence, stored.Id));
    }

    // A request whose writes keep losing gives up with Busy (temporary), and nothing partial is stored.
    [Fact]
    public async Task ASendThatKeepsLosing_IsBusy_AndStoresNothing()
    {
        var owner = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);

        await using var scope = fixture.CreateScope();
        var repository = new PausingAssistantRepository(Repository(scope), () => Execute(
            $"UPDATE assistant_conversations SET next_sequence = next_sequence + 1 WHERE id = '{conversation}'"), pauseEverySave: true);

        var result = await new SendAssistantMessageHandler(repository, _clock)
            .HandleAsync(owner.Id, conversation, new SendAssistantMessageCommand(Guid.CreateVersion7(), "Ciao"), default);

        Assert.Equal((AssistantResultStatus.Busy, AssistantErrors.Busy), (result.Status, result.Code));
        Assert.Equal(AssistantTurnPolicy.MaxWriteAttempts, repository.Saves);
        Assert.Empty(await MessagesAsync(conversation));
    }

    // ---- Lease and fencing ----

    [Fact]
    public async Task Complete_StoresTheReply_UntilTheLeaseEnds_ButNotAtIt()
    {
        var owner = await NewUserAsync();
        var lateConversation = await NewConversationAsync(owner);
        var late = (await SendAsync(owner, lateConversation, "Ciao")).Value!.UserMessage;
        var onTimeConversation = await NewConversationAsync(owner);
        var onTime = (await SendAsync(owner, onTimeConversation, "Ciao")).Value!.UserMessage;

        _clock.Advance(LeaseDuration);
        var atExpiry = await CompleteAsync(owner, lateConversation, late.Id, 1, "Risposta tardiva");
        var failAtExpiry = await FailAsync(owner, lateConversation, late.Id, 1, "ai_unavailable");

        Assert.Equal((AssistantResultStatus.Conflict, AssistantErrors.TurnNotHeld), (atExpiry.Status, atExpiry.Code));
        Assert.Equal((AssistantResultStatus.Conflict, AssistantErrors.TurnNotHeld), (failAtExpiry.Status, failAtExpiry.Code));
        var lateStored = Assert.Single(await MessagesAsync(lateConversation));
        Assert.Equal((AssistantTurnStatus.Processing, null), (lateStored.TurnStatus!.Value, lateStored.FailureCode));

        _clock.UtcNow = Now + LeaseDuration - TimeSpan.FromMicroseconds(1);
        var inTime = await CompleteAsync(owner, onTimeConversation, onTime.Id, 1, "Risposta");
        Assert.Equal(AssistantResultStatus.Ok, inTime.Status);
        Assert.Equal([AssistantMessageRole.User, AssistantMessageRole.Assistant], (await MessagesAsync(onTimeConversation)).Select(m => m.Role));
    }

    // Recovery takes an expired turn over; the old attempt's completion and failure are rejected, the
    // new attempt completes, and repeating that completion returns the stored reply.
    [Fact]
    public async Task AStaleFencingToken_CannotCloseTheTurn_AfterARetry()
    {
        var owner = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);
        var message = (await SendAsync(owner, conversation, "Ciao")).Value!.UserMessage;

        _clock.Advance(LeaseDuration);
        var retried = await RetryAsync(owner, message.Id);
        Assert.Equal((AssistantResultStatus.Ok, AssistantTurnStatus.Processing, 2),
            (retried.Status, retried.Value!.UserMessage.TurnStatus!.Value, retried.Value.UserMessage.TurnAttempts!.Value));

        var stale = await CompleteAsync(owner, conversation, message.Id, 1, "Risposta tardiva");
        var staleFail = await FailAsync(owner, conversation, message.Id, 1, "ai_unavailable");
        Assert.Equal(AssistantErrors.TurnNotHeld, stale.Code);
        Assert.Equal(AssistantErrors.TurnNotHeld, staleFail.Code);

        var current = await CompleteAsync(owner, conversation, message.Id, 2, "Risposta");
        var repeated = await CompleteAsync(owner, conversation, message.Id, 2, "Risposta");
        Assert.Equal((AssistantResultStatus.Ok, false), (current.Status, current.Value!.Replayed));
        Assert.Equal((AssistantResultStatus.Ok, true, current.Value.Reply!.Id), (repeated.Status, repeated.Value!.Replayed, repeated.Value.Reply!.Id));

        var stored = await MessagesAsync(conversation);
        Assert.Equal([(1, AssistantMessageRole.User), (2, AssistantMessageRole.Assistant)], stored.Select(m => (m.Sequence, m.Role)));
        Assert.Equal((AssistantTurnStatus.Completed, 2), (stored[0].TurnStatus!.Value, stored[0].TurnAttempts!.Value));
        Assert.Equal(message.Id, stored[1].ReplyToMessageId);
    }

    [Fact]
    public async Task Complete_RejectsAnInvalidReply_AndKeepsTheTurn()
    {
        var owner = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);
        var message = (await SendAsync(owner, conversation, "Ciao")).Value!.UserMessage;

        var result = await CompleteAsync(owner, conversation, message.Id, 1, new string('a', AssistantMessage.MaxAssistantContentLength + 1));

        Assert.Equal((AssistantResultStatus.Invalid, "reply"), (result.Status, result.Field));
        var stored = Assert.Single(await MessagesAsync(conversation));
        Assert.Equal((AssistantTurnStatus.Processing, 1), (stored.TurnStatus!.Value, stored.TurnAttempts!.Value));
    }

    // Fail stores only the stable code on the turn: no assistant message.
    [Fact]
    public async Task Fail_StoresTheCode_WithoutAnAssistantMessage_AndIsIdempotent()
    {
        var owner = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);
        var message = (await SendAsync(owner, conversation, "Ciao")).Value!.UserMessage;

        var failed = await FailAsync(owner, conversation, message.Id, 1, "ai_unavailable");
        var repeated = await FailAsync(owner, conversation, message.Id, 1, "ai_unavailable");

        Assert.Equal((AssistantResultStatus.Ok, false), (failed.Status, failed.Value!.Replayed));
        Assert.Equal((AssistantResultStatus.Ok, true), (repeated.Status, repeated.Value!.Replayed));
        var stored = Assert.Single(await MessagesAsync(conversation));
        Assert.Equal((AssistantTurnStatus.Failed, "ai_unavailable"), (stored.TurnStatus!.Value, stored.FailureCode));

        // Only stable codes; the turn stays as it was.
        var otherConversation = await NewConversationAsync(owner);
        var other = (await SendAsync(owner, otherConversation, "Ciao")).Value!.UserMessage;
        var invalid = await FailAsync(owner, otherConversation, other.Id, 1, "Timeout: provider error");
        Assert.Equal((AssistantResultStatus.Invalid, "failureCode"), (invalid.Status, invalid.Field));
        Assert.Equal(AssistantTurnStatus.Processing, Assert.Single(await MessagesAsync(otherConversation)).TurnStatus);
    }

    // ---- Recovery ----

    [Fact]
    public async Task ReadingTheConversation_InterruptsAnExpiredTurn()
    {
        var owner = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);
        await SendAsync(owner, conversation, "Ciao");

        var live = await GetMessagesAsync(owner, conversation);
        Assert.Equal(AssistantTurnStatus.Processing, Assert.Single(live.Value!.Messages).TurnStatus);

        _clock.Advance(LeaseDuration);
        var recovered = await GetMessagesAsync(owner, conversation);

        var message = Assert.Single(recovered.Value!.Messages);
        Assert.Equal((AssistantTurnStatus.Failed, AssistantMessage.Interrupted, 1),
            (message.TurnStatus!.Value, message.FailureCode, message.TurnAttempts!.Value));
        Assert.Equal(AssistantMessage.Interrupted, (await MessagesAsync(conversation)).Single().FailureCode);
    }

    [Fact]
    public async Task ANewMessage_AfterAnExpiredTurn_IsAccepted_AndTheOldTurnInterrupted()
    {
        var owner = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);
        await SendAsync(owner, conversation, "Primo");

        Assert.Equal(AssistantResultStatus.InProgress, (await SendAsync(owner, conversation, "Secondo")).Status);

        _clock.Advance(LeaseDuration);
        var accepted = await SendAsync(owner, conversation, "Secondo");

        Assert.Equal(AssistantResultStatus.Ok, accepted.Status);
        Assert.Equal([(1, AssistantTurnStatus.Failed, AssistantMessage.Interrupted), (2, AssistantTurnStatus.Processing, null)],
            (await MessagesAsync(conversation)).Select(m => (m.Sequence, m.TurnStatus!.Value, m.FailureCode)));
    }

    // Repeating the send of an interrupted message (e.g. after a client timeout) retries it automatically,
    // with the same message and client id, until the automatic attempts are used up; then it stays Failed,
    // and only the user's explicit retry starts another attempt — which is never refused for the count.
    [Fact]
    public async Task ResendingAFailedMessage_RetriesAutomaticallyUpToTheLimit_ThenOnlyTheUserCanRetry()
    {
        var owner = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);
        var clientId = Guid.CreateVersion7();
        var messageId = (await SendAsync(owner, conversation, "Ciao", clientId)).Value!.UserMessage.Id;

        for (var expected = 2; expected <= AssistantMessage.MaxAutomaticAttempts; expected++)
        {
            _clock.Advance(LeaseDuration);
            var resent = await SendAsync(owner, conversation, "Ciao", clientId);
            Assert.Equal((AssistantResultStatus.Ok, true, AssistantTurnStatus.Processing, expected),
                (resent.Status, resent.Value!.Replayed, resent.Value.UserMessage.TurnStatus!.Value, resent.Value.UserMessage.TurnAttempts!.Value));
        }

        _clock.Advance(LeaseDuration);
        var exhausted = await SendAsync(owner, conversation, "Ciao", clientId);
        Assert.Equal((AssistantResultStatus.Ok, AssistantTurnStatus.Failed, AssistantMessage.Interrupted, AssistantMessage.MaxAutomaticAttempts),
            (exhausted.Status, exhausted.Value!.UserMessage.TurnStatus!.Value, exhausted.Value.UserMessage.FailureCode,
                exhausted.Value.UserMessage.TurnAttempts!.Value));

        var manual = await RetryAsync(owner, messageId);
        Assert.Equal((AssistantResultStatus.Ok, AssistantTurnStatus.Processing, AssistantMessage.MaxAutomaticAttempts + 1),
            (manual.Status, manual.Value!.UserMessage.TurnStatus!.Value, manual.Value.UserMessage.TurnAttempts!.Value));

        var again = await RetryAsync(owner, messageId);
        Assert.Equal(AssistantResultStatus.InProgress, again.Status);

        var stored = Assert.Single(await MessagesAsync(conversation));
        Assert.Equal((messageId, clientId), (stored.Id, stored.ClientMessageId!.Value));
    }

    [Fact]
    public async Task Retry_OfAnOlderFailedTurn_IsSuperseded_AndOfACompletedOne_ReturnsTheReply()
    {
        var owner = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);
        var first = (await SendAsync(owner, conversation, "Primo")).Value!.UserMessage;
        await FailAsync(owner, conversation, first.Id, 1, "ai_unavailable");
        var second = (await SendAsync(owner, conversation, "Secondo")).Value!.UserMessage;
        await CompleteAsync(owner, conversation, second.Id, 1, "Risposta");

        var superseded = await RetryAsync(owner, first.Id);
        var completed = await RetryAsync(owner, second.Id);

        Assert.Equal((AssistantResultStatus.Conflict, AssistantErrors.TurnSuperseded), (superseded.Status, superseded.Code));
        Assert.Equal((AssistantResultStatus.Ok, true, "Risposta"), (completed.Status, completed.Value!.Replayed, completed.Value.Reply!.Content));
        Assert.Equal(3, (await MessagesAsync(conversation)).Count);
    }

    // A retry racing a new message: the retry checks that no later message exists, a new message is then
    // accepted and closed before the retry saves, and the retry's save (on the older message only) still
    // wins. The older turn's reply would land after the later message, so completing it is refused and
    // nothing is stored; the attempt can still be failed.
    [Fact]
    public async Task ARetryRacingANewMessage_CannotStoreAReplyOutOfOrder()
    {
        var owner = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);
        var older = (await ProcessorSendAsync(owner, conversation, "Primo", Guid.CreateVersion7())).Value!.UserMessage;

        await using var scope = fixture.CreateScope();
        var retried = await new RetryAssistantTurnHandler(new PausingAssistantRepository(Repository(scope), async () =>
                Assert.Equal(AssistantResultStatus.Ok, (await ProcessorSendAsync(owner, conversation, "Secondo", Guid.CreateVersion7())).Status)),
            _clock).HandleAsync(owner.Id, older.Id, default);
        Assert.Equal((AssistantResultStatus.Ok, AssistantTurnStatus.Processing, 2),
            (retried.Status, retried.Value!.UserMessage.TurnStatus!.Value, retried.Value.UserMessage.TurnAttempts!.Value));

        var completed = await CompleteAsync(owner, conversation, older.Id, 2, "Risposta fuori ordine");

        Assert.Equal((AssistantResultStatus.Conflict, AssistantErrors.TurnSuperseded), (completed.Status, completed.Code));
        Assert.Equal([(1, AssistantMessageRole.User), (2, AssistantMessageRole.User)],
            (await MessagesAsync(conversation)).Select(m => (m.Sequence, m.Role)));
        Assert.Equal(AssistantResultStatus.Ok, (await FailAsync(owner, conversation, older.Id, 2, "ai_unavailable")).Status);
    }

    // ---- Deletion ----

    [Fact]
    public async Task Delete_RemovesTheConversationAndItsMessages_Only()
    {
        var owner = await NewUserAsync();
        var deleted = await NewConversationAsync(owner);
        var message = (await SendAsync(owner, deleted, "Ciao")).Value!.UserMessage;
        await CompleteAsync(owner, deleted, message.Id, 1, "Risposta");
        var kept = await NewConversationAsync(owner);
        await SendAsync(owner, kept, "Ciao");

        await using (var scope = fixture.CreateScope())
        {
            var handler = new DeleteAssistantConversationHandler(Repository(scope));
            Assert.Equal(AssistantResultStatus.Ok, await handler.HandleAsync(owner.Id, deleted, default));
            Assert.Equal(AssistantResultStatus.NotFound, await handler.HandleAsync(owner.Id, deleted, default));
        }

        Assert.Empty(await MessagesAsync(deleted));
        Assert.Single(await MessagesAsync(kept));
        Assert.Equal(AssistantResultStatus.NotFound, (await GetMessagesAsync(owner, deleted)).Status);
        Assert.Equal(AssistantResultStatus.NotFound, (await SendAsync(owner, deleted, "Ancora")).Status);
    }

    // A processor that completes after the user deleted the conversation stores nothing.
    [Fact]
    public async Task ACompletion_RacingADelete_StoresNothing()
    {
        var owner = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);
        var message = (await SendAsync(owner, conversation, "Ciao")).Value!.UserMessage;

        await using var scope = fixture.CreateScope();
        var repository = new PausingAssistantRepository(Repository(scope), async () =>
        {
            await using var other = fixture.CreateScope();
            await new DeleteAssistantConversationHandler(Repository(other)).HandleAsync(owner.Id, conversation, default);
        });

        var result = await new CompleteAssistantTurnHandler(repository, _clock)
            .HandleAsync(owner.Id, conversation, new CompleteAssistantTurnCommand(message.Id, 1, "Risposta"), default);

        Assert.Equal(AssistantResultStatus.NotFound, result.Status);
        Assert.Empty(await MessagesAsync(conversation));
    }

    // ---- F1 processor ----

    // F1 has no AI: the accepted attempt fails at once with assistant_unavailable. The message is stored,
    // no assistant message is, and nothing stays Processing.
    [Fact]
    public async Task Processor_StoresTheMessage_AndFailsItsAttempt_WithAssistantUnavailable()
    {
        var owner = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);
        var clientId = Guid.CreateVersion7();

        var sent = await ProcessorSendAsync(owner, conversation, "Speso 5€", clientId);

        Assert.Equal((AssistantResultStatus.Ok, false), (sent.Status, sent.Value!.Replayed));
        Assert.Null(sent.Value.Reply);
        Assert.Equal((AssistantTurnStatus.Failed, AssistantErrors.AssistantUnavailable),
            (sent.Value.UserMessage.TurnStatus!.Value, sent.Value.UserMessage.FailureCode));
        var stored = Assert.Single(await MessagesAsync(conversation));
        Assert.Equal((sent.Value.UserMessage.Id, AssistantTurnStatus.Failed, 1, AssistantErrors.AssistantUnavailable, (DateTimeOffset?)null),
            (stored.Id, stored.TurnStatus!.Value, stored.TurnAttempts!.Value, stored.FailureCode, stored.TurnLeaseUntilUtc));

        // A replay restarts the failed turn automatically (attempt 2), which fails the same way; no second message.
        var replay = await ProcessorSendAsync(owner, conversation, "Speso 5€", clientId);
        Assert.Equal((AssistantResultStatus.Ok, true, 2, AssistantErrors.AssistantUnavailable),
            (replay.Status, replay.Value!.Replayed, replay.Value.UserMessage.TurnAttempts!.Value, replay.Value.UserMessage.FailureCode));

        // The user's retry: the next attempt of the same message, failed the same way.
        await using (var scope = fixture.CreateScope())
        {
            var retried = await Processor(Repository(scope)).RetryAsync(owner.Id, stored.Id, default);
            Assert.Equal((AssistantResultStatus.Ok, false, 3, AssistantTurnStatus.Failed),
                (retried.Status, retried.Value!.Replayed, retried.Value.UserMessage.TurnAttempts!.Value, retried.Value.UserMessage.TurnStatus!.Value));
        }

        // The next message is accepted at once: nothing is left in progress.
        Assert.Equal(AssistantResultStatus.Ok, (await ProcessorSendAsync(owner, conversation, "Altro", Guid.CreateVersion7())).Status);
        Assert.Equal([AssistantMessageRole.User, AssistantMessageRole.User], (await MessagesAsync(conversation)).Select(m => m.Role));
    }

    // A turn Processing under another request's lease is never closed by this request.
    [Fact]
    public async Task Processor_LeavesAnotherRequestsProcessingTurn_Alone()
    {
        var owner = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);
        var running = (await SendAsync(owner, conversation, "In corso")).Value!.UserMessage;

        var other = await ProcessorSendAsync(owner, conversation, "Altro", Guid.CreateVersion7());
        var sameId = await ProcessorSendAsync(owner, conversation, "In corso", running.ClientMessageId);

        Assert.Equal((AssistantResultStatus.InProgress, running.Id), (other.Status, other.Value!.UserMessage.Id));
        Assert.Equal((AssistantResultStatus.InProgress, true), (sameId.Status, sameId.Value!.Replayed));
        var stored = Assert.Single(await MessagesAsync(conversation));
        Assert.Equal((AssistantTurnStatus.Processing, 1), (stored.TurnStatus!.Value, stored.TurnAttempts!.Value));
    }

    // The conversation is deleted between accepting the message and closing its attempt: NotFound, and
    // nothing remains.
    [Fact]
    public async Task Processor_RacingADelete_IsNotFound_AndLeavesNothing()
    {
        var owner = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);
        var saves = 0;

        await using var scope = fixture.CreateScope();
        var repository = new PausingAssistantRepository(Repository(scope), async () =>
        {
            if (++saves == 2)
            {
                await using var other = fixture.CreateScope();
                await new DeleteAssistantConversationHandler(Repository(other)).HandleAsync(owner.Id, conversation, default);
            }
        }, pauseEverySave: true);

        var result = await Processor(repository).SendAsync(owner.Id, conversation, new SendAssistantMessageCommand(Guid.CreateVersion7(), "Ciao"), default);

        Assert.Equal(AssistantResultStatus.NotFound, result.Status);
        Assert.Empty(await MessagesAsync(conversation));
    }

    // The accepted attempt is taken over before it can be closed (here another writer moved the turn to
    // attempt 2): the stale attempt writes nothing and the processor answers Busy.
    [Fact]
    public async Task Processor_WhoseAttemptWasTakenOver_IsBusy_AndWritesNothing()
    {
        var owner = await NewUserAsync();
        var conversation = await NewConversationAsync(owner);
        var saves = 0;

        await using var scope = fixture.CreateScope();
        var repository = new PausingAssistantRepository(Repository(scope), async () =>
        {
            if (++saves == 2)
            {
                await Execute($"UPDATE assistant_messages SET turn_attempts = 2 WHERE conversation_id = '{conversation}'");
            }
        }, pauseEverySave: true);

        var result = await Processor(repository).SendAsync(owner.Id, conversation, new SendAssistantMessageCommand(Guid.CreateVersion7(), "Ciao"), default);

        Assert.Equal((AssistantResultStatus.Busy, AssistantErrors.Busy), (result.Status, result.Code));
        var stored = Assert.Single(await MessagesAsync(conversation));
        Assert.Equal((AssistantTurnStatus.Processing, 2, (string?)null), (stored.TurnStatus!.Value, stored.TurnAttempts!.Value, stored.FailureCode));
    }

    // ---- Helpers ----

    private AssistantTurnProcessor Processor(IAssistantConversationRepository repository) => new(repository, _clock);

    private async Task<AssistantResult<AssistantTurn>> ProcessorSendAsync(User owner, Guid conversationId, string text, Guid? clientMessageId)
    {
        await using var scope = fixture.CreateScope();
        return await Processor(Repository(scope)).SendAsync(owner.Id, conversationId,
            new SendAssistantMessageCommand(clientMessageId!.Value, text), default);
    }

    private async Task<AssistantResult<AssistantTurn>[]> RaceAsync(int count,
        Func<IAssistantConversationRepository, Task<AssistantResult<AssistantTurn>>> request)
    {
        var rendezvous = new Rendezvous(count);

        return await Task.WhenAll(Enumerable.Range(0, count).Select(_ => Task.Run(async () =>
        {
            await using var scope = fixture.CreateScope();
            return await request(new PausingAssistantRepository(Repository(scope), rendezvous.ArriveAndWaitAsync));
        })));
    }

    private async Task<Guid> NewConversationAsync(User owner)
    {
        await using var scope = fixture.CreateScope();
        var result = await new CreateAssistantConversationHandler(Repository(scope), _clock).HandleAsync(owner.Id, Guid.CreateVersion7(), default);

        return result.Value!.Conversation.Id;
    }

    private async Task<AssistantResult<AssistantTurn>> SendAsync(User owner, Guid conversationId, string text, Guid? clientMessageId = null)
    {
        await using var scope = fixture.CreateScope();
        return await new SendAssistantMessageHandler(Repository(scope), _clock).HandleAsync(owner.Id, conversationId,
            new SendAssistantMessageCommand(clientMessageId ?? Guid.CreateVersion7(), text), default);
    }

    private async Task<AssistantResult<AssistantTurn>> CompleteAsync(User owner, Guid conversationId, Guid messageId, int attempt, string reply)
    {
        await using var scope = fixture.CreateScope();
        return await new CompleteAssistantTurnHandler(Repository(scope), _clock).HandleAsync(owner.Id, conversationId,
            new CompleteAssistantTurnCommand(messageId, attempt, reply), default);
    }

    private async Task<AssistantResult<AssistantTurn>> FailAsync(User owner, Guid conversationId, Guid messageId, int attempt, string code)
    {
        await using var scope = fixture.CreateScope();
        return await new FailAssistantTurnHandler(Repository(scope), _clock).HandleAsync(owner.Id, conversationId,
            new FailAssistantTurnCommand(messageId, attempt, code), default);
    }

    private async Task<AssistantResult<AssistantTurn>> RetryAsync(User owner, Guid messageId)
    {
        await using var scope = fixture.CreateScope();
        return await new RetryAssistantTurnHandler(Repository(scope), _clock).HandleAsync(owner.Id, messageId, default);
    }

    private async Task<AssistantResult<AssistantMessagesPage>> GetMessagesAsync(User owner, Guid conversationId)
    {
        await using var scope = fixture.CreateScope();
        return await new GetAssistantConversationMessagesHandler(Repository(scope), _clock).HandleAsync(owner.Id, conversationId, null, null, default);
    }

    // What is stored, read directly (not through the handlers under test).
    private Task<List<AssistantMessage>> MessagesAsync(Guid conversationId) =>
        CountAsync(db => db.AssistantMessages.AsNoTracking().Where(m => m.ConversationId == conversationId).OrderBy(m => m.Sequence).ToListAsync());

    private async Task<T> CountAsync<T>(Func<LifeOSDbContext, Task<T>> query)
    {
        await using var scope = fixture.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<LifeOSDbContext>());
    }

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now.AddDays(-30));
        await PostgresAssert.InsertAsync(fixture, user);

        return user;
    }

    private async Task Execute(string sql)
    {
        await using var scope = fixture.CreateScope();
        await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.ExecuteSqlRawAsync(sql);
    }

    private static IAssistantConversationRepository Repository(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IAssistantConversationRepository>();
}

// Delegates to the real repository and runs `beforeSave` right before a save (by default only the first
// TrySaveAsync; TryAddAsync too when pauseBeforeAdd): a rendezvous that lines concurrent requests up after
// their reads, or a concurrent write that makes the save lose.
internal sealed class PausingAssistantRepository(
    IAssistantConversationRepository inner,
    Func<Task> beforeSave,
    bool pauseEverySave = false,
    bool pauseBeforeAdd = false) : IAssistantConversationRepository
{
    private bool _paused;

    public int Saves { get; private set; }

    public async Task<bool> TryAddAsync(AssistantConversation conversation, CancellationToken cancellationToken)
    {
        if (pauseBeforeAdd)
        {
            await PauseAsync();
        }

        return await inner.TryAddAsync(conversation, cancellationToken);
    }

    public Task<AssistantConversation?> GetAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken) =>
        inner.GetAsync(userId, conversationId, cancellationToken);

    public Task<AssistantConversation?> GetByClientIdAsync(Guid userId, Guid clientConversationId, CancellationToken cancellationToken) =>
        inner.GetByClientIdAsync(userId, clientConversationId, cancellationToken);

    public Task<IReadOnlyList<AssistantConversation>> ListAsync(Guid userId, AssistantConversationCursor? before, int limit,
        CancellationToken cancellationToken) =>
        inner.ListAsync(userId, before, limit, cancellationToken);

    public Task<bool> DeleteAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken) =>
        inner.DeleteAsync(userId, conversationId, cancellationToken);

    public Task<IReadOnlyList<AssistantMessage>> GetMessagesAsync(Guid userId, Guid conversationId, int afterSequence, int limit,
        CancellationToken cancellationToken) =>
        inner.GetMessagesAsync(userId, conversationId, afterSequence, limit, cancellationToken);

    public Task<AssistantMessage?> GetUserMessageAsync(Guid userId, Guid conversationId, Guid messageId, CancellationToken cancellationToken) =>
        inner.GetUserMessageAsync(userId, conversationId, messageId, cancellationToken);

    public Task<AssistantMessage?> FindUserMessageAsync(Guid userId, Guid messageId, CancellationToken cancellationToken) =>
        inner.FindUserMessageAsync(userId, messageId, cancellationToken);

    public Task<AssistantMessage?> GetUserMessageByClientIdAsync(Guid userId, Guid conversationId, Guid clientMessageId,
        CancellationToken cancellationToken) =>
        inner.GetUserMessageByClientIdAsync(userId, conversationId, clientMessageId, cancellationToken);

    public Task<AssistantMessage?> GetProcessingTurnAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken) =>
        inner.GetProcessingTurnAsync(userId, conversationId, cancellationToken);

    public Task<AssistantMessage?> GetReplyAsync(Guid userId, Guid conversationId, Guid userMessageId, CancellationToken cancellationToken) =>
        inner.GetReplyAsync(userId, conversationId, userMessageId, cancellationToken);

    public Task<bool> HasLaterUserMessageAsync(Guid userId, Guid conversationId, int sequence, CancellationToken cancellationToken) =>
        inner.HasLaterUserMessageAsync(userId, conversationId, sequence, cancellationToken);

    public async Task<bool> TrySaveAsync(IReadOnlyCollection<AssistantMessage> added, CancellationToken cancellationToken)
    {
        Saves++;
        await PauseAsync();

        return await inner.TrySaveAsync(added, cancellationToken);
    }

    private async Task PauseAsync()
    {
        if (!_paused || pauseEverySave)
        {
            _paused = true;
            await beforeSave();
        }
    }
}
