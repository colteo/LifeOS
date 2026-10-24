using LifeOS.Domain.Assistant;

namespace LifeOS.Application.Assistant;

// AI-007 F1: the user's conversations. Conversations are kept until the user deletes them; nothing here
// deletes or expires them automatically.

// Idempotent on clientConversationId (AI-007 §11): a repeated request, also a concurrent one, returns
// the conversation the first request created (Created = false). The database's unique (user, client id)
// index decides the race; the client is never trusted to avoid it.
public sealed class CreateAssistantConversationHandler(IAssistantConversationRepository repository, TimeProvider clock)
{
    public async Task<AssistantResult<AssistantConversationResult>> HandleAsync(Guid userId, Guid clientConversationId,
        CancellationToken cancellationToken)
    {
        if (clientConversationId == Guid.Empty)
        {
            return AssistantResult<AssistantConversationResult>.Invalid("clientConversationId", "A client conversation id is required.");
        }

        for (var attempt = 0; attempt < AssistantTurnPolicy.MaxWriteAttempts; attempt++)
        {
            if (await repository.GetByClientIdAsync(userId, clientConversationId, cancellationToken) is { } existing)
            {
                return AssistantResult<AssistantConversationResult>.Ok(new AssistantConversationResult(existing, Created: false));
            }

            var conversation = AssistantConversation.Create(userId, clientConversationId, clock.GetUtcNow());

            if (await repository.TryAddAsync(conversation, cancellationToken))
            {
                return AssistantResult<AssistantConversationResult>.Ok(new AssistantConversationResult(conversation, Created: true));
            }

            // A concurrent request with the same client id won: read its conversation.
        }

        return AssistantResult<AssistantConversationResult>.Busy();
    }
}

public sealed class ListAssistantConversationsHandler(IAssistantConversationRepository repository)
{
    public async Task<AssistantResult<IReadOnlyList<AssistantConversation>>> HandleAsync(Guid userId, AssistantConversationCursor? before,
        int? limit, CancellationToken cancellationToken)
    {
        var take = limit ?? AssistantPaging.DefaultConversations;

        if (take is < 1 or > AssistantPaging.MaxConversations)
        {
            return AssistantResult<IReadOnlyList<AssistantConversation>>.Invalid("limit",
                $"limit must be between 1 and {AssistantPaging.MaxConversations}.");
        }

        return AssistantResult<IReadOnlyList<AssistantConversation>>.Ok(
            await repository.ListAsync(userId, before, take, cancellationToken));
    }
}

// The conversation's messages in sequence order, after afterSequence. Lazy recovery (AI-007 §10.8):
// a Processing turn whose lease has ended is failed with Interrupted before the messages are read, so
// the app shows it as Failed with Retry.
public sealed class GetAssistantConversationMessagesHandler(IAssistantConversationRepository repository, TimeProvider clock)
{
    public async Task<AssistantResult<AssistantMessagesPage>> HandleAsync(Guid userId, Guid conversationId, int? afterSequence,
        int? limit, CancellationToken cancellationToken)
    {
        var after = afterSequence ?? 0;
        var take = limit ?? AssistantPaging.DefaultMessages;

        if (after < 0)
        {
            return AssistantResult<AssistantMessagesPage>.Invalid("afterSequence", "afterSequence cannot be negative.");
        }

        if (take is < 1 or > AssistantPaging.MaxMessages)
        {
            return AssistantResult<AssistantMessagesPage>.Invalid("limit", $"limit must be between 1 and {AssistantPaging.MaxMessages}.");
        }

        if (await repository.GetAsync(userId, conversationId, cancellationToken) is not { } conversation)
        {
            return AssistantResult<AssistantMessagesPage>.NotFound();
        }

        if (await repository.GetProcessingTurnAsync(userId, conversationId, cancellationToken) is { } turn
            && turn.InterruptIfLeaseExpired(clock.GetUtcNow()))
        {
            // Losing this write is fine: whatever won (the turn completed, or another request recovered it)
            // is what the read below returns.
            await repository.TrySaveAsync([], cancellationToken);
        }

        var messages = await repository.GetMessagesAsync(userId, conversationId, after, take, cancellationToken);

        return AssistantResult<AssistantMessagesPage>.Ok(new AssistantMessagesPage(conversation, messages));
    }
}

// Manual deletion only (AI-007 §9.5): the messages cascade. A second delete is NotFound.
public sealed class DeleteAssistantConversationHandler(IAssistantConversationRepository repository)
{
    public async Task<AssistantResultStatus> HandleAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken) =>
        await repository.DeleteAsync(userId, conversationId, cancellationToken)
            ? AssistantResultStatus.Ok
            : AssistantResultStatus.NotFound;
}
