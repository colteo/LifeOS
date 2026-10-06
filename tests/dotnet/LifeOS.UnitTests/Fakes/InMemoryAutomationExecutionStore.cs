using LifeOS.Application.Automation;
using LifeOS.Domain.Automation;

namespace LifeOS.UnitTests.Fakes;

// Same preconditions as the PostgreSQL store's statements (AutomationExecutionStore), under one lock.
// The real store's atomicity and concurrency are proven against PostgreSQL in the integration tests.
internal sealed class InMemoryAutomationExecutionStore : IAutomationExecutionStore
{
    private readonly Lock _lock = new();

    public List<Row> Rows { get; } = [];

    // Users that exist; null = every user exists.
    public HashSet<Guid>? ExistingUsers { get; set; }

    public sealed class Row
    {
        public required Guid Id { get; init; }
        public required Guid UserId { get; init; }
        public required string AutomationType { get; init; }
        public required string OccurrenceKey { get; init; }
        public required string TimeZoneId { get; init; }
        public required DateTimeOffset ScheduledForUtc { get; init; }
        public required DateTimeOffset ExpiresAtUtc { get; init; }
        public AutomationExecutionStatus Status { get; set; }
        public int AttemptCount { get; set; }
        public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
        public DateTimeOffset? NextAttemptAtUtc { get; set; }
        public string? LastFailureCode { get; set; }
        public Guid? ResultId { get; set; }
        public DateTimeOffset StartedAtUtc { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
    }

    public Row Single(string occurrenceKey)
    {
        lock (_lock)
        {
            return Rows.Single(row => row.OccurrenceKey == occurrenceKey);
        }
    }

    public Task<bool> TryClaimAsync(AutomationExecution execution, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if ((ExistingUsers is not null && !ExistingUsers.Contains(execution.UserId))
                || Rows.Any(row => row.UserId == execution.UserId
                    && row.AutomationType == execution.AutomationType
                    && row.OccurrenceKey == execution.OccurrenceKey))
            {
                return Task.FromResult(false);
            }

            Rows.Add(new Row
            {
                Id = execution.Id,
                UserId = execution.UserId,
                AutomationType = execution.AutomationType,
                OccurrenceKey = execution.OccurrenceKey,
                TimeZoneId = execution.TimeZoneId,
                ScheduledForUtc = execution.ScheduledForUtc,
                ExpiresAtUtc = execution.ExpiresAtUtc,
                Status = execution.Status,
                AttemptCount = execution.AttemptCount,
                LeaseExpiresAtUtc = execution.LeaseExpiresAtUtc,
                StartedAtUtc = execution.StartedAtUtc
            });

            return Task.FromResult(true);
        }
    }

    public Task<AutomationOccurrence?> ClaimNextRetryAsync(
        IReadOnlyCollection<string> automationTypes,
        DateTimeOffset nowUtc,
        TimeSpan lease,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var row = Rows
                .Where(row => automationTypes.Contains(row.AutomationType)
                    && nowUtc < row.ExpiresAtUtc
                    && ((row.Status == AutomationExecutionStatus.FailedRetryable && row.NextAttemptAtUtc <= nowUtc)
                        || (row.Status == AutomationExecutionStatus.Running && row.LeaseExpiresAtUtc <= nowUtc && row.AttemptCount < maxAttempts)))
                .OrderBy(row => row.NextAttemptAtUtc ?? row.LeaseExpiresAtUtc)
                .ThenBy(row => row.Id)
                .FirstOrDefault();

            if (row is null)
            {
                return Task.FromResult<AutomationOccurrence?>(null);
            }

            row.Status = AutomationExecutionStatus.Running;
            row.AttemptCount++;
            row.LeaseExpiresAtUtc = nowUtc + lease;
            row.StartedAtUtc = nowUtc;
            row.NextAttemptAtUtc = null;

            return Task.FromResult<AutomationOccurrence?>(new AutomationOccurrence(
                row.Id, row.UserId, row.AutomationType, row.OccurrenceKey, row.TimeZoneId, row.ScheduledForUtc, row.ExpiresAtUtc, row.AttemptCount));
        }
    }

    public Task<int> FinalizeAbandonedAsync(DateTimeOffset nowUtc, int maxAttempts, int limit, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var abandoned = Rows
                .Where(row => (row.Status == AutomationExecutionStatus.FailedRetryable && nowUtc >= row.ExpiresAtUtc)
                    || (row.Status == AutomationExecutionStatus.Running && row.LeaseExpiresAtUtc <= nowUtc
                        && (nowUtc >= row.ExpiresAtUtc || row.AttemptCount >= maxAttempts)))
                .OrderBy(row => row.Id)
                .Take(limit)
                .ToList();

            foreach (var row in abandoned)
            {
                row.LastFailureCode = nowUtc >= row.ExpiresAtUtc
                    ? AutomationExecutionPolicy.ExpiredCode
                    : AutomationExecutionPolicy.MaxAttemptsReachedCode;
                row.Status = AutomationExecutionStatus.FailedFinal;
                row.CompletedAtUtc = nowUtc;
                row.LeaseExpiresAtUtc = null;
                row.NextAttemptAtUtc = null;
            }

            return Task.FromResult(abandoned.Count);
        }
    }

    public Task<bool> CompleteSucceededAsync(Guid executionId, int attempt, Guid? resultId, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        Complete(executionId, attempt, row =>
        {
            row.Status = AutomationExecutionStatus.Succeeded;
            row.ResultId = resultId;
            row.CompletedAtUtc = nowUtc;
            row.LastFailureCode = null;
        });

    public Task<bool> CompleteRetryableAsync(Guid executionId, int attempt, string failureCode, DateTimeOffset nextAttemptAtUtc, CancellationToken cancellationToken) =>
        Complete(executionId, attempt, row =>
        {
            row.Status = AutomationExecutionStatus.FailedRetryable;
            row.NextAttemptAtUtc = nextAttemptAtUtc;
            row.LastFailureCode = failureCode;
        });

    public Task<bool> CompleteFinalAsync(Guid executionId, int attempt, string failureCode, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        Complete(executionId, attempt, row =>
        {
            row.Status = AutomationExecutionStatus.FailedFinal;
            row.LastFailureCode = failureCode;
            row.CompletedAtUtc = nowUtc;
        });

    // Fenced: only the current Running attempt completes.
    private Task<bool> Complete(Guid executionId, int attempt, Action<Row> apply)
    {
        lock (_lock)
        {
            var row = Rows.SingleOrDefault(row => row.Id == executionId
                && row.Status == AutomationExecutionStatus.Running
                && row.AttemptCount == attempt);

            if (row is null)
            {
                return Task.FromResult(false);
            }

            apply(row);
            row.LeaseExpiresAtUtc = null;

            if (row.Status != AutomationExecutionStatus.FailedRetryable)
            {
                row.NextAttemptAtUtc = null;
            }

            return Task.FromResult(true);
        }
    }
}
