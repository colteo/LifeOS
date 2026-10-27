using LifeOS.Application.Assistant;
using LifeOS.Application.Users;
using LifeOS.Domain.Assistant;
using LifeOS.Domain.Finance.Assistant;
using FinanceOperation = LifeOS.Domain.Assistant.AssistantOperation<LifeOS.Domain.Finance.Assistant.FinanceTransactionDraft>;

namespace LifeOS.Application.Finance.Assistant;

// AI-007 F2: the user-facing use cases of the Finance capability. None of them writes Finance: preparing,
// reading, editing and cancelling a draft only touch assistant_operations. The only Finance write is the
// confirmation (ConfirmFinanceOperationHandler), through CreateTransactionHandler.
//
// Every write is conditional on the status and draft version it read; a lost race reads again and decides
// again, at most AssistantTurnPolicy.MaxWriteAttempts times (then Busy). Lazy expiry: whoever reads or
// writes an open operation past its expiry first marks it Expired (FinanceOperationExpiry).

// Opens a draft in a conversation from structured input. Missing fields are allowed (the draft is
// Collecting); present ones must be valid, and referenced accounts/categories must be the user's.
public sealed class CreateFinanceOperationHandler(
    IFinanceOperationRepository operations,
    IAssistantConversationRepository conversations,
    FinanceDraftReferences references,
    IUserRepository users,
    TimeProvider clock)
{
    public async Task<AssistantResult<FinanceOperationOutcome>> HandleAsync(Guid userId, Guid conversationId, FinanceDraftInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (await conversations.GetAsync(userId, conversationId, cancellationToken) is null)
        {
            return AssistantResult<FinanceOperationOutcome>.NotFound();
        }

        var now = clock.GetUtcNow();
        var timeZoneId = (await users.GetByIdAsync(userId, cancellationToken))?.TimeZoneId;
        FinanceTransactionDraft draft;

        try
        {
            draft = FinanceDraftBuilder.FromInput(input, timeZoneId, now);
        }
        catch (ArgumentException exception)
        {
            return AssistantResult<FinanceOperationOutcome>.Invalid(exception.ParamName ?? "request", exception);
        }

        if (await references.FirstProblemAsync(userId, draft, cancellationToken) is { } problem)
        {
            return FinanceOperationResults.Rejected<FinanceOperationOutcome>(problem.Code, problem.Field);
        }

        // AI-008: the insert lost to an open operation that is not a Finance one (another module's draft).
        var blockedByOtherKind = false;

        for (var attempt = 0; attempt < AssistantTurnPolicy.MaxWriteAttempts; attempt++)
        {
            if (await operations.GetOpenAsync(userId, conversationId, cancellationToken) is { } open)
            {
                var current = await FinanceOperationExpiry.ExpireIfDueAsync(operations, open, clock.GetUtcNow(), cancellationToken);

                if (current is { IsOpen: true })
                {
                    return AssistantResult<FinanceOperationOutcome>.Conflict(AssistantErrors.OperationAlreadyOpen,
                        "The conversation already has an open operation.",
                        new FinanceOperationOutcome(await references.ViewAsync(current, now, cancellationToken), Changed: false));
                }

                // It has just expired (or closed): the conversation is free.
                continue;
            }

            if (blockedByOtherKind)
            {
                return AssistantResult<FinanceOperationOutcome>.Conflict(AssistantErrors.OperationAlreadyOpen,
                    "The conversation already has an open operation.");
            }

            var operation = FinanceOperation.Create(userId, conversationId, AssistantOperationKinds.FinanceTransactionCreate,
                FinanceTransactionDraft.PayloadVersion, draft, now);

            switch (await operations.TryAddAsync(operation, cancellationToken))
            {
                case FinanceOperationAddOutcome.Added:
                    return AssistantResult<FinanceOperationOutcome>.Ok(
                        new FinanceOperationOutcome(await references.ViewAsync(operation, now, cancellationToken), Changed: true));

                case FinanceOperationAddOutcome.ConversationNotFound:
                    return AssistantResult<FinanceOperationOutcome>.NotFound();

                default:
                    // A concurrent request opened one first (or another module's draft is open): read it.
                    blockedByOtherKind = true;
                    continue;
            }
        }

        return AssistantResult<FinanceOperationOutcome>.Busy();
    }
}

// One operation of the user in any status, Executed included (with its transaction id). A read never
// renews the expiry; it only marks an outlived draft Expired.
public sealed class GetFinanceOperationHandler(IFinanceOperationRepository operations, FinanceDraftReferences references, TimeProvider clock)
{
    public async Task<AssistantResult<FinanceOperationView>> HandleAsync(Guid userId, Guid operationId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        if (await operations.GetAsync(userId, operationId, cancellationToken) is not { } stored
            || await FinanceOperationExpiry.ExpireIfDueAsync(operations, stored, now, cancellationToken) is not { } operation)
        {
            return AssistantResult<FinanceOperationView>.NotFound();
        }

        return AssistantResult<FinanceOperationView>.Ok(await references.ViewAsync(operation, now, cancellationToken));
    }
}

