using LifeOS.Domain.Users;

namespace LifeOS.Domain.Automation;

public enum AutomationExecutionStatus
{
    Running,
    Succeeded,
    FailedRetryable,
    FailedFinal
}

// AUTO-001 §5.3: "did this scheduled occurrence run". One row per logical occurrence
// (user, automation type, occurrence key). A row exists only once an occurrence has been claimed,
// so it always starts Running at attempt 1. Later transitions (retry claim, lease takeover, fenced
// completion) are atomic conditional updates in the store; this type only creates valid claims and
// defines the stable codes that may be stored.
public sealed class AutomationExecution
{
    public const int MaxAutomationTypeLength = 64;
    public const int MaxOccurrenceKeyLength = 64;
    public const int MaxFailureCodeLength = 64;

    private AutomationExecution(
        Guid id,
        Guid userId,
        string automationType,
        string occurrenceKey,
        string timeZoneId,
        DateTimeOffset scheduledForUtc,
        DateTimeOffset expiresAtUtc,
        AutomationExecutionStatus status,
        int attemptCount,
        DateTimeOffset? leaseExpiresAtUtc,
        DateTimeOffset? nextAttemptAtUtc,
        string? lastFailureCode,
        Guid? resultId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? completedAtUtc)
    {
        Id = id;
        UserId = userId;
        AutomationType = automationType;
        OccurrenceKey = occurrenceKey;
        TimeZoneId = timeZoneId;
        ScheduledForUtc = scheduledForUtc;
        ExpiresAtUtc = expiresAtUtc;
        Status = status;
        AttemptCount = attemptCount;
        LeaseExpiresAtUtc = leaseExpiresAtUtc;
        NextAttemptAtUtc = nextAttemptAtUtc;
        LastFailureCode = lastFailureCode;
        ResultId = resultId;
        CreatedAtUtc = createdAtUtc;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
    }

    public Guid Id { get; }

    public Guid UserId { get; }

    // Stable code chosen by the handler, e.g. "WeeklyReview".
    public string AutomationType { get; }

    // Handler-defined, from local calendar data (e.g. the local date "2026-10-04"), never the tick instant.
    public string OccurrenceKey { get; }

    // The zone used to resolve this occurrence; never rewritten when the user's zone changes.
    public string TimeZoneId { get; }

    public DateTimeOffset ScheduledForUtc { get; }

    // No attempt starts at or after this instant.
    public DateTimeOffset ExpiresAtUtc { get; }

    public AutomationExecutionStatus Status { get; }

    // ≥ 1; also the fencing token for completion.
    public int AttemptCount { get; }

    public DateTimeOffset? LeaseExpiresAtUtc { get; }

    public DateTimeOffset? NextAttemptAtUtc { get; }

    // A stable code (IsStableCode), never exception text or provider messages.
    public string? LastFailureCode { get; }

    // Opaque id of the artifact the handler produced, if any (no foreign key).
    public Guid? ResultId { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    // Start of the latest attempt.
    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset? CompletedAtUtc { get; }

    // Attempt 1 of a due occurrence: Running, leased until nowUtc + lease.
    public static AutomationExecution Claim(
        Guid userId,
        string automationType,
        string occurrenceKey,
        string timeZoneId,
        DateTimeOffset scheduledForUtc,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset nowUtc,
        TimeSpan lease)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A valid user id is required.", nameof(userId));
        }

        if (!IsStableCode(automationType, MaxAutomationTypeLength))
        {
            throw new ArgumentException("The automation type must be a stable code.", nameof(automationType));
        }

        if (!IsStableCode(occurrenceKey, MaxOccurrenceKeyLength))
        {
            throw new ArgumentException("The occurrence key must be a stable code.", nameof(occurrenceKey));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);

        if (timeZoneId.Length > User.MaxTimeZoneIdLength)
        {
            throw new ArgumentException($"Time zone id must be at most {User.MaxTimeZoneIdLength} characters.", nameof(timeZoneId));
        }

        if (lease <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lease), lease, "The lease must be positive.");
        }

        var scheduled = scheduledForUtc.ToUniversalTime();
        var expires = expiresAtUtc.ToUniversalTime();
        var now = nowUtc.ToUniversalTime();

        if (expires <= scheduled)
        {
            throw new ArgumentException("An occurrence must expire after it is scheduled.", nameof(expiresAtUtc));
        }

        if (now < scheduled || now >= expires)
        {
            throw new InvalidOperationException("Only a due, unexpired occurrence can be claimed.");
        }

        return new AutomationExecution(
            Guid.CreateVersion7(),
            userId,
            automationType,
            occurrenceKey,
            timeZoneId,
            scheduled,
            expires,
            AutomationExecutionStatus.Running,
            attemptCount: 1,
            leaseExpiresAtUtc: now + lease,
            nextAttemptAtUtc: null,
            lastFailureCode: null,
            resultId: null,
            createdAtUtc: now,
            startedAtUtc: now,
            completedAtUtc: null);
    }

    // Types, keys and failure codes: 1..maxLength characters of [A-Za-z0-9._:-]. This keeps them
    // machine-readable and makes it impossible to store free text such as exception messages.
    public static bool IsStableCode(string? value, int maxLength = MaxFailureCodeLength) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= maxLength
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');
}
