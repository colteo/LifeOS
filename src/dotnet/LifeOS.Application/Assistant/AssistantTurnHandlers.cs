using LifeOS.Domain.Assistant;

namespace LifeOS.Application.Assistant;

// AI-007 F1 §10.1, §10.8, §11: accepting a user message is separate from processing it. Send and Retry
// only make the turn durable as Processing, with a lease and an attempt number (the fencing token); they
// never produce an answer. Complete and Fail are the processor's side. Users reach all of them only
// through AssistantTurnProcessor, which processes the attempt it started in the same request; in F1, with
// no AI integrated, that attempt fails at once with assistant_unavailable (see AssistantTurnProcessor).
// A turn left Processing by a crashed request is failed with Interrupted by lazy recovery once its lease
// ends; the user can then retry it.
//
// Every write is optimistic (see IAssistantConversationRepository). A lost write is never repeated with
// the entities in hand: the handler reads the authoritative state again and decides again, at most
// AssistantTurnPolicy.MaxWriteAttempts times, then answers Busy.

public sealed record SendAssistantMessageCommand(Guid ClientMessageId, string Text);

// The processor's answer for `Attempt` of the turn of user message MessageId.
public sealed record CompleteAssistantTurnCommand(Guid MessageId, int Attempt, string Reply);

// The processor's failure of `Attempt`, with a stable code (no assistant message is stored).
public sealed record FailAssistantTurnCommand(Guid MessageId, int Attempt, string FailureCode);

// Accepts the user's message and opens its turn, idempotently on clientMessageId:
//
// - new id: a Processing turn of the conversation whose lease is still running → InProgress; one whose
//   lease ended is failed (Interrupted) in the same save that accepts the new message;
// - same id, same text: the stored turn — Completed with its reply, Processing → InProgress, Failed →
//   restarted automatically while automatic attempts remain (and it is the latest turn), otherwise
//   returned as Failed for the user to retry explicitly;
// - same id, other text → Conflict client_message_id_reused.
//
// The text is compared after trimming, as the domain stores it.
public sealed class SendAssistantMessageHandler(IAssistantConversationRepository repository, TimeProvider clock)
{
    public async Task<AssistantResult<AssistantTurn>> HandleAsync(Guid userId, Guid conversationId, SendAssistantMessageCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientMessageId == Guid.Empty)
        {
            return AssistantResult<AssistantTurn>.Invalid("clientMessageId", "A client message id is required.");
        }

        for (var attempt = 0; attempt < AssistantTurnPolicy.MaxWriteAttempts; attempt++)
        {
            var now = clock.GetUtcNow();

            if (await repository.GetAsync(userId, conversationId, cancellationToken) is not { } conversation)
            {
                return AssistantResult<AssistantTurn>.NotFound();
            }

            if (await repository.GetUserMessageByClientIdAsync(userId, conversationId, command.ClientMessageId, cancellationToken) is { } existing)
            {
                if (await ReplayAsync(existing, command.Text, now, cancellationToken) is { } replay)
                {
                    return replay;
                }

                continue;
            }

            if (await AssistantTurns.LiveProcessingTurnAsync(repository, userId, conversationId, now, cancellationToken) is { } running)
            {
                return AssistantResult<AssistantTurn>.InProgress(new AssistantTurn(running, null, Replayed: false));
            }

            AssistantMessage message;

            try
            {
                message = conversation.AppendUserMessage(command.ClientMessageId, command.Text, now, now + AssistantTurnPolicy.LeaseDuration);
            }
            catch (ArgumentException exception)
            {
                return AssistantResult<AssistantTurn>.Invalid("text", exception);
            }

            if (await repository.TrySaveAsync([message], cancellationToken))
            {
                return AssistantResult<AssistantTurn>.Ok(new AssistantTurn(message, null, Replayed: false));
            }
        }

