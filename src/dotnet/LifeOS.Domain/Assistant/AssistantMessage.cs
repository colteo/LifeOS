namespace LifeOS.Domain.Assistant;

public enum AssistantMessageRole
{
    User,
    Assistant
}

// The turn a user message opens (AI-007 §10.1, §10.8):
//
//   Processing ──complete──▶ Completed
//      │  ▲
//      │  └────retry────┐
//      └──fail───────▶ Failed
//
// Processing carries a lease: the instant from which the turn counts as interrupted (its process died
// or hangs) and lazy recovery may fail it with Interrupted. Completed is final. Every retry starts a
// new attempt; the attempt number is the fencing token. Only the holder of the current attempt may
// complete or fail the turn, and only before its lease ends (the end is exclusive: at that instant
// recovery may already take the turn over), so neither a process whose attempt was taken over nor one
// that outlived its lease can write.
public enum AssistantTurnStatus
{
    Processing,
    Completed,
    Failed
}

// Who asked for a retry. Automatic retries stop after MaxAutomaticAttempts; a retry the user explicitly
// asks for is never refused for the number of earlier attempts.
public enum AssistantTurnRetryMode
{
    Automatic,
    Manual
}

// AI-007 F1: one message of a conversation. User messages carry the client's idempotency key and the
// state of their turn; assistant messages carry the user message they answer. Content is immutable.
public sealed class AssistantMessage
{
    public const int MaxUserContentLength = 1000;
    public const int MaxAssistantContentLength = 4000;
    public const int MaxFailureCodeLength = 64;
    public const int MaxAutomaticAttempts = 3;

    // Failure code of a turn whose lease expired before it completed.
    public const string Interrupted = "interrupted";

    private AssistantMessage() { }

    public Guid Id { get; private set; }

    public Guid ConversationId { get; private set; }

    // The conversation owner, stored on every message; the database ties it to the conversation's owner.
    public Guid UserId { get; private set; }

    // Position in the conversation (1, 2, 3, ...), unique per conversation.
    public int Sequence { get; private set; }

    public AssistantMessageRole Role { get; private set; }

    public string Content { get; private set; } = "";

    // User messages only: the client's idempotency key, unique per conversation.
    public Guid? ClientMessageId { get; private set; }

    // Assistant messages only: the user message whose turn this message completes.
    public Guid? ReplyToMessageId { get; private set; }

    // User messages only.
    public AssistantTurnStatus? TurnStatus { get; private set; }

    // User messages only: the current attempt, from 1.
    public int? TurnAttempts { get; private set; }

    // Processing only.
    public DateTimeOffset? TurnLeaseUntilUtc { get; private set; }

    // Failed only: a stable code (e.g. Interrupted), never exception or provider text.
    public string? FailureCode { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public bool CanRetryAutomatically => TurnStatus == AssistantTurnStatus.Failed && TurnAttempts < MaxAutomaticAttempts;

    // Whether `attempt` still holds the turn at `now`: Processing, that attempt, and before the lease end.
    public bool IsHeldBy(int attempt, DateTimeOffset now) =>
        TurnStatus == AssistantTurnStatus.Processing && TurnAttempts == attempt && now < TurnLeaseUntilUtc;

    internal static AssistantMessage CreateUserMessage(
        Guid conversationId,
        Guid userId,
        int sequence,
        Guid clientMessageId,
        string content,
        DateTimeOffset now,
        DateTimeOffset leaseUntilUtc)
    {
        if (clientMessageId == Guid.Empty)
        {
            throw new ArgumentException("A client message id is required.", nameof(clientMessageId));
        }

        var text = NormalizeContent(content, MaxUserContentLength, nameof(content));
        var lease = Lease(now, leaseUntilUtc);

        return new AssistantMessage
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversationId,
            UserId = userId,
            Sequence = sequence,
            Role = AssistantMessageRole.User,
            Content = text,
            ClientMessageId = clientMessageId,
            TurnStatus = AssistantTurnStatus.Processing,
            TurnAttempts = 1,
            TurnLeaseUntilUtc = lease,
            CreatedAtUtc = AssistantConversation.ToStoredPrecision(now)
        };
    }

