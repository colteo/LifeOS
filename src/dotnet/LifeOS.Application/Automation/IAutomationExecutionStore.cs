using LifeOS.Domain.Automation;

namespace LifeOS.Application.Automation;

// AUTO-001 §8: persistence of automation_executions with database-enforced idempotency. Every
// operation is one atomic statement; correctness never depends on in-memory state.
//
// Completion is fenced: it applies only while the row is still Running with the caller's attempt
// number, so an attempt whose lease was taken over can no longer complete (returns false).
public interface IAutomationExecutionStore
{
    // Claim = insert. False when the occurrence (user, type, key) already has a row, or the user no
    // longer exists. Only one claim of an occurrence can ever succeed.
    Task<bool> TryClaimAsync(AutomationExecution execution, CancellationToken cancellationToken);

    // Claims the oldest eligible row of one of these types and starts its next attempt (Running,
    // attempt + 1, new lease): FailedRetryable whose next attempt is due, or Running whose lease
    // expired with attempts left (takeover). Never a row at or after its expiry. Rows locked by a
    // concurrent claimer are skipped. Null when none is eligible.
    Task<AutomationOccurrence?> ClaimNextRetryAsync(
        IReadOnlyCollection<string> automationTypes,
        DateTimeOffset nowUtc,
        TimeSpan lease,
        int maxAttempts,
        CancellationToken cancellationToken);

    // Makes up to `limit` abandoned rows terminal (FailedFinal): FailedRetryable rows past their
    // expiry ("Expired"), and Running rows whose lease expired after their expiry ("Expired") or
    // after the last allowed attempt ("MaxAttemptsReached"). Returns how many rows changed.
    Task<int> FinalizeAbandonedAsync(DateTimeOffset nowUtc, int maxAttempts, int limit, CancellationToken cancellationToken);

    Task<bool> CompleteSucceededAsync(Guid executionId, int attempt, Guid? resultId, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    Task<bool> CompleteRetryableAsync(Guid executionId, int attempt, string failureCode, DateTimeOffset nextAttemptAtUtc, CancellationToken cancellationToken);

    Task<bool> CompleteFinalAsync(Guid executionId, int attempt, string failureCode, DateTimeOffset nowUtc, CancellationToken cancellationToken);
}
