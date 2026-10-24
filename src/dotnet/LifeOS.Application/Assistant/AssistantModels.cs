using LifeOS.Domain.Assistant;

namespace LifeOS.Application.Assistant;

public enum AssistantResultStatus
{
    Ok,

    // The request itself is wrong (Field and Message say why); repeating it cannot succeed.
    Invalid,

    // No such conversation or message for this user.
    NotFound,

    // Definitively refused in the current state (Code): repeating the same request cannot succeed.
    Conflict,

    // A turn of the conversation is still being processed (Value: that turn). Try again later.
    InProgress,

    // Concurrent writes kept winning (assistant_busy). Temporary: the same request may be repeated.
    Busy
}

// The outcome of an assistant use case. Messages are fixed texts; nothing from the database or EF Core
// reaches them.
public sealed record AssistantResult<T>(
    AssistantResultStatus Status,
    T? Value = default,
    string? Field = null,
    string? Code = null,
    string? Message = null)
{
    public static AssistantResult<T> Ok(T value) => new(AssistantResultStatus.Ok, value);

    public static AssistantResult<T> NotFound() => new(AssistantResultStatus.NotFound);

    public static AssistantResult<T> Invalid(string field, string message) => new(AssistantResultStatus.Invalid, Field: field, Message: message);

    public static AssistantResult<T> Invalid(string field, ArgumentException exception) => Invalid(field, AssistantErrors.Message(exception));

    public static AssistantResult<T> Conflict(string code, string message) => new(AssistantResultStatus.Conflict, Code: code, Message: message);

    public static AssistantResult<T> InProgress(T value) =>
        new(AssistantResultStatus.InProgress, value, Code: AssistantErrors.TurnInProgress, Message: "The assistant is still answering a message.");

    public static AssistantResult<T> Busy() =>
        new(AssistantResultStatus.Busy, Code: AssistantErrors.Busy, Message: "The conversation is busy. Try again.");
}

// Create replays return the stored conversation with Created = false.
public sealed record AssistantConversationResult(AssistantConversation Conversation, bool Created);

// Position in the conversation list: the last conversation of the previous page.
public sealed record AssistantConversationCursor(DateTimeOffset UpdatedAtUtc, Guid Id);

public sealed record AssistantMessagesPage(AssistantConversation Conversation, IReadOnlyList<AssistantMessage> Messages);

// A user message and its turn: Processing (UserMessage.TurnAttempts is the attempt to complete or fail),
// Completed (Reply is the answer) or Failed (UserMessage.FailureCode). Replayed: the request had already
// been accepted, nothing new was stored except, possibly, a lazy recovery or an automatic retry.
public sealed record AssistantTurn(AssistantMessage UserMessage, AssistantMessage? Reply, bool Replayed);

public static class AssistantTurnPolicy
{
    // AI-007 §12: the AI call (≤ 60 s including the AI service's own retries) plus a margin.
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(90);

    // Reads and decisions repeated after a lost optimistic write before giving up with Busy.
    public const int MaxWriteAttempts = 3;
}

public static class AssistantPaging
{
    public const int DefaultConversations = 20;
    public const int MaxConversations = 50;
    public const int DefaultMessages = 50;
    public const int MaxMessages = 200;
}

public static class AssistantErrors
{
    public const string TurnInProgress = "turn_in_progress";
    public const string Busy = "assistant_busy";

    // The client message id was already used in this conversation for another text.
    public const string ClientMessageIdReused = "client_message_id_reused";

    // The processing attempt no longer holds the turn (taken over, closed or its lease ended).
    public const string TurnNotHeld = "turn_not_held";

    // A later message was sent after this failed one; only the latest turn can be retried.
    public const string TurnSuperseded = "turn_superseded";

    // Failure code of a turn that no assistant could process (F1: no AI is integrated yet).
    public const string AssistantUnavailable = "assistant_unavailable";

    // ArgumentException appends " (Parameter '...')" to the message.
    internal static string Message(ArgumentException exception)
    {
        var index = exception.Message.IndexOf(" (Parameter '", StringComparison.Ordinal);

        return index < 0 ? exception.Message : exception.Message[..index];
    }
}