    internal static AssistantMessage CreateAssistantMessage(
        Guid conversationId,
        Guid userId,
        int sequence,
        Guid replyToMessageId,
        string content,
        DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversationId,
            UserId = userId,
            Sequence = sequence,
            Role = AssistantMessageRole.Assistant,
            Content = NormalizeContent(content, MaxAssistantContentLength, nameof(content)),
            ReplyToMessageId = replyToMessageId,
            CreatedAtUtc = AssistantConversation.ToStoredPrecision(now)
        };

    // Called by AssistantConversation.CompleteTurn, which also appends the reply.
    internal void CompleteTurn(int attempt, DateTimeOffset now)
    {
        EnsureHeldBy(attempt, now);

        TurnStatus = AssistantTurnStatus.Completed;
        TurnLeaseUntilUtc = null;
    }

    public void FailTurn(int attempt, string failureCode, DateTimeOffset now)
    {
        var code = NormalizeFailureCode(failureCode);
        EnsureHeldBy(attempt, now);

        TurnStatus = AssistantTurnStatus.Failed;
        TurnLeaseUntilUtc = null;
        FailureCode = code;
    }

    // Lazy recovery: a Processing turn whose lease has expired becomes Failed/Interrupted. Returns false,
    // and changes nothing, for any other turn.
    public bool InterruptIfLeaseExpired(DateTimeOffset now)
    {
        if (TurnStatus != AssistantTurnStatus.Processing || TurnLeaseUntilUtc > now)
        {
            return false;
        }

        TurnStatus = AssistantTurnStatus.Failed;
        TurnLeaseUntilUtc = null;
        FailureCode = Interrupted;

        return true;
    }

    // Starts the next attempt of a Failed turn and returns its number (the new fencing token).
    public int RetryTurn(AssistantTurnRetryMode mode, DateTimeOffset now, DateTimeOffset leaseUntilUtc)
    {
        if (TurnStatus != AssistantTurnStatus.Failed)
        {
            throw new InvalidOperationException($"Only a failed turn can be retried (this one is {TurnStatus?.ToString() ?? "not a turn"}).");
        }

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown retry mode.");
        }

        if (mode == AssistantTurnRetryMode.Automatic && !CanRetryAutomatically)
        {
            throw new InvalidOperationException($"A turn is retried automatically at most until attempt {MaxAutomaticAttempts}.");
        }

        if (TurnAttempts == int.MaxValue)
        {
            throw new InvalidOperationException("The turn cannot be retried again.");
        }

        var lease = Lease(now, leaseUntilUtc);

        TurnStatus = AssistantTurnStatus.Processing;
        TurnAttempts++;
        TurnLeaseUntilUtc = lease;
        FailureCode = null;

        return TurnAttempts!.Value;
    }

    // Non-blank text within the limit, trimmed. NUL is rejected (PostgreSQL text cannot store it); line
    // breaks and other characters are kept as written.
    private static string NormalizeContent(string? content, int maxLength, string field)
    {
        var text = content?.Trim();

        if (string.IsNullOrEmpty(text) || text.Length > maxLength)
        {
            throw new ArgumentException($"A message must have 1–{maxLength} characters.", field);
        }

        if (text.Contains('\0'))
        {
            throw new ArgumentException("A message cannot contain NUL characters.", field);
        }

        return text;
    }

    private void EnsureHeldBy(int attempt, DateTimeOffset now)
    {
        if (!IsHeldBy(attempt, now))
        {
            throw new InvalidOperationException("The turn is no longer held by this attempt (taken over, closed or its lease has ended).");
        }
    }

    private static DateTimeOffset Lease(DateTimeOffset now, DateTimeOffset leaseUntilUtc)
    {
        var lease = AssistantConversation.ToStoredPrecision(leaseUntilUtc);

        if (lease <= now)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseUntilUtc), "A turn lease must end after now.");
        }

        return lease;
    }

    // A stable code: lowercase letters, digits and underscores.
    private static string NormalizeFailureCode(string? failureCode)
    {
        if (string.IsNullOrEmpty(failureCode)
            || failureCode.Length > MaxFailureCodeLength
            || !failureCode.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
        {
            throw new ArgumentException($"A failure code is 1–{MaxFailureCodeLength} characters of a-z, 0-9 and _.", nameof(failureCode));
        }

        return failureCode;
    }
}
