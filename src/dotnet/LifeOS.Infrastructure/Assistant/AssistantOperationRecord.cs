namespace LifeOS.Infrastructure.Assistant;

// The assistant_operations row (AI-007 F2 §9.3). A persistence record rather than the Domain entity: the
// draft is a versioned jsonb document whose reader depends on kind and payload_version (as for
// proposed_actions), so each capability's repository maps it to its own typed operation.
internal sealed class AssistantOperationRecord
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid ConversationId { get; set; }

    public string Kind { get; set; } = "";

    public string Status { get; set; } = "";

    public int DraftVersion { get; set; }

    public int PayloadVersion { get; set; }

    // jsonb document, written and read by the capability for Kind at PayloadVersion.
    public string Draft { get; set; } = "";

    public DateTimeOffset ExpiresAtUtc { get; set; }

    public Guid? ResultTransactionId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset? ExecutedAtUtc { get; set; }
}
