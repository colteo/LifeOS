using LifeOS.Application.Persistence;
using LifeOS.Domain.Assistant;

namespace LifeOS.Application.Assistant;

// AI-008: the operation lifecycle shared by the assistant's modules (Hydration and Meals; Finance keeps its AI-007
// F2 handlers, which follow the same rules). A module brings its typed draft, a policy (what blocks a
// confirmation, how a confirmed draft is executed through the module's existing use case) and its own builders;
// creating, reading, editing, cancelling, expiring and confirming an operation is the same for every kind:
//   - every write is conditional on the status and draft version it read; a lost race reads again and decides
//     again, at most AssistantTurnPolicy.MaxWriteAttempts times (then Busy);
//   - lazy expiry: whoever reads or writes an open operation past its expiry first marks it Expired;
//   - one open operation per conversation, whatever its kind (the partial unique index; IAssistantOperationReader
//     sees the open one of any kind);
//   - the confirmation is the only path to the module: lock the row (AwaitingConfirmation at the expected version,
//     not expired), execute the stored draft, mark Executed with the record's id — one unit of work, all or
//     nothing; idempotent (an Executed operation is returned with the same result id).

public enum AssistantOperationAddOutcome
{
    Added,

    // The conversation already has an open operation (of any kind); nothing was stored.
    OpenOperationExists,

    // No such conversation for this user (also: deleted meanwhile, or another user's); nothing was stored.
    ConversationNotFound
}

// The user's assistant operations of one kind (TDraft). The same contract as IFinanceOperationRepository: every
// method is scoped to userId, every write is conditional, reads return detached copies.
public interface IAssistantOperationRepository<TDraft> where TDraft : class, IAssistantDraft
{
    // Inserts a new open operation. The database allows at most one open operation per conversation.
    Task<AssistantOperationAddOutcome> TryAddAsync(AssistantOperation<TDraft> operation, CancellationToken cancellationToken);

    Task<AssistantOperation<TDraft>?> GetAsync(Guid userId, Guid operationId, CancellationToken cancellationToken);

    // The conversation's open operation of this kind, whatever its expiry.
    Task<AssistantOperation<TDraft>?> GetOpenAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken);

    // Stores the operation if the stored row still has expectedStatus and expectedDraftVersion.
    Task<bool> TryUpdateAsync(AssistantOperation<TDraft> operation, AssistantOperationStatus expectedStatus, int expectedDraftVersion,
        CancellationToken cancellationToken);

    // Inside a unit of work only: locks the row until the transaction ends if it is AwaitingConfirmation with
    // expectedDraftVersion and not expired at `now`.
    Task<bool> TryLockForConfirmationAsync(Guid userId, Guid operationId, int expectedDraftVersion, DateTimeOffset now,
        CancellationToken cancellationToken);
}

// What every kind has in common, without reading its draft.
public sealed record AssistantOperationHeader(Guid Id, string Kind, AssistantOperationStatus Status, int DraftVersion, DateTimeOffset ExpiresAtUtc)
{
    public bool IsOpen => AssistantOperation<IAssistantDraft>.IsOpenStatus(Status);

    public bool IsExpiredAt(DateTimeOffset now) => IsOpen && now >= ExpiresAtUtc;
}

// Kind-agnostic reads of the user's operations: which kind an operation is (to route a request to its module) and
// which operation is open in a conversation, whatever its kind. Scoped to userId like every assistant read.
public interface IAssistantOperationReader
{
    Task<AssistantOperationHeader?> GetAsync(Guid userId, Guid operationId, CancellationToken cancellationToken);

    // The conversation's open (Collecting or AwaitingConfirmation) operation of any kind, whatever its expiry.
    Task<AssistantOperationHeader?> GetOpenAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken);

    // Lazy expiry of an operation of any kind: Expired if the stored row still has expectedStatus and
    // expectedDraftVersion and has outlived its expiry at `now` (only the status changes).
    Task<bool> TryExpireAsync(Guid userId, Guid operationId, AssistantOperationStatus expectedStatus, int expectedDraftVersion, DateTimeOffset now,
        CancellationToken cancellationToken);
}

