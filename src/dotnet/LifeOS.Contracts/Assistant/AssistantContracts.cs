namespace LifeOS.Contracts.Assistant;

// AI-007 F1: LifeOS Assistant conversations and messages. There is never a user id: the owner is always the
// authenticated user. F1 has no AI: a sent message is stored and its turn fails at once with FailureCode
// "assistant_unavailable"; no assistant message is produced.

// ClientConversationId (required, non-empty UUID): the client's idempotency key. Repeating the request with
// the same value returns the same conversation (200 instead of 201).
public sealed record CreateAssistantConversationRequest(Guid? ClientConversationId);

public sealed record AssistantConversationResponse(
    Guid Id,
    Guid ClientConversationId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

// Most recently active first (UpdatedAtUtc, then Id). NextCursor continues the list (pass it as "cursor");
// null on the last page.
public sealed record AssistantConversationPageResponse(IReadOnlyList<AssistantConversationResponse> Items, string? NextCursor);

// ClientMessageId (required, non-empty UUID): the client's idempotency key, unique per conversation. Text:
// 1–1000 characters after trimming, no NUL.
public sealed record SendAssistantMessageRequest(Guid? ClientMessageId, string? Text);

// Role: "User" or "Assistant". Turn: user messages only. ReplyToMessageId: assistant messages only.
public sealed record AssistantMessageResponse(
    Guid Id,
    Guid ConversationId,
    int Sequence,
    string Role,
    string Content,
    Guid? ClientMessageId,
    Guid? ReplyToMessageId,
    AssistantTurnStateResponse? Turn,
    DateTimeOffset CreatedAtUtc);

// Status: "Processing", "Completed" or "Failed" (AssistantTurnStatuses). Attempt: from 1, +1 on every retry.
// FailureCode (Failed only): "assistant_unavailable" or "interrupted" (AssistantFailureCodes).
public sealed record AssistantTurnStateResponse(string Status, int Attempt, string? FailureCode);

// The conversation's messages in sequence order, after "afterSequence". NextAfterSequence continues the
// list (pass it as "afterSequence"); null when this page is the last one so far.
public sealed record AssistantMessagePageResponse(
    AssistantConversationResponse Conversation,
    IReadOnlyList<AssistantMessageResponse> Items,
    int? NextAfterSequence);

// The outcome of a send or retry: the user message with its turn, and the assistant's reply when the turn
// is Completed (never in F1). Replayed: the request had already been accepted; nothing new was stored
// except, possibly, a new attempt.
public sealed record AssistantTurnResponse(AssistantMessageResponse UserMessage, AssistantMessageResponse? Reply, bool Replayed);

public static class AssistantTurnStatuses
{
    public const string Processing = "Processing";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

public static class AssistantFailureCodes
{
    // No assistant could process the message (F1: no AI is integrated yet).
    public const string AssistantUnavailable = "assistant_unavailable";

    // The turn's processing did not finish within its lease.
    public const string Interrupted = "interrupted";
}

// The stable "code" of every Assistant problem response.
public static class AssistantErrorCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string ConversationNotFound = "conversation_not_found";
    public const string MessageNotFound = "message_not_found";
    public const string TurnInProgress = "turn_in_progress";
    public const string ClientMessageIdReused = "client_message_id_reused";
    public const string TurnSuperseded = "turn_superseded";
    public const string Busy = "assistant_busy";
}
