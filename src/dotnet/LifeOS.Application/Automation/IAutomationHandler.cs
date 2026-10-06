using LifeOS.Domain.Automation;
using LifeOS.Domain.Notifications;

namespace LifeOS.Application.Automation;

// AUTO-001 §14: one scheduled business automation. Handlers live in their own module and call that
// module's use cases; the automation core never references a module. Registered with plain DI
// (IEnumerable<IAutomationHandler>). AUTO-001 registers none.
public interface IAutomationHandler
{
    // Stable code stored in automation_executions.automation_type, e.g. "WeeklyReview".
    string AutomationType { get; }

    // An occurrence expires at ScheduledForUtc + MaxLateness; no attempt starts after that.
    TimeSpan MaxLateness { get; }

    // At most `limit` occurrences that are due at nowUtc and have no execution row yet.
    Task<IReadOnlyList<DueOccurrence>> FindDueAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken);

    // Expected outcomes are results, not exceptions. An exception is recorded as a retryable
    // "Unhandled" failure.
    Task<AutomationResult> ExecuteAsync(AutomationOccurrence occurrence, CancellationToken cancellationToken);
}

// A due occurrence found by a handler. The key comes from local calendar data (AUTO-001 §8).
public sealed record DueOccurrence(
    Guid UserId,
    string OccurrenceKey,
    string TimeZoneId,
    DateTimeOffset ScheduledForUtc);

// The occurrence a claimed attempt is executing. Attempt is the fencing token of this attempt.
public sealed record AutomationOccurrence(
    Guid ExecutionId,
    Guid UserId,
    string AutomationType,
    string OccurrenceKey,
    string TimeZoneId,
    DateTimeOffset ScheduledForUtc,
    DateTimeOffset ExpiresAtUtc,
    int Attempt);

// What a succeeded execution notifies (fixed copy by type; deep-link target as an opaque id).
public sealed record AutomationNotification(NotificationType Type, string? ResourceType = null, Guid? ResourceId = null);

// AUTO-001 §9 handler results. Failure codes are stable codes, never exception or provider text.
public abstract record AutomationResult
{
    private AutomationResult()
    {
    }

    // The optional notification is enqueued (one delivery per Active device of the user) in the same
    // transaction as the fenced completion; its key is "automation:<execution id>".
    //
    // saveArtifact (AUTO-002) writes the handler's artifact in that same transaction, after the fenced
    // completion and before the notification. It must use the scoped stores (never open its own
    // transaction). False — e.g. the artifact already exists — rolls the whole completion back: the
    // attempt stays Running and a later attempt takes it over.
    public static AutomationResult Succeeded(
        Guid? resultId = null,
        AutomationNotification? notification = null,
        Func<CancellationToken, Task<bool>>? saveArtifact = null) =>
        new Success(resultId, notification, saveArtifact);

    public static AutomationResult RetryableFailure(string code) => new Retryable(RequireCode(code, AutomationExecution.MaxFailureCodeLength));

    // Stored as "Permanent:<code>".
    public static AutomationResult PermanentFailure(string code) =>
        new Permanent(RequireCode(code, AutomationExecution.MaxFailureCodeLength - AutomationExecutionPolicy.PermanentPrefix.Length));

    // E.g. the feature was disabled after discovery: recorded as Succeeded with no artifact, so the
    // occurrence is not rediscovered.
    public static AutomationResult NotApplicable() => new Inapplicable();

    // Created only through the factories above (get-only properties: `with` cannot bypass validation).
    public sealed record Success : AutomationResult
    {
        internal Success(Guid? resultId, AutomationNotification? notification, Func<CancellationToken, Task<bool>>? saveArtifact)
        {
            ResultId = resultId;
            Notification = notification;
            SaveArtifact = saveArtifact;
        }

        public Guid? ResultId { get; }

        public AutomationNotification? Notification { get; }

        public Func<CancellationToken, Task<bool>>? SaveArtifact { get; }
    }

    public sealed record Retryable : AutomationResult
    {
        internal Retryable(string code) => Code = code;

        public string Code { get; }
    }

    public sealed record Permanent : AutomationResult
    {
        internal Permanent(string code) => Code = code;

        public string Code { get; }
    }

    public sealed record Inapplicable : AutomationResult
    {
        internal Inapplicable()
        {
        }
    }

    private static string RequireCode(string code, int maxLength) =>
        AutomationExecution.IsStableCode(code, maxLength)
            ? code
            : throw new ArgumentException("A failure code must be a stable code.", nameof(code));
}
