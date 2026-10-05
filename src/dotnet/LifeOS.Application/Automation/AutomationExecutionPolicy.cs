namespace LifeOS.Application.Automation;

// AUTO-001 §8–§10: fixed lease and bounded retries for executions.
public static class AutomationExecutionPolicy
{
    public const int MaxAttempts = 3;

    // ≫ the 20 s tick budget, ≪ the retry delays. An expired lease means a crashed attempt.
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    // Delay before attempt 2, then attempt 3.
    public static readonly IReadOnlyList<TimeSpan> RetryDelays = [TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)];

    public const string PermanentPrefix = "Permanent:";
    public const string ExpiredCode = "Expired";
    public const string MaxAttemptsReachedCode = "MaxAttemptsReached";
    public const string UnhandledCode = "Unhandled";

    // A retryable failure of `attempt`: retried later while attempts remain and the occurrence has
    // not expired; otherwise final.
    public static FailureOutcome AfterRetryableFailure(int attempt, string code, DateTimeOffset nowUtc, DateTimeOffset expiresAtUtc)
    {
        if (attempt >= MaxAttempts)
        {
            return new FailureOutcome(MaxAttemptsReachedCode, NextAttemptAtUtc: null);
        }

        if (nowUtc >= expiresAtUtc)
        {
            return new FailureOutcome(ExpiredCode, NextAttemptAtUtc: null);
        }

        return new FailureOutcome(code, nowUtc + RetryDelays[attempt - 1]);
    }

    // NextAttemptAtUtc null: FailedFinal with Code; otherwise FailedRetryable.
    public sealed record FailureOutcome(string Code, DateTimeOffset? NextAttemptAtUtc)
    {
        public bool IsFinal => NextAttemptAtUtc is null;
    }
}