// The conversation's open operation, or Ok(null) when it has none (an outlived one is marked Expired and is
// no longer open). The caller checks that the conversation is the user's.
public sealed class GetActiveFinanceOperationHandler(IFinanceOperationRepository operations, FinanceDraftReferences references, TimeProvider clock)
{
    public async Task<AssistantResult<FinanceOperationView?>> HandleAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        if (await operations.GetOpenAsync(userId, conversationId, cancellationToken) is { } stored
            && await FinanceOperationExpiry.ExpireIfDueAsync(operations, stored, now, cancellationToken) is { IsOpen: true } operation)
        {
            return AssistantResult<FinanceOperationView?>.Ok(await references.ViewAsync(operation, now, cancellationToken));
        }

        return AssistantResult<FinanceOperationView?>.Ok(null);
    }
}

// Changes an open draft the user has seen at expectedVersion. A substantive change creates the next version,
// renews the expiry and recomputes the status (Collecting ⇄ AwaitingConfirmation); a patch that changes
// nothing returns the draft as it is (Changed = false), without a new version or expiry.
public sealed class UpdateFinanceDraftHandler(
    IFinanceOperationRepository operations,
    FinanceDraftReferences references,
    IUserRepository users,
    TimeProvider clock)
{
    public async Task<AssistantResult<FinanceOperationOutcome>> HandleAsync(Guid userId, Guid operationId, int expectedVersion,
        FinanceDraftPatch patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);

        if (FinanceOperationResults.InvalidVersion<FinanceOperationOutcome>(expectedVersion) is { } invalidVersion)
        {
            return invalidVersion;
        }

        if (InvalidPatch(patch) is { } invalidPatch)
        {
            return invalidPatch;
        }

        var timeZoneId = (await users.GetByIdAsync(userId, cancellationToken))?.TimeZoneId;

        for (var attempt = 0; attempt < AssistantTurnPolicy.MaxWriteAttempts; attempt++)
        {
            var now = clock.GetUtcNow();

            if (await operations.GetAsync(userId, operationId, cancellationToken) is not { } stored
                || await FinanceOperationExpiry.ExpireIfDueAsync(operations, stored, now, cancellationToken) is not { } operation)
            {
                return AssistantResult<FinanceOperationOutcome>.NotFound();
            }

            if ((FinanceOperationResults.Closed(operation) ?? FinanceOperationResults.StaleVersion(operation, expectedVersion)) is { } refusal)
            {
                return FinanceOperationResults.WithView(refusal, await references.ViewAsync(operation, now, cancellationToken));
            }

            FinanceTransactionDraft draft;

            try
            {
                draft = FinanceDraftBuilder.Apply(operation.Draft, patch, timeZoneId, now);
            }
            catch (ArgumentException exception)
            {
                return AssistantResult<FinanceOperationOutcome>.Invalid(exception.ParamName ?? "request", exception);
            }

            if (await references.FirstProblemAsync(userId, draft, cancellationToken) is { } problem)
            {
                return FinanceOperationResults.Rejected<FinanceOperationOutcome>(problem.Code, problem.Field);
            }

            var (status, version) = (operation.Status, operation.DraftVersion);

            if (!operation.ReviseDraft(draft, now))
            {
                return AssistantResult<FinanceOperationOutcome>.Ok(
                    new FinanceOperationOutcome(await references.ViewAsync(operation, now, cancellationToken), Changed: false));
            }

            if (await operations.TryUpdateAsync(operation, status, version, cancellationToken))
            {
                return AssistantResult<FinanceOperationOutcome>.Ok(
                    new FinanceOperationOutcome(await references.ViewAsync(operation, now, cancellationToken), Changed: true));
            }

            // Changed concurrently (edited, confirmed, cancelled or expired): decide again on what is stored.
        }

        return AssistantResult<FinanceOperationOutcome>.Busy();
    }

    private static AssistantResult<FinanceOperationOutcome>? InvalidPatch(FinanceDraftPatch patch)
    {
        var clear = patch.Clear ?? [];

        if (clear.FirstOrDefault(field => !FinanceDraftPatch.ClearableFields.Contains(field)) is { } unknown)
        {
            return AssistantResult<FinanceOperationOutcome>.Invalid("clear", $"'{unknown}' is not a field that can be cleared.");
        }

        (string Field, bool Set)[] fields =
        [
            (FinanceDraftFields.Type, patch.Type is not null), (FinanceDraftFields.Amount, patch.Amount is not null),
            (FinanceDraftFields.Currency, patch.Currency is not null), (FinanceDraftFields.Account, patch.AccountId is not null),
            (FinanceDraftFields.Category, patch.CategoryId is not null), (FinanceDraftFields.Note, patch.Note is not null),
            (FinanceDraftFields.OccurredOn, patch.OccurredOn is not null), (FinanceDraftFields.OccurredTime, patch.OccurredTime is not null)
        ];

        return fields.FirstOrDefault(field => field.Set && clear.Contains(field.Field)) is { Field: { } both }
            ? AssistantResult<FinanceOperationOutcome>.Invalid("clear", $"'{both}' cannot be both set and cleared.")
            : null;
    }
}

