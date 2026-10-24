using LifeOS.Domain.Assistant;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Assistant;

// AI-007 F1: conversation and message invariants, sequence allocation and the turn state machine.
public class AssistantConversationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Lease = Now.AddSeconds(90);

    // ---- Conversation ----

    [Fact]
    public void Create_SetsEveryField()
    {
        var clientId = Guid.CreateVersion7();
        var conversation = AssistantConversation.Create(TestUsers.A, clientId, new DateTimeOffset(2026, 10, 24, 11, 0, 0, TimeSpan.FromHours(2)));

        Assert.Equal(7, conversation.Id.Version);
        Assert.Equal((TestUsers.A, clientId), (conversation.UserId, conversation.ClientConversationId));
        Assert.Equal(AssistantConversation.FirstSequence, conversation.NextSequence);
        Assert.Equal((Now, Now), (conversation.CreatedAtUtc, conversation.UpdatedAtUtc));
        Assert.Equal(TimeSpan.Zero, conversation.CreatedAtUtc.Offset);
    }

    [Fact]
    public void Create_RequiresUserAndClientId()
    {
        Assert.Throws<ArgumentException>(() => AssistantConversation.Create(Guid.Empty, Guid.CreateVersion7(), Now));
        Assert.Throws<ArgumentException>(() => AssistantConversation.Create(TestUsers.A, Guid.Empty, Now));
    }

    [Fact]
    public void Messages_GetConsecutiveSequences_AndMoveUpdatedAt()
    {
        var conversation = NewConversation();

        var first = conversation.AppendUserMessage(Guid.CreateVersion7(), "Ciao", Now, Lease);
        var reply = conversation.CompleteTurn(first, 1, "Ciao!", Now.AddSeconds(5));
        var second = conversation.AppendUserMessage(Guid.CreateVersion7(), "Altro", Now.AddMinutes(1), Lease.AddMinutes(1));

        Assert.Equal([1, 2, 3], new[] { first.Sequence, reply.Sequence, second.Sequence });
        Assert.Equal(4, conversation.NextSequence);
        Assert.Equal(Now.AddMinutes(1), conversation.UpdatedAtUtc);
        Assert.All(new[] { first, reply, second }, message =>
            Assert.Equal((conversation.Id, TestUsers.A), (message.ConversationId, message.UserId)));
    }

    // Instants are kept at PostgreSQL's precision, so what a use case returns equals what a later read returns.
    [Fact]
    public void Instants_AreTruncatedToWholeMicroseconds()
    {
        var now = Now.AddTicks(7);
        var conversation = AssistantConversation.Create(TestUsers.A, Guid.CreateVersion7(), now);

        var message = conversation.AppendUserMessage(Guid.CreateVersion7(), "Ciao", now.AddTicks(10), Lease.AddTicks(9));
        var reply = conversation.CompleteTurn(message, 1, "Ciao!", now.AddTicks(25));

        // 1 µs = 10 ticks: Now+7 → Now, Now+17 → Now+10, Now+32 → Now+30.
        Assert.Equal((Now, Now.AddTicks(10)), (conversation.CreatedAtUtc, message.CreatedAtUtc));
        Assert.Equal((Now.AddTicks(30), Now.AddTicks(30)), (reply.CreatedAtUtc, conversation.UpdatedAtUtc));
        Assert.Null(message.TurnLeaseUntilUtc);

        var retried = NewConversation().AppendUserMessage(Guid.CreateVersion7(), "Ciao", Now, Lease.AddTicks(9));
        Assert.Equal(Lease, retried.TurnLeaseUntilUtc);
    }

    // ---- Messages ----

    [Fact]
    public void UserMessage_OpensAProcessingTurn()
    {
        var clientId = Guid.CreateVersion7();
        var message = NewConversation().AppendUserMessage(clientId, "  Speso 5€ per colazione\ncon Pamela  ", Now, Lease);

        Assert.Equal(7, message.Id.Version);
        Assert.Equal(AssistantMessageRole.User, message.Role);
        Assert.Equal("Speso 5€ per colazione\ncon Pamela", message.Content);
        Assert.Equal(clientId, message.ClientMessageId);
        Assert.Null(message.ReplyToMessageId);
        Assert.Equal((AssistantTurnStatus.Processing, 1, Lease), (message.TurnStatus, message.TurnAttempts, message.TurnLeaseUntilUtc));
        Assert.Null(message.FailureCode);
        Assert.Equal(Now, message.CreatedAtUtc);
    }

    [Fact]
    public void UserMessage_RequiresClientIdAndALeaseInTheFuture()
    {
        var conversation = NewConversation();

        Assert.Throws<ArgumentException>(() => conversation.AppendUserMessage(Guid.Empty, "Ciao", Now, Lease));
        Assert.Throws<ArgumentOutOfRangeException>(() => conversation.AppendUserMessage(Guid.CreateVersion7(), "Ciao", Now, Now));
        Assert.Equal(AssistantConversation.FirstSequence, conversation.NextSequence);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    [InlineData("Ciao\0")]
    [InlineData("\0")]
    public void UserMessage_RejectsBlankTextAndNul(string content)
    {
        var conversation = NewConversation();

        Assert.Equal("content", Assert.Throws<ArgumentException>(() =>
            conversation.AppendUserMessage(Guid.CreateVersion7(), content, Now, Lease)).ParamName);
        Assert.Equal(AssistantConversation.FirstSequence, conversation.NextSequence);
    }

    [Fact]
    public void Limits_AreInclusive_AndLongerTextIsRejected_NeverTruncated()
    {
        var conversation = NewConversation();

        var user = conversation.AppendUserMessage(Guid.CreateVersion7(), new string('u', AssistantMessage.MaxUserContentLength), Now, Lease);
        Assert.Equal(AssistantMessage.MaxUserContentLength, user.Content.Length);
        Assert.Throws<ArgumentException>(() => conversation.AppendUserMessage(Guid.CreateVersion7(),
            new string('u', AssistantMessage.MaxUserContentLength + 1), Now, Lease));

        Assert.Throws<ArgumentException>(() => conversation.CompleteTurn(user, 1, new string('a', AssistantMessage.MaxAssistantContentLength + 1), Now));
        Assert.Throws<ArgumentException>(() => conversation.CompleteTurn(user, 1, "con\0nul", Now));
        Assert.Equal(AssistantTurnStatus.Processing, user.TurnStatus);

        var reply = conversation.CompleteTurn(user, 1, new string('a', AssistantMessage.MaxAssistantContentLength), Now);
        Assert.Equal(AssistantMessage.MaxAssistantContentLength, reply.Content.Length);
    }

    // ---- Turn ----

    [Fact]
    public void CompleteTurn_ClosesTheTurn_AndAppendsTheReply()
    {
        var conversation = NewConversation();
        var user = conversation.AppendUserMessage(Guid.CreateVersion7(), "Ciao", Now, Lease);

        var reply = conversation.CompleteTurn(user, 1, "L'assistente non è ancora attivo.", Now.AddSeconds(3));

        Assert.Equal((AssistantTurnStatus.Completed, 1), (user.TurnStatus, user.TurnAttempts));
        Assert.Null(user.TurnLeaseUntilUtc);
        Assert.Equal(AssistantMessageRole.Assistant, reply.Role);
        Assert.Equal(user.Id, reply.ReplyToMessageId);
        Assert.Null(reply.ClientMessageId);
        Assert.Null(reply.TurnStatus);
        Assert.Null(reply.TurnAttempts);
        Assert.Equal(Now.AddSeconds(3), reply.CreatedAtUtc);
    }

    [Fact]
    public void CompleteTurn_OfAnotherConversation_IsRejected()
    {
        var user = NewConversation().AppendUserMessage(Guid.CreateVersion7(), "Ciao", Now, Lease);

        Assert.Throws<ArgumentException>(() => NewConversation().CompleteTurn(user, 1, "Ciao!", Now));
        Assert.Equal(AssistantTurnStatus.Processing, user.TurnStatus);
    }

    [Fact]
    public void ACompletedTurn_IsFinal()
    {
        var conversation = NewConversation();
        var user = conversation.AppendUserMessage(Guid.CreateVersion7(), "Ciao", Now, Lease);
        conversation.CompleteTurn(user, 1, "Ciao!", Now);
        var next = conversation.NextSequence;

        Assert.Throws<InvalidOperationException>(() => conversation.CompleteTurn(user, 1, "Di nuovo", Now));
        Assert.Throws<InvalidOperationException>(() => user.FailTurn(1, "ai_unavailable", Now));
        Assert.Throws<InvalidOperationException>(() => user.RetryTurn(AssistantTurnRetryMode.Manual, Now, Lease));
        Assert.False(user.InterruptIfLeaseExpired(Lease.AddHours(1)));
        Assert.Equal(AssistantTurnStatus.Completed, user.TurnStatus);
        Assert.Equal(next, conversation.NextSequence);
    }

    [Fact]
    public void FailTurn_StoresTheCode_AndRetryStartsTheNextAttempt()
    {
        var user = NewConversation().AppendUserMessage(Guid.CreateVersion7(), "Ciao", Now, Lease);

        user.FailTurn(1, "ai_unavailable", Now);
        Assert.Equal((AssistantTurnStatus.Failed, "ai_unavailable"), (user.TurnStatus, user.FailureCode));
        Assert.Null(user.TurnLeaseUntilUtc);

        var attempt = user.RetryTurn(AssistantTurnRetryMode.Automatic, Now.AddMinutes(1), Lease.AddMinutes(1));

        Assert.Equal(2, attempt);
        Assert.Equal((AssistantTurnStatus.Processing, 2, Lease.AddMinutes(1)), (user.TurnStatus, user.TurnAttempts, user.TurnLeaseUntilUtc));
        Assert.Null(user.FailureCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AI_unavailable")]
    [InlineData("ai unavailable")]
    [InlineData("errore: timeout")]
    public void FailTurn_AcceptsOnlyStableCodes(string code)
    {
        var user = NewConversation().AppendUserMessage(Guid.CreateVersion7(), "Ciao", Now, Lease);

        Assert.Throws<ArgumentException>(() => user.FailTurn(1, code, Now));
        Assert.Throws<ArgumentException>(() => user.FailTurn(1, new string('a', AssistantMessage.MaxFailureCodeLength + 1), Now));
        Assert.Equal(AssistantTurnStatus.Processing, user.TurnStatus);
    }

    [Fact]
    public void OnlyAFailedTurn_CanBeRetried()
    {
        var user = NewConversation().AppendUserMessage(Guid.CreateVersion7(), "Ciao", Now, Lease);

        Assert.Throws<InvalidOperationException>(() => user.RetryTurn(AssistantTurnRetryMode.Manual, Now, Lease));

        user.FailTurn(1, "ai_invalid", Now);
        Assert.Throws<ArgumentOutOfRangeException>(() => user.RetryTurn(AssistantTurnRetryMode.Manual, Now, Now.AddSeconds(-1)));
        Assert.Equal((AssistantTurnStatus.Failed, 1), (user.TurnStatus, user.TurnAttempts));
    }

    [Fact]
    public void AutomaticRetries_StopAtTheThirdAttempt_ManualRetriesDoNot()
    {
        var user = NewConversation().AppendUserMessage(Guid.CreateVersion7(), "Ciao", Now, Lease);

        user.FailTurn(1, "ai_unavailable", Now);
        Assert.True(user.CanRetryAutomatically);
        user.FailTurn(user.RetryTurn(AssistantTurnRetryMode.Automatic, Now, Lease), "ai_unavailable", Now);
        Assert.True(user.CanRetryAutomatically);
        user.FailTurn(user.RetryTurn(AssistantTurnRetryMode.Automatic, Now, Lease), "ai_unavailable", Now);

        Assert.Equal(AssistantMessage.MaxAutomaticAttempts, user.TurnAttempts);
        Assert.False(user.CanRetryAutomatically);
        Assert.Throws<InvalidOperationException>(() => user.RetryTurn(AssistantTurnRetryMode.Automatic, Now, Lease));
        Assert.Equal((AssistantTurnStatus.Failed, 3), (user.TurnStatus, user.TurnAttempts));

        Assert.Equal(4, user.RetryTurn(AssistantTurnRetryMode.Manual, Now, Lease));
        Assert.Equal(AssistantTurnStatus.Processing, user.TurnStatus);
    }

    [Fact]
    public void LazyRecovery_InterruptsOnlyAnExpiredProcessingTurn()
    {
        var user = NewConversation().AppendUserMessage(Guid.CreateVersion7(), "Ciao", Now, Lease);

        Assert.False(user.InterruptIfLeaseExpired(Lease.AddTicks(-1)));
        Assert.Equal(AssistantTurnStatus.Processing, user.TurnStatus);

        Assert.True(user.InterruptIfLeaseExpired(Lease));
        Assert.Equal((AssistantTurnStatus.Failed, AssistantMessage.Interrupted), (user.TurnStatus, user.FailureCode));
        Assert.Null(user.TurnLeaseUntilUtc);

        Assert.False(user.InterruptIfLeaseExpired(Lease.AddHours(1)));
        Assert.Equal(AssistantMessage.Interrupted, user.FailureCode);
    }

    // Fencing: once an expired attempt is taken over, only the new attempt may close the turn.
    [Fact]
    public void ATakenOverAttempt_CanNeitherCompleteNorFail()
    {
        var conversation = NewConversation();
        var user = conversation.AppendUserMessage(Guid.CreateVersion7(), "Ciao", Now, Lease);

        Assert.True(user.InterruptIfLeaseExpired(Lease));
        var current = user.RetryTurn(AssistantTurnRetryMode.Automatic, Lease, Lease.AddSeconds(90));
        var next = conversation.NextSequence;

        Assert.Throws<InvalidOperationException>(() => conversation.CompleteTurn(user, 1, "Risposta tardiva", Lease));
        Assert.Throws<InvalidOperationException>(() => user.FailTurn(1, "ai_unavailable", Lease));
        Assert.Equal((AssistantTurnStatus.Processing, 2), (user.TurnStatus, user.TurnAttempts));
        Assert.Equal(next, conversation.NextSequence);

        var reply = conversation.CompleteTurn(user, current, "Risposta", Lease.AddSeconds(10));
        Assert.Equal(next, reply.Sequence);
        Assert.Equal(AssistantTurnStatus.Completed, user.TurnStatus);
    }

    // A reply directly follows the message it answers: once a later message exists (a retry raced it), the
    // older turn cannot complete, and nothing changes.
    [Fact]
    public void ATurnFollowedByALaterMessage_CannotComplete()
    {
        var conversation = NewConversation();
        var older = conversation.AppendUserMessage(Guid.CreateVersion7(), "Primo", Now, Lease);
        older.FailTurn(1, "ai_unavailable", Now);
        var later = conversation.AppendUserMessage(Guid.CreateVersion7(), "Secondo", Now, Lease);
        var attempt = older.RetryTurn(AssistantTurnRetryMode.Manual, Now, Lease);

        Assert.False(conversation.IsLastMessage(older));
        Assert.True(conversation.IsLastMessage(later));
        Assert.Throws<InvalidOperationException>(() => conversation.CompleteTurn(older, attempt, "Risposta", Now));
        Assert.Equal((AssistantTurnStatus.Processing, 3), (older.TurnStatus, conversation.NextSequence));
    }

    // An interrupted attempt whose lease expired cannot complete either: recovery already failed it.
    [Fact]
    public void AnInterruptedAttempt_CannotComplete()
    {
        var conversation = NewConversation();
        var user = conversation.AppendUserMessage(Guid.CreateVersion7(), "Ciao", Now, Lease);
        user.InterruptIfLeaseExpired(Lease);

        Assert.Throws<InvalidOperationException>(() => conversation.CompleteTurn(user, 1, "Risposta tardiva", Lease));
        Assert.Equal(AssistantTurnStatus.Failed, user.TurnStatus);
    }

    // ---- Lease ----

    // The lease end is exclusive: the holder may close the turn up to the instant before it, never at it.
    [Fact]
    public void TheHolder_CanCompleteUntilTheLeaseEnds_ButNotAtOrAfterIt()
    {
        var conversation = NewConversation();
        var user = conversation.AppendUserMessage(Guid.CreateVersion7(), "Ciao", Now, Lease);
        var next = conversation.NextSequence;

        Assert.True(user.IsHeldBy(1, Lease.AddTicks(-1)));
        Assert.False(user.IsHeldBy(1, Lease));
        Assert.False(user.IsHeldBy(2, Now));

        Assert.Throws<InvalidOperationException>(() => conversation.CompleteTurn(user, 1, "Risposta tardiva", Lease));
        Assert.Throws<InvalidOperationException>(() => conversation.CompleteTurn(user, 1, "Risposta tardiva", Lease.AddMinutes(5)));
        Assert.Equal((AssistantTurnStatus.Processing, 1, Lease), (user.TurnStatus, user.TurnAttempts, user.TurnLeaseUntilUtc));
        Assert.Equal(next, conversation.NextSequence);

        var reply = conversation.CompleteTurn(user, 1, "Risposta", Lease.AddTicks(-1));
        Assert.Equal(next, reply.Sequence);
        Assert.Equal(AssistantTurnStatus.Completed, user.TurnStatus);
    }

    [Fact]
    public void TheHolder_CannotFailTheTurnOnceTheLeaseEnded()
    {
        var user = NewConversation().AppendUserMessage(Guid.CreateVersion7(), "Ciao", Now, Lease);

        Assert.Throws<InvalidOperationException>(() => user.FailTurn(1, "ai_unavailable", Lease));
        Assert.Equal(AssistantTurnStatus.Processing, user.TurnStatus);
        Assert.Null(user.FailureCode);

        // What recovery does instead.
        Assert.True(user.InterruptIfLeaseExpired(Lease));
        Assert.Equal(AssistantMessage.Interrupted, user.FailureCode);
    }

    // A retried attempt gets its own lease; an expired lease of the new attempt fences it the same way.
    [Fact]
    public void ARetriedAttempt_IsBoundByItsOwnLease()
    {
        var conversation = NewConversation();
        var user = conversation.AppendUserMessage(Guid.CreateVersion7(), "Ciao", Now, Lease);
        user.InterruptIfLeaseExpired(Lease);
        var secondLease = Lease.AddSeconds(90);
        var attempt = user.RetryTurn(AssistantTurnRetryMode.Manual, Lease, secondLease);

        Assert.True(user.IsHeldBy(attempt, Lease));
        Assert.Throws<InvalidOperationException>(() => conversation.CompleteTurn(user, attempt, "Risposta", secondLease));
        Assert.True(user.InterruptIfLeaseExpired(secondLease));
        Assert.Equal((AssistantTurnStatus.Failed, 2), (user.TurnStatus, user.TurnAttempts));
    }

    private static AssistantConversation NewConversation() => AssistantConversation.Create(TestUsers.A, Guid.CreateVersion7(), Now);
}
