using LifeOS.Domain.Assistant;

namespace LifeOS.Application.Assistant;

// AI-007 F1: conversations and messages of LifeOS Assistant. Every method is scoped to userId: another
// user's conversation or message is indistinguishable from a missing one.
//
// Writes are optimistic. "Tracked" reads return entities the caller may change and then save with
// TrySaveAsync, together with the messages it created. A save that loses a race (a stale sequence or
// turn attempt, a duplicate client message id, a second Processing turn, a conversation deleted in the
// meantime) stores nothing, returns false and forgets every tracked entity: the caller must read the
// state again and decide again, never retry with the entities it holds.
public interface IAssistantConversationRepository
{
    // False, and nothing stored, when the user already has a conversation with this client id.
    Task<bool> TryAddAsync(AssistantConversation conversation, CancellationToken cancellationToken);

    // Tracked.
    Task<AssistantConversation?> GetAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken);

    Task<AssistantConversation?> GetByClientIdAsync(Guid userId, Guid clientConversationId, CancellationToken cancellationToken);

    // Most recently active first (UpdatedAtUtc, then Id, both descending), strictly after `before` in
    // that order when given.
    Task<IReadOnlyList<AssistantConversation>> ListAsync(Guid userId, AssistantConversationCursor? before, int limit,
        CancellationToken cancellationToken);

    // Hard delete; the messages cascade. False when the user has no such conversation.
    Task<bool> DeleteAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken);

    // Messages with Sequence > afterSequence, in sequence order.
    Task<IReadOnlyList<AssistantMessage>> GetMessagesAsync(Guid userId, Guid conversationId, int afterSequence, int limit,
        CancellationToken cancellationToken);

    // A user message (the ones that carry a turn), tracked.
    Task<AssistantMessage?> GetUserMessageAsync(Guid userId, Guid conversationId, Guid messageId, CancellationToken cancellationToken);

    // A user message (the ones that carry a turn) of any of the user's conversations, tracked.
    Task<AssistantMessage?> FindUserMessageAsync(Guid userId, Guid messageId, CancellationToken cancellationToken);

    // A user message by its client idempotency key, tracked.
    Task<AssistantMessage?> GetUserMessageByClientIdAsync(Guid userId, Guid conversationId, Guid clientMessageId,
        CancellationToken cancellationToken);

    // The conversation's Processing turn (at most one), tracked, whatever its lease.
    Task<AssistantMessage?> GetProcessingTurnAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken);

    // The assistant message that completed this user message's turn.
    Task<AssistantMessage?> GetReplyAsync(Guid userId, Guid conversationId, Guid userMessageId, CancellationToken cancellationToken);

    // Whether a user message after `sequence` exists.
    Task<bool> HasLaterUserMessageAsync(Guid userId, Guid conversationId, int sequence, CancellationToken cancellationToken);

    // AI-007 F3, inside a unit of work only: locks the conversation and the user message until the transaction
    // ends if `attempt` still holds the message's turn at `now` (Processing, that attempt, lease not ended) and
    // the message is the conversation's last one. A concurrent send, retry, recovery or delete of the
    // conversation waits for this transaction. False, nothing locked, otherwise.
    Task<bool> TryLockTurnAsync(Guid userId, Guid conversationId, Guid messageId, int attempt, DateTimeOffset now,
        CancellationToken cancellationToken);

    // Saves the changes of the tracked entities and inserts `added`, all in one database transaction.
    // False when a concurrent write won (see above).
    Task<bool> TrySaveAsync(IReadOnlyCollection<AssistantMessage> added, CancellationToken cancellationToken);
}
