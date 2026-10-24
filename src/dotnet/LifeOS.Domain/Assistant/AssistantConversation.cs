namespace LifeOS.Domain.Assistant;

// AI-007 F1: one conversation of LifeOS Assistant, owned by one user. A user has any number of them.
//
// Ordering: every message of a conversation gets the next value of NextSequence (1, 2, 3, ...). The
// sequence is the only ordering key; timestamps never decide the order. Allocating a sequence changes
// this row, and the persistence layer saves NextSequence with an optimistic check, so two writers that
// read the same value cannot both store a message (the unique (conversation, sequence) index is the
// backstop).
//
// Messages are not loaded with the conversation: it hands out sequences and creates the messages, the
// caller stores them.
public sealed class AssistantConversation
{
    public const int FirstSequence = 1;

    private AssistantConversation() { }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    // The client's idempotency key for the create request, unique per user (AI-007 §11).
    public Guid ClientConversationId { get; private set; }

    // The sequence the next message will get.
    public int NextSequence { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    // The time of the last message (or the creation, while there is none).
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static AssistantConversation Create(Guid userId, Guid clientConversationId, DateTimeOffset now)
    {
        if (userId == Guid.Empty || clientConversationId == Guid.Empty)
        {
            throw new ArgumentException("A user id and a client conversation id are required.");
        }

        var utc = ToStoredPrecision(now);

        return new AssistantConversation
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            ClientConversationId = clientConversationId,
            NextSequence = FirstSequence,
            CreatedAtUtc = utc,
            UpdatedAtUtc = utc
        };
    }

    // Accepts the user's message and opens its turn (Processing, attempt 1, leased until leaseUntilUtc).
    public AssistantMessage AppendUserMessage(Guid clientMessageId, string content, DateTimeOffset now, DateTimeOffset leaseUntilUtc)
    {
        var message = AssistantMessage.CreateUserMessage(Id, UserId, PeekSequence(), clientMessageId, content, now, leaseUntilUtc);
        Advance(now);

        return message;
    }

    // Completes the turn of userMessage, if attempt `attempt` still holds it at `now` (see
    // AssistantMessage.IsHeldBy), and returns the assistant's reply. Throws InvalidOperationException when
    // the turn was taken over, is already closed or its lease has ended, or when a later message was
    // appended (a retry that raced a new message): the reply always directly follows the message it answers.
    public AssistantMessage CompleteTurn(AssistantMessage userMessage, int attempt, string replyContent, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(userMessage);

        if (userMessage.ConversationId != Id || userMessage.UserId != UserId)
        {
            throw new ArgumentException("The message belongs to another conversation.", nameof(userMessage));
        }

        if (!IsLastMessage(userMessage))
        {
            throw new InvalidOperationException("A later message was appended: the reply would not follow the message it answers.");
        }

        // Everything is validated before any state changes, so a rejected reply leaves both rows untouched.
        var reply = AssistantMessage.CreateAssistantMessage(Id, UserId, PeekSequence(), userMessage.Id, replyContent, now);
        userMessage.CompleteTurn(attempt, now);
        Advance(now);

        return reply;
    }

    // Whether nothing was appended after `message` (the sequences are consecutive).
    public bool IsLastMessage(AssistantMessage message) => message.ConversationId == Id && message.Sequence == NextSequence - 1;

    // The sequence of the message being created; NextSequence moves only once that message is valid.
    private int PeekSequence() =>
        NextSequence < int.MaxValue ? NextSequence : throw new InvalidOperationException("The conversation cannot hold more messages.");

    private void Advance(DateTimeOffset now)
    {
        NextSequence++;
        UpdatedAtUtc = ToStoredPrecision(now);
    }

    // UTC, truncated to whole microseconds (PostgreSQL's precision), so a returned entity and the stored row
    // carry the same instants.
    public static DateTimeOffset ToStoredPrecision(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();

        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMicrosecond));
    }
}