        return AssistantResult<AssistantTurn>.Busy();
    }

    // Null: a concurrent write won; read and decide again.
    private async Task<AssistantResult<AssistantTurn>?> ReplayAsync(AssistantMessage existing, string? text, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(existing.Content, text?.Trim(), StringComparison.Ordinal))
        {
            return AssistantResult<AssistantTurn>.Conflict(AssistantErrors.ClientMessageIdReused,
                "This message id was already used for another text.");
        }

        existing.InterruptIfLeaseExpired(now);

        switch (existing.TurnStatus)
        {
            case AssistantTurnStatus.Completed:
                var reply = await repository.GetReplyAsync(existing.UserId, existing.ConversationId, existing.Id, cancellationToken);
                return AssistantResult<AssistantTurn>.Ok(new AssistantTurn(existing, reply, Replayed: true));

            case AssistantTurnStatus.Processing:
                return AssistantResult<AssistantTurn>.InProgress(new AssistantTurn(existing, null, Replayed: true));
        }

        if (existing.CanRetryAutomatically
            && !await repository.HasLaterUserMessageAsync(existing.UserId, existing.ConversationId, existing.Sequence, cancellationToken))
        {
            existing.RetryTurn(AssistantTurnRetryMode.Automatic, now, now + AssistantTurnPolicy.LeaseDuration);
        }

        // Stores the retry, or the interruption found above; nothing when the turn simply stays Failed.
        return await repository.TrySaveAsync([], cancellationToken)
            ? AssistantResult<AssistantTurn>.Ok(new AssistantTurn(existing, null, Replayed: true))
            : null;
    }
}

// The user's "Riprova" on a Failed turn: always allowed, also after the automatic attempts are used up,
// but only for the conversation's latest user message (an older one would be answered out of order) —
// otherwise Conflict turn_superseded. It keeps the message and its client id and starts the next attempt
// (a new fencing token and lease), so a late completion of an earlier attempt is rejected.
// Idempotent: a Completed turn returns its reply, a Processing one InProgress.
//
// The message is addressed by its id alone (POST /api/assistant/messages/{id}/retry); its conversation is
// the one it is stored in.
public sealed class RetryAssistantTurnHandler(IAssistantConversationRepository repository, TimeProvider clock)
{
    public async Task<AssistantResult<AssistantTurn>> HandleAsync(Guid userId, Guid messageId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < AssistantTurnPolicy.MaxWriteAttempts; attempt++)
        {
            var now = clock.GetUtcNow();

            if (await repository.FindUserMessageAsync(userId, messageId, cancellationToken) is not { } message)
            {
                return AssistantResult<AssistantTurn>.NotFound();
            }

            var conversationId = message.ConversationId;

            message.InterruptIfLeaseExpired(now);

            switch (message.TurnStatus)
            {
                case AssistantTurnStatus.Completed:
                    var reply = await repository.GetReplyAsync(userId, conversationId, message.Id, cancellationToken);
                    return AssistantResult<AssistantTurn>.Ok(new AssistantTurn(message, reply, Replayed: true));

                case AssistantTurnStatus.Processing:
                    return AssistantResult<AssistantTurn>.InProgress(new AssistantTurn(message, null, Replayed: true));
            }

            if (await repository.HasLaterUserMessageAsync(userId, conversationId, message.Sequence, cancellationToken))
            {
                return AssistantResult<AssistantTurn>.Conflict(AssistantErrors.TurnSuperseded,
                    "Only the latest message can be retried. Send it again instead.");
            }

            message.RetryTurn(AssistantTurnRetryMode.Manual, now, now + AssistantTurnPolicy.LeaseDuration);

            if (await repository.TrySaveAsync([], cancellationToken))
            {
                return AssistantResult<AssistantTurn>.Ok(new AssistantTurn(message, null, Replayed: false));
            }
        }

        return AssistantResult<AssistantTurn>.Busy();
    }
}

