using LifeOS.Application.Automation;
using LifeOS.Domain.Automation;
using LifeOS.Infrastructure.Persistence;
using LifeOS.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Automation;

// AUTO-001 §8 on PostgreSQL. Every transition is ONE statement whose WHERE clause states the
// transition's precondition, so concurrent ticks cannot both win:
// - claim = INSERT … ON CONFLICT (occurrence) DO NOTHING (the unique index decides);
// - retry/takeover = UPDATE of a row picked with FOR UPDATE SKIP LOCKED (each row has one claimer,
//   ticks never block each other); the locking subquery re-checks its conditions after the lock;
// - completion = UPDATE … WHERE status = 'Running' AND attempt_count = <my attempt> (fencing).
// Statuses are written as the same strings the EF model stores (HasConversion<string>).
internal sealed class AutomationExecutionStore(LifeOSDbContext db) : IAutomationExecutionStore
{
    public async Task<bool> TryClaimAsync(AutomationExecution execution, CancellationToken cancellationToken)
    {
        try
        {
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO automation_executions
                    (id, user_id, automation_type, occurrence_key, time_zone_id, scheduled_for_utc, expires_at_utc,
                     status, attempt_count, lease_expires_at_utc, created_at_utc, started_at_utc)
                VALUES
                    ({execution.Id}, {execution.UserId}, {execution.AutomationType}, {execution.OccurrenceKey},
                     {execution.TimeZoneId}, {execution.ScheduledForUtc}, {execution.ExpiresAtUtc},
                     'Running', {execution.AttemptCount}, {execution.LeaseExpiresAtUtc!.Value},
                     {execution.CreatedAtUtc}, {execution.StartedAtUtc})
                ON CONFLICT (user_id, automation_type, occurrence_key) DO NOTHING
                """, cancellationToken);

            return inserted == 1;
        }
        catch (Exception exception) when (PostgresErrors.IsForeignKeyViolation(exception, AutomationExecutionConfiguration.UserForeignKeyName))
        {
            // The user was deleted after discovery: nothing to run.
            return false;
        }
    }

    public async Task<AutomationOccurrence?> ClaimNextRetryAsync(
        IReadOnlyCollection<string> automationTypes,
        DateTimeOffset nowUtc,
        TimeSpan lease,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        if (automationTypes.Count == 0)
        {
            return null;
        }

        var types = automationTypes.ToArray();
        var now = nowUtc.ToUniversalTime();
        var leaseUntil = now + lease;

        var claimed = await db.AutomationExecutions
            .FromSqlInterpolated($"""
                UPDATE automation_executions
                SET status = 'Running',
                    attempt_count = attempt_count + 1,
                    lease_expires_at_utc = {leaseUntil},
                    started_at_utc = {now},
                    next_attempt_at_utc = NULL
                WHERE id = (
                    SELECT id FROM automation_executions
                    WHERE automation_type = ANY({types})
                      AND {now} < expires_at_utc
                      AND ((status = 'FailedRetryable' AND next_attempt_at_utc <= {now})
                        OR (status = 'Running' AND lease_expires_at_utc <= {now} AND attempt_count < {maxAttempts}))
                    ORDER BY COALESCE(next_attempt_at_utc, lease_expires_at_utc), id
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED)
                RETURNING *
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return claimed.Count == 0
            ? null
            : new AutomationOccurrence(
                claimed[0].Id,
                claimed[0].UserId,
                claimed[0].AutomationType,
                claimed[0].OccurrenceKey,
                claimed[0].TimeZoneId,
                claimed[0].ScheduledForUtc,
                claimed[0].ExpiresAtUtc,
                claimed[0].AttemptCount);
    }

    public Task<int> FinalizeAbandonedAsync(DateTimeOffset nowUtc, int maxAttempts, int limit, CancellationToken cancellationToken)
    {
        var now = nowUtc.ToUniversalTime();

        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE automation_executions
            SET status = 'FailedFinal',
                last_failure_code = CASE WHEN {now} >= expires_at_utc THEN {AutomationExecutionPolicy.ExpiredCode}
                                         ELSE {AutomationExecutionPolicy.MaxAttemptsReachedCode} END,
                completed_at_utc = {now},
                lease_expires_at_utc = NULL,
                next_attempt_at_utc = NULL
            WHERE id IN (
                SELECT id FROM automation_executions
                WHERE (status = 'FailedRetryable' AND {now} >= expires_at_utc)
                   OR (status = 'Running' AND lease_expires_at_utc <= {now}
                       AND ({now} >= expires_at_utc OR attempt_count >= {maxAttempts}))
                ORDER BY id
                LIMIT {limit}
                FOR UPDATE SKIP LOCKED)
            """, cancellationToken);
    }

    public async Task<bool> CompleteSucceededAsync(Guid executionId, int attempt, Guid? resultId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var now = nowUtc.ToUniversalTime();

        var updated = await db.AutomationExecutions
            .Where(execution => execution.Id == executionId
                && execution.Status == AutomationExecutionStatus.Running
                && execution.AttemptCount == attempt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(execution => execution.Status, AutomationExecutionStatus.Succeeded)
                .SetProperty(execution => execution.ResultId, resultId)
                .SetProperty(execution => execution.CompletedAtUtc, now)
                .SetProperty(execution => execution.LeaseExpiresAtUtc, (DateTimeOffset?)null)
                .SetProperty(execution => execution.NextAttemptAtUtc, (DateTimeOffset?)null)
                .SetProperty(execution => execution.LastFailureCode, (string?)null),
                cancellationToken);

        return updated == 1;
    }

    public async Task<bool> CompleteRetryableAsync(Guid executionId, int attempt, string failureCode, DateTimeOffset nextAttemptAtUtc, CancellationToken cancellationToken)
    {
        RequireStableCode(failureCode);
        var next = nextAttemptAtUtc.ToUniversalTime();

        var updated = await db.AutomationExecutions
            .Where(execution => execution.Id == executionId
                && execution.Status == AutomationExecutionStatus.Running
                && execution.AttemptCount == attempt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(execution => execution.Status, AutomationExecutionStatus.FailedRetryable)
                .SetProperty(execution => execution.NextAttemptAtUtc, next)
                .SetProperty(execution => execution.LastFailureCode, failureCode)
                .SetProperty(execution => execution.LeaseExpiresAtUtc, (DateTimeOffset?)null),
                cancellationToken);

        return updated == 1;
    }

    public async Task<bool> CompleteFinalAsync(Guid executionId, int attempt, string failureCode, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        RequireStableCode(failureCode);
        var now = nowUtc.ToUniversalTime();

        var updated = await db.AutomationExecutions
            .Where(execution => execution.Id == executionId
                && execution.Status == AutomationExecutionStatus.Running
                && execution.AttemptCount == attempt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(execution => execution.Status, AutomationExecutionStatus.FailedFinal)
                .SetProperty(execution => execution.LastFailureCode, failureCode)
                .SetProperty(execution => execution.CompletedAtUtc, now)
                .SetProperty(execution => execution.LeaseExpiresAtUtc, (DateTimeOffset?)null)
                .SetProperty(execution => execution.NextAttemptAtUtc, (DateTimeOffset?)null),
                cancellationToken);

        return updated == 1;
    }

    private static void RequireStableCode(string failureCode)
    {
        if (!AutomationExecution.IsStableCode(failureCode))
        {
            throw new ArgumentException("A failure code must be a stable code.", nameof(failureCode));
        }
    }
}
