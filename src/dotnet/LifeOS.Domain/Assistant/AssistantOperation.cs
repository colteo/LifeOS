namespace LifeOS.Domain.Assistant;

// AI-007 F2: the lifecycle of an operation the assistant prepares for the user (AI-007 §9.3, §10.5-10.7):
//
//   Collecting ◀──────────▶ AwaitingConfirmation ──confirm──▶ Executed
//      │  │                    │  │
//      │  └──cancel──┐  ┌──────┘  └──expire──┐
//      │             ▼  ▼                    ▼
//      └──expire──▶ Cancelled             Expired
//
// Collecting and AwaitingConfirmation are the open states: the draft can still change, and the status
// follows its completeness (a revision that makes a complete draft incomplete goes back to Collecting).
// Executed, Cancelled and Expired are terminal: nothing changes afterwards. There is no Failed state: a
// confirmation that cannot be executed (e.g. the account was deleted meanwhile) changes nothing and is
// reported to the user as a correctable error, so the operation stays open.
//
// Versioning: DraftVersion starts at 1 and grows with every substantive revision; a revision equal to the
// current draft changes nothing (no new version, no new expiry). Readers and status changes never move it.
// Callers write a revision or a status change only if the stored version and status are still the ones
// they read (optimistic concurrency, enforced by the persistence layer).
//
// Expiry: an open operation expires DraftLifetime after its last substantive revision (the creation
// counts as one). The end is exclusive: from ExpiresAtUtc on it can no longer be revised or confirmed,
// only marked Expired (lazily, by whoever reads or writes it; there is no background worker).
public enum AssistantOperationStatus
{
    Collecting,
    AwaitingConfirmation,
    Executed,
    Cancelled,
    Expired
}

// The capability-specific content of an operation (v1: FinanceTransactionDraft). The lifecycle only needs
// to know whether it is complete; value equality decides whether a revision changes anything.
public interface IAssistantDraft
{
    bool IsComplete { get; }
}

public static class AssistantOperationKinds
{
    // Create one Expense or Income (AI-007 v1).
    public const string FinanceTransactionCreate = "finance.transaction.create";

    public static readonly IReadOnlyList<string> All = [FinanceTransactionCreate];

    public const int MaxLength = 64;
}

public sealed class AssistantOperation<TDraft> where TDraft : class, IAssistantDraft
{
    public static readonly TimeSpan DraftLifetime = TimeSpan.FromHours(24);

    public const int FirstDraftVersion = 1;

    private AssistantOperation() { }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public Guid ConversationId { get; private set; }

    public string Kind { get; private set; } = "";

    public AssistantOperationStatus Status { get; private set; }

    // 1, 2, 3, ...: +1 on every substantive revision of the draft.
    public int DraftVersion { get; private set; }

    // The schema version of the stored draft document.
    public int PayloadVersion { get; private set; }

    public TDraft Draft { get; private set; } = null!;

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    // Executed only: the transaction the confirmation created.
    public Guid? ResultTransactionId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    // The last change of any kind (revision or status).
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    // Executed only.
    public DateTimeOffset? ExecutedAtUtc { get; private set; }

    public bool IsOpen => IsOpenStatus(Status);

    public static bool IsOpenStatus(AssistantOperationStatus status) =>
        status is AssistantOperationStatus.Collecting or AssistantOperationStatus.AwaitingConfirmation;

    // Whether the operation is open but its draft has outlived DraftLifetime at `now`.
    public bool IsExpiredAt(DateTimeOffset now) => IsOpen && now >= ExpiresAtUtc;

    // The transitions of the state machine above. Open → open covers revisions (Collecting ⇄
    // AwaitingConfirmation, and a revision that keeps the status).
    public static bool CanTransition(AssistantOperationStatus from, AssistantOperationStatus to) => (from, to) switch
    {
        (AssistantOperationStatus.Collecting or AssistantOperationStatus.AwaitingConfirmation,
            AssistantOperationStatus.Collecting or AssistantOperationStatus.AwaitingConfirmation) => true,
        (AssistantOperationStatus.AwaitingConfirmation, AssistantOperationStatus.Executed) => true,
        (AssistantOperationStatus.Collecting or AssistantOperationStatus.AwaitingConfirmation,
            AssistantOperationStatus.Cancelled or AssistantOperationStatus.Expired) => true,
        _ => false
    };