// Open → Cancelled, for the draft the user has seen at expectedVersion. Idempotent: cancelling a Cancelled
// operation returns it (Changed = false). An Executed or Expired operation cannot be cancelled. Finance is
// never touched.
public sealed class CancelFinanceOperationHandler(IFinanceOperationRepository operations, FinanceDraftReferences references, TimeProvider clock)
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

            if (operation.Status == AssistantOperationStatus.Cancelled)
            {
                return AssistantResult<FinanceOperationOutcome>.Ok(
                    new FinanceOperationOutcome(await references.ViewAsync(operation, now, cancellationToken), Changed: false));
            }

            if ((FinanceOperationResults.Closed(operation) ?? FinanceOperationResults.StaleVersion(operation, expectedVersion)) is { } refusal)
            {
                return FinanceOperationResults.WithView(refusal, await references.ViewAsync(operation, now, cancellationToken));
            }

            var (status, version) = (operation.Status, operation.DraftVersion);
            operation.Cancel(now);

            if (await operations.TryUpdateAsync(operation, status, version, cancellationToken))
            {
                return AssistantResult<FinanceOperationOutcome>.Ok(
                    new FinanceOperationOutcome(await references.ViewAsync(operation, now, cancellationToken), Changed: true));
            }
        }

        return AssistantResult<FinanceOperationOutcome>.Busy();
    }
}

// Lazy expiry (AI-007 F2 C.10, no worker): an open operation past its expiry is marked Expired by the first
// request that sees it. Returns what is stored afterwards (null if the operation was deleted meanwhile).
internal static class FinanceOperationExpiry
{
    public static async Task<FinanceOperation?> ExpireIfDueAsync(IFinanceOperationRepository operations, FinanceOperation operation,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (status, version) = (operation.Status, operation.DraftVersion);

        if (!operation.ExpireIfDue(now) || await operations.TryUpdateAsync(operation, status, version, cancellationToken))
        {
            return operation;
        }

        // Another request expired, confirmed or cancelled it first: whatever it stored.
        return await operations.GetAsync(operation.UserId, operation.Id, cancellationToken);
    }
}

// The refusals shared by the operation use cases. Messages are fixed texts.
internal static class FinanceOperationResults
{
    public static AssistantResult<T>? InvalidVersion<T>(int expectedVersion) =>
        expectedVersion < FinanceOperation.FirstDraftVersion
            ? AssistantResult<T>.Invalid("expectedVersion", "expectedVersion must be a draft version (from 1).")
            : null;

    // A terminal operation refuses every change.
    public static AssistantResult<FinanceOperationOutcome>? Closed(FinanceOperation operation) => operation.Status switch
    {
        AssistantOperationStatus.Executed => AssistantResult<FinanceOperationOutcome>.Conflict(AssistantErrors.OperationExecuted,
            "The operation has already been executed."),
        AssistantOperationStatus.Cancelled => AssistantResult<FinanceOperationOutcome>.Conflict(AssistantErrors.OperationCancelled,
            "The operation has been cancelled."),
        AssistantOperationStatus.Expired => AssistantResult<FinanceOperationOutcome>.Conflict(AssistantErrors.OperationExpired,
            "The draft has expired."),
        _ => null
    };

    public static AssistantResult<FinanceOperationOutcome>? StaleVersion(FinanceOperation operation, int expectedVersion) =>
        operation.DraftVersion != expectedVersion
            ? AssistantResult<FinanceOperationOutcome>.Conflict(AssistantErrors.VersionConflict, "The draft has changed since this version.")
            : null;

    // The refusal, carrying the operation as it is stored now.
    public static AssistantResult<FinanceOperationOutcome> WithView(AssistantResult<FinanceOperationOutcome> result, FinanceOperationView view) =>
        result with { Value = new FinanceOperationOutcome(view, Changed: false) };

    public static AssistantResult<T> Rejected<T>(string code, string field, T? value = default) =>
        AssistantResult<T>.Rejected(code, field, code switch
        {
            FinanceOperationErrors.AccountNotFound => "The account does not exist.",
            FinanceOperationErrors.CategoryNotFound => "The category does not exist.",
            FinanceOperationErrors.CategoryIncompatible => "The category's type does not match the transaction's.",
            FinanceOperationErrors.CurrencyMismatch => "The account's currency differs from the stated one.",
            AssistantErrors.DraftIncomplete => "The draft is not complete.",
            _ => "The transaction cannot be recorded as it is."
        }, value);
}