// The outcome of executing a confirmed draft through the module's use case: the created record's id, or a stable
// refusal code (and the draft field it concerns) when the use case refused it — then nothing is kept.
public sealed record AssistantExecution(Guid? ResultId, string? RefusalCode = null, string? Field = null)
{
    public static AssistantExecution Created(Guid resultId) => new(resultId);

    public static AssistantExecution Refused(string code, string? field) => new(null, code, field);
}

// The module's rules for its drafts.
public interface IAssistantDraftPolicy<TDraft> where TDraft : class, IAssistantDraft
{
    string Kind { get; }

    int PayloadVersion { get; }

    // The draft fields still missing, in display order (the first one is asked).
    IReadOnlyList<string> MissingFields(TDraft draft);

    // Problems that block a confirmation now (stable codes), e.g. a water intake on a future day.
    IReadOnlyList<string> Issues(TDraft draft, DateTimeOffset now);

    bool OccursInFuture(TDraft draft, DateTimeOffset now);

    // Called inside the confirmation's unit of work, after the row lock: writes the module's record through its
    // existing use case, from the stored draft only.
    Task<AssistantExecution> ExecuteAsync(Guid userId, TDraft draft, CancellationToken cancellationToken);
}

// An operation with what the user needs to review it: the problems that block a confirmation now (open operations
// only), whether the occurrence lies in the future, and whether a confirmation would be attempted.
public sealed record AssistantOperationView<TDraft>(
    AssistantOperation<TDraft> Operation,
    IReadOnlyList<string> MissingFields,
    IReadOnlyList<string> Issues,
    bool OccursInFuture,
    bool CanConfirm) where TDraft : class, IAssistantDraft;

// Changed: something was stored by this request.
public sealed record AssistantOperationOutcome<TDraft>(AssistantOperationView<TDraft> View, bool Changed) where TDraft : class, IAssistantDraft;

