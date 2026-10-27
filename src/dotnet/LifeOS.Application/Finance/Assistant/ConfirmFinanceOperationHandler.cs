using LifeOS.Application.Assistant;
using LifeOS.Application.Finance.Transactions.CreateTransaction;
using LifeOS.Application.Persistence;
using LifeOS.Domain.Assistant;
using LifeOS.Domain.Finance.Assistant;
using FinanceOperation = LifeOS.Domain.Assistant.AssistantOperation<LifeOS.Domain.Finance.Assistant.FinanceTransactionDraft>;

namespace LifeOS.Application.Finance.Assistant;

// AI-007 F2: the human confirmation boundary. The user's explicit confirmation of the draft version they
// saw is the ONLY way an assistant operation reaches Finance, and it does so through the existing
// deterministic use case (CreateTransactionHandler), with a command built only from the stored, validated
// draft — never from anything a request or a model supplies at confirmation time.
//
// Atomicity (one database transaction, IUnitOfWork):
//   1. lock the operation's row, if it is still AwaitingConfirmation at expectedVersion and not expired;
//   2. CreateTransactionHandler: user-scoped account/category checks, the Domain factory, one insert;
//   3. AwaitingConfirmation → Executed with the new transaction's id (conditional update).
// Either all three commit or nothing does. The row lock serialises everything else that could change the
// operation (another confirmation, an edit, a cancellation, an expiry, the deletion of its conversation):
// those wait for this transaction and then see Executed, so at most one transaction exists per operation
// (the unique result_id is the backstop).
//
// Idempotent: confirming an Executed operation returns it with the same transaction id (Changed = false),
// also for a request that lost the race against the confirmation that executed it. A refusal by
// CreateTransactionHandler (e.g. the account was deleted after the draft was prepared) rolls everything back
// and leaves the operation unchanged and open: the user corrects the draft and confirms again.
public sealed class ConfirmFinanceOperationHandler(
    IFinanceOperationRepository operations,
    FinanceDraftReferences references,
    CreateTransactionHandler createTransaction,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
{
    public async Task<AssistantResult<FinanceOperationOutcome>> HandleAsync(Guid userId, Guid operationId, int expectedVersion,
        CancellationToken cancellationToken)
    {
        if (FinanceOperationResults.InvalidVersion<FinanceOperationOutcome>(expectedVersion) is { } invalidVersion)
        {
            return invalidVersion;
        }

        for (var attempt = 0; attempt < AssistantTurnPolicy.MaxWriteAttempts; attempt++)
        {
            var now = clock.GetUtcNow();

            if (await operations.GetAsync(userId, operationId, cancellationToken) is not { } stored
                || await FinanceOperationExpiry.ExpireIfDueAsync(operations, stored, now, cancellationToken) is not { } operation)
            {
                return AssistantResult<FinanceOperationOutcome>.NotFound();
            }

            // Already confirmed (by this request's earlier attempt, a retry or a concurrent request).
            if (operation.Status == AssistantOperationStatus.Executed)
            {
                return AssistantResult<FinanceOperationOutcome>.Ok(
                    new FinanceOperationOutcome(await references.ViewAsync(operation, now, cancellationToken), Changed: false));
            }

            if ((FinanceOperationResults.Closed(operation) ?? FinanceOperationResults.StaleVersion(operation, expectedVersion)) is { } refusal)
            {
                return FinanceOperationResults.WithView(refusal, await references.ViewAsync(operation, now, cancellationToken));
            }

            if (operation.Status != AssistantOperationStatus.AwaitingConfirmation)
            {
                return FinanceOperationResults.Rejected(AssistantErrors.DraftIncomplete, operation.Draft.MissingFields[0],
                    new FinanceOperationOutcome(await references.ViewAsync(operation, now, cancellationToken), Changed: false));
            }

            // The one rule CreateTransactionHandler cannot know: the currency the user stated.
            if (await references.CurrencyProblemAsync(userId, operation.Draft, cancellationToken) is { } currencyProblem)
            {
                return FinanceOperationResults.Rejected<FinanceOperationOutcome>(currencyProblem, FinanceDraftFields.Account);
            }

            var outcome = await ExecuteAsync(operation, now, cancellationToken);

            if (outcome is not null)
            {
                return outcome;
            }

            // Lost the row to a concurrent change (confirmed, edited, cancelled, expired or deleted): decide
            // again on what is stored now.
        }

        return AssistantResult<FinanceOperationOutcome>.Busy();
    }

    // Null when the operation was no longer AwaitingConfirmation at its version (nothing written).
    private async Task<AssistantResult<FinanceOperationOutcome>?> ExecuteAsync(FinanceOperation operation, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var draft = operation.Draft;
        var occurrence = draft.Occurrence!;
        var version = operation.DraftVersion;
        CreateTransactionResult? refused = null;

        var command = new CreateTransactionCommand(draft.Type!.Value, draft.Amount!.Value, draft.AccountId, SourceAccountId: null,
            DestinationAccountId: null, draft.CategoryId, occurrence.OccurredAtUtc, draft.Note);

        var committed = await unitOfWork.TryInTransactionAsync(async ct =>
        {
            if (!await operations.TryLockForConfirmationAsync(operation.UserId, operation.Id, version, now, ct))
            {
                return false;
            }

            var created = await createTransaction.HandleAsync(operation.UserId, command, ct);

            if (created.Status != CreateTransactionStatus.Created)
            {
                // Rolls back everything, including anything the handler may have attempted.
                refused = created;
                return false;
            }

            operation.MarkExecuted(created.Transaction!.Id, now);

            // Under the row lock this cannot lose; if it ever did, nothing commits.
            return await operations.TryUpdateAsync(operation, AssistantOperationStatus.AwaitingConfirmation, version, ct);
        }, cancellationToken);

        if (committed)
        {
            return AssistantResult<FinanceOperationOutcome>.Ok(
                new FinanceOperationOutcome(await references.ViewAsync(operation, now, cancellationToken), Changed: true));
        }

        return refused is null ? null : Refusal(refused);
    }

    // CreateTransactionHandler's refusal as a correctable draft problem. Its own messages (which contain
    // ids) are not passed on.
    private static AssistantResult<FinanceOperationOutcome> Refusal(CreateTransactionResult result)
    {
        var code = (result.Status, result.Field) switch
        {
            (CreateTransactionStatus.NotFound, FinanceDraftFields.Account) => FinanceOperationErrors.AccountNotFound,
            (CreateTransactionStatus.NotFound, FinanceDraftFields.Category) => FinanceOperationErrors.CategoryNotFound,
            (CreateTransactionStatus.Invalid, FinanceDraftFields.Category) => FinanceOperationErrors.CategoryIncompatible,
            _ => FinanceOperationErrors.TransactionRejected
        };

        return FinanceOperationResults.Rejected<FinanceOperationOutcome>(code, result.Field ?? "request");
    }
}
