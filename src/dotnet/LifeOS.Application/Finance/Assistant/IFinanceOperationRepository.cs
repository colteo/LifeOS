using LifeOS.Domain.Assistant;
using LifeOS.Domain.Finance.Assistant;
using FinanceOperation = LifeOS.Domain.Assistant.AssistantOperation<LifeOS.Domain.Finance.Assistant.FinanceTransactionDraft>;

namespace LifeOS.Application.Finance.Assistant;

public enum FinanceOperationAddOutcome
{
    Added,

    // The conversation already has an open operation; nothing was stored.
    OpenOperationExists,

    // No such conversation for this user (also: deleted meanwhile, or another user's); nothing was stored.
    ConversationNotFound
}

// AI-007 F2: the user's assistant operations of kind finance.transaction.create. Every method is scoped
// to userId: another user's operation (or conversation) is indistinguishable from a missing one.
//
// Writes are conditional: an update stores the operation only if the row still has the status and draft
// version the caller read, so of two concurrent writers based on the same state exactly one succeeds; the
// other gets false, nothing stored, and must read again. Reads return detached copies.
public interface IFinanceOperationRepository
{
    // Inserts a new open operation. The database allows at most one open operation per conversation.
    Task<FinanceOperationAddOutcome> TryAddAsync(FinanceOperation operation, CancellationToken cancellationToken);

    Task<FinanceOperation?> GetAsync(Guid userId, Guid operationId, CancellationToken cancellationToken);

    // The conversation's open (Collecting or AwaitingConfirmation) operation, whatever its expiry.
    Task<FinanceOperation?> GetOpenAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken);

    // Stores status, draft, versions, expiry, result and timestamps of `operation` if the stored row still
    // has expectedStatus and expectedDraftVersion. False, nothing written, otherwise (also when deleted).
    Task<bool> TryUpdateAsync(FinanceOperation operation, AssistantOperationStatus expectedStatus, int expectedDraftVersion,
        CancellationToken cancellationToken);

    // Inside a unit of work only: locks the operation's row until the transaction ends if it is
    // AwaitingConfirmation with expectedDraftVersion and not expired at `now`. A concurrent confirmation,
    // edit, cancellation or conversation delete waits for this transaction and then sees its outcome.
    Task<bool> TryLockForConfirmationAsync(Guid userId, Guid operationId, int expectedDraftVersion, DateTimeOffset now,
        CancellationToken cancellationToken);
}