public sealed class AssistantOperationLifecycle<TDraft>(
    IAssistantOperationRepository<TDraft> operations,
    IAssistantOperationReader reader,
    IAssistantConversationRepository conversations,
    IAssistantDraftPolicy<TDraft> policy,
    IUnitOfWork unitOfWork,
    TimeProvider clock) where TDraft : class, IAssistantDraft
{
    // Opens a draft in a conversation. The conversation must be the user's and have no open operation of any kind.
    public async Task<AssistantResult<AssistantOperationOutcome<TDraft>>> CreateAsync(Guid userId, Guid conversationId, TDraft draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (await conversations.GetAsync(userId, conversationId, cancellationToken) is null)
        {
            return AssistantResult<AssistantOperationOutcome<TDraft>>.NotFound();
        }

        for (var attempt = 0; attempt < AssistantTurnPolicy.MaxWriteAttempts; attempt++)
        {
            var now = clock.GetUtcNow();

            if (await reader.GetOpenAsync(userId, conversationId, cancellationToken) is { } open)
            {
                if (open.IsExpiredAt(now))
                {
                    // It has just expired: the conversation is free (whoever marks it first).
                    await reader.TryExpireAsync(userId, open.Id, open.Status, open.DraftVersion, now, cancellationToken);
                    continue;
                }

                var current = open.Kind == policy.Kind ? await operations.GetAsync(userId, open.Id, cancellationToken) : null;

                return AssistantResult<AssistantOperationOutcome<TDraft>>.Conflict(AssistantErrors.OperationAlreadyOpen,
                    "The conversation already has an open operation.",
                    current is null ? null! : new AssistantOperationOutcome<TDraft>(View(current, now), Changed: false));
            }

            var operation = AssistantOperation<TDraft>.Create(userId, conversationId, policy.Kind, policy.PayloadVersion, draft, now);

            switch (await operations.TryAddAsync(operation, cancellationToken))
            {
                case AssistantOperationAddOutcome.Added:
                    return AssistantResult<AssistantOperationOutcome<TDraft>>.Ok(new AssistantOperationOutcome<TDraft>(View(operation, now), Changed: true));

                case AssistantOperationAddOutcome.ConversationNotFound:
                    return AssistantResult<AssistantOperationOutcome<TDraft>>.NotFound();

                default:
                    // A concurrent request opened one first: read it.
                    continue;
            }
        }

        return AssistantResult<AssistantOperationOutcome<TDraft>>.Busy();
    }

    // One operation of the user in any status. A read never renews the expiry; it only marks an outlived draft Expired.
    public async Task<AssistantResult<AssistantOperationView<TDraft>>> GetAsync(Guid userId, Guid operationId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        if (await operations.GetAsync(userId, operationId, cancellationToken) is not { } stored
            || await ExpireIfDueAsync(stored, now, cancellationToken) is not { } operation)
        {
            return AssistantResult<AssistantOperationView<TDraft>>.NotFound();
        }

        return AssistantResult<AssistantOperationView<TDraft>>.Ok(View(operation, now));
    }

    // The conversation's open operation of this kind, or Ok(null).
    public async Task<AssistantResult<AssistantOperationView<TDraft>?>> GetActiveAsync(Guid userId, Guid conversationId,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        if (await operations.GetOpenAsync(userId, conversationId, cancellationToken) is { } stored
            && await ExpireIfDueAsync(stored, now, cancellationToken) is { IsOpen: true } operation)
        {
            return AssistantResult<AssistantOperationView<TDraft>?>.Ok(View(operation, now));
        }

        return AssistantResult<AssistantOperationView<TDraft>?>.Ok(null);
    }

    // Changes an open draft the user has seen at expectedVersion. `revise` builds the new draft from the stored one
    // at `now` (ArgumentException, ParamName = the field: 400). A revision equal to the draft changes nothing.
    public async Task<AssistantResult<AssistantOperationOutcome<TDraft>>> UpdateAsync(Guid userId, Guid operationId, int expectedVersion,
        Func<TDraft, DateTimeOffset, TDraft> revise, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revise);

        if (InvalidVersion<AssistantOperationOutcome<TDraft>>(expectedVersion) is { } invalidVersion)
        {
            return invalidVersion;
        }

        for (var attempt = 0; attempt < AssistantTurnPolicy.MaxWriteAttempts; attempt++)
        {
            var now = clock.GetUtcNow();

            if (await operations.GetAsync(userId, operationId, cancellationToken) is not { } stored
                || await ExpireIfDueAsync(stored, now, cancellationToken) is not { } operation)
            {
                return AssistantResult<AssistantOperationOutcome<TDraft>>.NotFound();
            }

            if ((Closed(operation) ?? StaleVersion(operation, expectedVersion)) is { } refusal)
            {
                return WithView(refusal, View(operation, now));
            }

            TDraft draft;

            try
            {
                draft = revise(operation.Draft, now);
            }
            catch (ArgumentException exception)
            {
                return AssistantResult<AssistantOperationOutcome<TDraft>>.Invalid(exception.ParamName ?? "request", exception);
            }

            var (status, version) = (operation.Status, operation.DraftVersion);

            if (!operation.ReviseDraft(draft, now))
            {
                return AssistantResult<AssistantOperationOutcome<TDraft>>.Ok(new AssistantOperationOutcome<TDraft>(View(operation, now), Changed: false));
            }

            if (await operations.TryUpdateAsync(operation, status, version, cancellationToken))
            {
                return AssistantResult<AssistantOperationOutcome<TDraft>>.Ok(new AssistantOperationOutcome<TDraft>(View(operation, now), Changed: true));
            }

            // Changed concurrently (edited, confirmed, cancelled or expired): decide again on what is stored.
        }

        return AssistantResult<AssistantOperationOutcome<TDraft>>.Busy();
    }

    // Open → Cancelled for the version the user saw. Idempotent for a Cancelled operation; the module is never touched.
    public async Task<AssistantResult<AssistantOperationOutcome<TDraft>>> CancelAsync(Guid userId, Guid operationId, int expectedVersion,
        CancellationToken cancellationToken)
    {
        if (InvalidVersion<AssistantOperationOutcome<TDraft>>(expectedVersion) is { } invalidVersion)
        {
            return invalidVersion;
        }

        for (var attempt = 0; attempt < AssistantTurnPolicy.MaxWriteAttempts; attempt++)
        {
            var now = clock.GetUtcNow();

            if (await operations.GetAsync(userId, operationId, cancellationToken) is not { } stored
                || await ExpireIfDueAsync(stored, now, cancellationToken) is not { } operation)
            {
                return AssistantResult<AssistantOperationOutcome<TDraft>>.NotFound();
            }

            if (operation.Status == AssistantOperationStatus.Cancelled)
            {
                return AssistantResult<AssistantOperationOutcome<TDraft>>.Ok(new AssistantOperationOutcome<TDraft>(View(operation, now), Changed: false));
            }

            if ((Closed(operation) ?? StaleVersion(operation, expectedVersion)) is { } refusal)
            {
                return WithView(refusal, View(operation, now));
            }

            var (status, version) = (operation.Status, operation.DraftVersion);
            operation.Cancel(now);

            if (await operations.TryUpdateAsync(operation, status, version, cancellationToken))
            {
                return AssistantResult<AssistantOperationOutcome<TDraft>>.Ok(new AssistantOperationOutcome<TDraft>(View(operation, now), Changed: true));
            }
        }

        return AssistantResult<AssistantOperationOutcome<TDraft>>.Busy();
    }

    // The human confirmation boundary: the user's explicit confirmation of the version they saw is the only way an
    // operation reaches its module, through the policy's execution of the stored draft.
    public async Task<AssistantResult<AssistantOperationOutcome<TDraft>>> ConfirmAsync(Guid userId, Guid operationId, int expectedVersion,
        CancellationToken cancellationToken)
    {
        if (InvalidVersion<AssistantOperationOutcome<TDraft>>(expectedVersion) is { } invalidVersion)
        {
            return invalidVersion;
        }

        for (var attempt = 0; attempt < AssistantTurnPolicy.MaxWriteAttempts; attempt++)
        {
            var now = clock.GetUtcNow();

            if (await operations.GetAsync(userId, operationId, cancellationToken) is not { } stored
                || await ExpireIfDueAsync(stored, now, cancellationToken) is not { } operation)
            {
                return AssistantResult<AssistantOperationOutcome<TDraft>>.NotFound();
            }

            // Already confirmed (by this request's earlier attempt, a retry or a concurrent request).
            if (operation.Status == AssistantOperationStatus.Executed)
            {
                return AssistantResult<AssistantOperationOutcome<TDraft>>.Ok(new AssistantOperationOutcome<TDraft>(View(operation, now), Changed: false));
            }

            if ((Closed(operation) ?? StaleVersion(operation, expectedVersion)) is { } refusal)
            {
                return WithView(refusal, View(operation, now));
            }

            var view = View(operation, now);

            if (operation.Status != AssistantOperationStatus.AwaitingConfirmation)
            {
                return Rejected(AssistantErrors.DraftIncomplete, view.MissingFields[0], new AssistantOperationOutcome<TDraft>(view, Changed: false));
            }

            if (view.Issues.Count > 0)
            {
                return Rejected(view.Issues[0], "request", new AssistantOperationOutcome<TDraft>(view, Changed: false));
            }

            var version = operation.DraftVersion;
            AssistantExecution? refused = null;

            var committed = await unitOfWork.TryInTransactionAsync(async ct =>
            {
                if (!await operations.TryLockForConfirmationAsync(userId, operation.Id, version, now, ct))
                {
                    return false;
                }

                var execution = await policy.ExecuteAsync(userId, operation.Draft, ct);

                if (execution.ResultId is not { } resultId)
                {
                    // Rolls back everything, including anything the use case may have attempted.
                    refused = execution;
                    return false;
                }

                operation.MarkExecuted(resultId, now);

                // Under the row lock this cannot lose; if it ever did, nothing commits.
                return await operations.TryUpdateAsync(operation, AssistantOperationStatus.AwaitingConfirmation, version, ct);
            }, cancellationToken);

            if (committed)
            {
                return AssistantResult<AssistantOperationOutcome<TDraft>>.Ok(new AssistantOperationOutcome<TDraft>(View(operation, now), Changed: true));
            }

            if (refused is not null)
            {
                return Rejected(refused.RefusalCode ?? AssistantErrors.RecordRejected, refused.Field ?? "request",
                    new AssistantOperationOutcome<TDraft>(view, Changed: false));
            }

            // Lost the row to a concurrent change: decide again on what is stored now.
        }

        return AssistantResult<AssistantOperationOutcome<TDraft>>.Busy();
    }

    public AssistantOperationView<TDraft> View(AssistantOperation<TDraft> operation, DateTimeOffset now)
    {
        var issues = operation.IsOpen ? policy.Issues(operation.Draft, now) : [];

        return new AssistantOperationView<TDraft>(operation, policy.MissingFields(operation.Draft), issues, policy.OccursInFuture(operation.Draft, now),
            operation.Status == AssistantOperationStatus.AwaitingConfirmation && issues.Count == 0 && !operation.IsExpiredAt(now));
    }

    private async Task<AssistantOperation<TDraft>?> ExpireIfDueAsync(AssistantOperation<TDraft> operation, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var (status, version) = (operation.Status, operation.DraftVersion);

        if (!operation.ExpireIfDue(now) || await operations.TryUpdateAsync(operation, status, version, cancellationToken))
        {
            return operation;
        }

        // Another request expired, confirmed or cancelled it first: whatever it stored.
        return await operations.GetAsync(operation.UserId, operation.Id, cancellationToken);
    }

    private static AssistantResult<T>? InvalidVersion<T>(int expectedVersion) =>
        expectedVersion < AssistantOperation<TDraft>.FirstDraftVersion
            ? AssistantResult<T>.Invalid("expectedVersion", "expectedVersion must be a draft version (from 1).")
            : null;

    private static AssistantResult<AssistantOperationOutcome<TDraft>>? Closed(AssistantOperation<TDraft> operation) => operation.Status switch
    {
        AssistantOperationStatus.Executed => AssistantResult<AssistantOperationOutcome<TDraft>>.Conflict(AssistantErrors.OperationExecuted,
            "The operation has already been executed."),
        AssistantOperationStatus.Cancelled => AssistantResult<AssistantOperationOutcome<TDraft>>.Conflict(AssistantErrors.OperationCancelled,
            "The operation has been cancelled."),
        AssistantOperationStatus.Expired => AssistantResult<AssistantOperationOutcome<TDraft>>.Conflict(AssistantErrors.OperationExpired,
            "The draft has expired."),
        _ => null
    };

    private static AssistantResult<AssistantOperationOutcome<TDraft>>? StaleVersion(AssistantOperation<TDraft> operation, int expectedVersion) =>
        operation.DraftVersion != expectedVersion
            ? AssistantResult<AssistantOperationOutcome<TDraft>>.Conflict(AssistantErrors.VersionConflict, "The draft has changed since this version.")
            : null;

    private static AssistantResult<AssistantOperationOutcome<TDraft>> WithView(AssistantResult<AssistantOperationOutcome<TDraft>> result,
        AssistantOperationView<TDraft> view) =>
        result with { Value = new AssistantOperationOutcome<TDraft>(view, Changed: false) };

    private static AssistantResult<AssistantOperationOutcome<TDraft>> Rejected(string code, string field, AssistantOperationOutcome<TDraft> value) =>
        AssistantResult<AssistantOperationOutcome<TDraft>>.Rejected(code, field, code switch
        {
            AssistantErrors.DraftIncomplete => "The draft is not complete.",
            _ => "The record cannot be created as it is."
        }, value);
}