    public static AssistantOperation<TDraft> Create(
        Guid userId,
        Guid conversationId,
        string kind,
        int payloadVersion,
        TDraft draft,
        DateTimeOffset now)
    {
        if (userId == Guid.Empty || conversationId == Guid.Empty)
        {
            throw new ArgumentException("A user id and a conversation id are required.");
        }

        ArgumentNullException.ThrowIfNull(draft);
        var utc = AssistantConversation.ToStoredPrecision(now);

        return new AssistantOperation<TDraft>
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            ConversationId = conversationId,
            Kind = ValidateKind(kind),
            Status = StatusFor(draft),
            DraftVersion = FirstDraftVersion,
            PayloadVersion = ValidatePayloadVersion(payloadVersion),
            Draft = draft,
            ExpiresAtUtc = utc + DraftLifetime,
            CreatedAtUtc = utc,
            UpdatedAtUtc = utc
        };
    }

    // Rebuilds a stored operation, checking the same invariants the database enforces.
    public static AssistantOperation<TDraft> Restore(
        Guid id,
        Guid userId,
        Guid conversationId,
        string kind,
        AssistantOperationStatus status,
        int draftVersion,
        int payloadVersion,
        TDraft draft,
        DateTimeOffset expiresAtUtc,
        Guid? resultTransactionId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? executedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (id == Guid.Empty || userId == Guid.Empty || conversationId == Guid.Empty || !Enum.IsDefined(status) || draftVersion < FirstDraftVersion)
        {
            throw new ArgumentException("The stored operation is not valid.");
        }

        var executed = status == AssistantOperationStatus.Executed;

        if (executed != resultTransactionId.HasValue || executed != executedAtUtc.HasValue
            || resultTransactionId == Guid.Empty || (status == AssistantOperationStatus.AwaitingConfirmation && !draft.IsComplete))
        {
            throw new ArgumentException("The stored operation's state is not consistent.");
        }

        return new AssistantOperation<TDraft>
        {
            Id = id,
            UserId = userId,
            ConversationId = conversationId,
            Kind = ValidateKind(kind),
            Status = status,
            DraftVersion = draftVersion,
            PayloadVersion = ValidatePayloadVersion(payloadVersion),
            Draft = draft,
            ExpiresAtUtc = expiresAtUtc.ToUniversalTime(),
            ResultTransactionId = resultTransactionId,
            CreatedAtUtc = createdAtUtc.ToUniversalTime(),
            UpdatedAtUtc = updatedAtUtc.ToUniversalTime(),
            ExecutedAtUtc = executedAtUtc?.ToUniversalTime()
        };
    }

    // Replaces the draft. Returns false, changing nothing, when the revision equals the current draft (it
    // neither creates a version nor extends the expiry). Otherwise: next version, expiry renewed from now,
    // status from the new draft's completeness. Throws when the operation is closed or has expired.
    public bool ReviseDraft(TDraft draft, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(draft);
        EnsureOpen(now);

        if (EqualityComparer<TDraft>.Default.Equals(Draft, draft))
        {
            return false;
        }

        if (DraftVersion == int.MaxValue)
        {
            throw new InvalidOperationException("The draft cannot be revised again.");
        }

        var utc = AssistantConversation.ToStoredPrecision(now);

        Draft = draft;
        DraftVersion++;
        Status = StatusFor(draft);
        ExpiresAtUtc = utc + DraftLifetime;
        UpdatedAtUtc = utc;

        return true;
    }

    // AwaitingConfirmation → Executed, recording the transaction the confirmation created.
    public void MarkExecuted(Guid transactionId, DateTimeOffset now)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("A transaction id is required.", nameof(transactionId));
        }

        EnsureOpen(now);

        if (Status != AssistantOperationStatus.AwaitingConfirmation || !Draft.IsComplete)
        {
            throw new InvalidOperationException("Only a complete draft awaiting confirmation can be executed.");
        }

        var utc = AssistantConversation.ToStoredPrecision(now);

        Status = AssistantOperationStatus.Executed;
        ResultTransactionId = transactionId;
        ExecutedAtUtc = utc;
        UpdatedAtUtc = utc;
    }

    // Open → Cancelled. An expired draft is Expired, not cancelled.
    public void Cancel(DateTimeOffset now)
    {
        EnsureOpen(now);

        Status = AssistantOperationStatus.Cancelled;
        UpdatedAtUtc = AssistantConversation.ToStoredPrecision(now);
    }

    // Lazy expiry: an open operation past its expiry becomes Expired. Returns false, changing nothing, for
    // any other operation.
    public bool ExpireIfDue(DateTimeOffset now)
    {
        if (!IsExpiredAt(now))
        {
            return false;
        }

        Status = AssistantOperationStatus.Expired;
        UpdatedAtUtc = AssistantConversation.ToStoredPrecision(now);

        return true;
    }

    private void EnsureOpen(DateTimeOffset now)
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException($"The operation is {Status}: it can no longer change.");
        }

        if (IsExpiredAt(now))
        {
            throw new InvalidOperationException("The draft has expired.");
        }
    }

    private static AssistantOperationStatus StatusFor(TDraft draft) =>
        draft.IsComplete ? AssistantOperationStatus.AwaitingConfirmation : AssistantOperationStatus.Collecting;

    private static string ValidateKind(string kind) =>
        AssistantOperationKinds.All.Contains(kind) ? kind : throw new ArgumentException("Unknown operation kind.", nameof(kind));

    private static int ValidatePayloadVersion(int payloadVersion) =>
        payloadVersion >= 1 ? payloadVersion : throw new ArgumentOutOfRangeException(nameof(payloadVersion), "A payload version starts at 1.");
}