// Processor only (never mapped to a user endpoint; an architecture test keeps LifeOS.Api off it): stores
// the reply of the turn if `Attempt` still holds it now — the current attempt, before its lease ends —
// otherwise Conflict turn_not_held and nothing is written — and only while no later message exists
// (Conflict turn_superseded). Repeating a completion that was stored returns the stored reply.
public sealed class CompleteAssistantTurnHandler(IAssistantConversationRepository repository, TimeProvider clock)
{
    public async Task<AssistantResult<AssistantTurn>> HandleAsync(Guid userId, Guid conversationId, CompleteAssistantTurnCommand command,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < AssistantTurnPolicy.MaxWriteAttempts; attempt++)
        {
            var now = clock.GetUtcNow();

            if (await repository.GetAsync(userId, conversationId, cancellationToken) is not { } conversation
                || await repository.GetUserMessageAsync(userId, conversationId, command.MessageId, cancellationToken) is not { } message)
            {
                return AssistantResult<AssistantTurn>.NotFound();
            }

            if (message.TurnStatus == AssistantTurnStatus.Completed && message.TurnAttempts == command.Attempt)
            {
                var stored = await repository.GetReplyAsync(userId, conversationId, message.Id, cancellationToken);
                return AssistantResult<AssistantTurn>.Ok(new AssistantTurn(message, stored, Replayed: true));
            }

            if (!message.IsHeldBy(command.Attempt, now))
            {
                return AssistantTurns.NotHeld();
            }

            // A retry that raced a new message can hold an older turn; its reply would follow the later
            // message. The conversation's next_sequence token makes this check hold until the save.
            if (!conversation.IsLastMessage(message))
            {
                return AssistantResult<AssistantTurn>.Conflict(AssistantErrors.TurnSuperseded,
                    "A later message was sent: this turn can no longer be answered.");
            }

            AssistantMessage reply;

            try
            {
                reply = conversation.CompleteTurn(message, command.Attempt, command.Reply, now);
            }
            catch (ArgumentException exception)
            {
                return AssistantResult<AssistantTurn>.Invalid("reply", exception);
            }

            if (await repository.TrySaveAsync([reply], cancellationToken))
            {
                return AssistantResult<AssistantTurn>.Ok(new AssistantTurn(message, reply, Replayed: false));
            }
        }

        return AssistantResult<AssistantTurn>.Busy();
    }
}

// Processor only, like CompleteAssistantTurnHandler: fails the turn with a stable code, no assistant
// message. Repeating the same failure returns the stored turn.
public sealed class FailAssistantTurnHandler(IAssistantConversationRepository repository, TimeProvider clock)
{
    public async Task<AssistantResult<AssistantTurn>> HandleAsync(Guid userId, Guid conversationId, FailAssistantTurnCommand command,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < AssistantTurnPolicy.MaxWriteAttempts; attempt++)
        {
            var now = clock.GetUtcNow();

            if (await repository.GetUserMessageAsync(userId, conversationId, command.MessageId, cancellationToken) is not { } message)
            {
                return AssistantResult<AssistantTurn>.NotFound();
            }

            if (message.TurnStatus == AssistantTurnStatus.Failed && message.TurnAttempts == command.Attempt
                && message.FailureCode == command.FailureCode)
            {
                return AssistantResult<AssistantTurn>.Ok(new AssistantTurn(message, null, Replayed: true));
            }

            if (!message.IsHeldBy(command.Attempt, now))
            {
                return AssistantTurns.NotHeld();
            }

            try
            {
                message.FailTurn(command.Attempt, command.FailureCode, now);
            }
            catch (ArgumentException exception)
            {
                return AssistantResult<AssistantTurn>.Invalid("failureCode", exception);
            }

            if (await repository.TrySaveAsync([], cancellationToken))
            {
                return AssistantResult<AssistantTurn>.Ok(new AssistantTurn(message, null, Replayed: false));
            }
        }

        return AssistantResult<AssistantTurn>.Busy();
    }
}

internal static class AssistantTurns
{
    // The conversation's Processing turn if its lease is still running. One whose lease ended is failed
    // with Interrupted (tracked; the caller's next save stores it) and null is returned.
    public static async Task<AssistantMessage?> LiveProcessingTurnAsync(IAssistantConversationRepository repository, Guid userId,
        Guid conversationId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await repository.GetProcessingTurnAsync(userId, conversationId, cancellationToken) is { } turn
        && turn.TurnStatus == AssistantTurnStatus.Processing
        && !turn.InterruptIfLeaseExpired(now)
            ? turn
            : null;

    public static AssistantResult<AssistantTurn> NotHeld() =>
        AssistantResult<AssistantTurn>.Conflict(AssistantErrors.TurnNotHeld, "This attempt no longer holds the turn.");
}
