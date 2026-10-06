using LifeOS.Application.Notifications;
using LifeOS.Application.Persistence;
using LifeOS.Domain.Automation;

namespace LifeOS.Application.Automation;

// Counts only: no user ids, types or data.
public sealed record AutomationTickResult(bool Skipped, int Deliveries, int Executions, bool More)
{
    public static readonly AutomationTickResult SkippedTick = new(true, 0, 0, false);
}

// AUTO-001 §7: the 60 s single-instance guard. It only saves wasted work on this instance (and stops
// ticks from overlapping here); occurrence correctness never depends on it — that is the database's
// job (unique occurrence key, conditional claims, fenced completion). Also rotates the handler order
// between ticks for fairness. Registered as a singleton.
public sealed class AutomationTickGuard
{
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(60);

    private readonly Lock _lock = new();
    private bool _running;
    private DateTimeOffset? _lastStartedAtUtc;
    private int _ticks;

    public bool TryEnter(DateTimeOffset nowUtc, out int rotation)
    {
        lock (_lock)
        {
            rotation = 0;

            if (_running || (_lastStartedAtUtc is { } last && nowUtc - last < MinimumInterval))
            {
                return false;
            }

            _running = true;
            _lastStartedAtUtc = nowUtc;
            rotation = _ticks++;

            return true;
        }
    }

    public void Exit()
    {
        lock (_lock)
        {
            _running = false;
        }
    }
}

// AUTO-001 §7: one parameterless tick. LifeOS decides what is due by its own clock; the caller
// chooses no user, type or time.
//
//   Phase A — notification dispatch: abandoned deliveries are made terminal, then due per-device
//             deliveries (Pending, or Sending with an expired lease) are claimed and sent, oldest
//             first. Skipped while no push sender is registered (NotificationDispatcher.IsEnabled).
//   Phase B — retries: abandoned rows are made terminal, then FailedRetryable rows whose next attempt
//             is due and Running rows whose lease expired are claimed and executed, oldest first.
//   Phase C — discovery: each handler (rotated order) reports due occurrences; each is claimed by
//             insert and, if this tick won the claim, executed.
//
// Bounded: at most MaxDeliveriesPerTick deliveries and MaxExecutionsPerTick attempts, and no new claim
// after TimeBudget. Claimed items always finish and complete (completion does not observe
// cancellation). More = work may remain.
//
// A succeeded execution's artifact (if any) and notification deliveries are written in the same
// transaction as its fenced completion (AUTO-001 §8): if another attempt took the lease over, none
// of them is written.
public sealed class RunAutomationTick
{
    public const int MaxExecutionsPerTick = 25;
    public static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(20);

    private readonly IReadOnlyList<IAutomationHandler> _handlers;
    private readonly IAutomationExecutionStore _store;
    private readonly NotificationDispatcher _notifications;
    private readonly INotificationDeliveryStore _deliveries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly AutomationTickGuard _guard;
    private readonly TimeProvider _time;

    public RunAutomationTick(
        IEnumerable<IAutomationHandler> handlers,
        IAutomationExecutionStore store,
        NotificationDispatcher notifications,
        INotificationDeliveryStore deliveries,
        IUnitOfWork unitOfWork,
        AutomationTickGuard guard,
        TimeProvider time)
    {
        _handlers = handlers.ToList();
        _store = store;
        _notifications = notifications;
        _deliveries = deliveries;
        _unitOfWork = unitOfWork;
        _guard = guard;
        _time = time;

        var duplicate = _handlers.GroupBy(handler => handler.AutomationType, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw new InvalidOperationException($"More than one automation handler is registered for '{duplicate.Key}'.");
        }
    }

    // cancellationToken only stops new claims (e.g. host shutdown); it is never the HTTP request's.
    public async Task<AutomationTickResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = _time.GetUtcNow();

        if (!_guard.TryEnter(nowUtc, out var rotation))
        {
            return AutomationTickResult.SkippedTick;
        }

        try
        {
            var tick = new TickState(nowUtc + TimeBudget, _time, cancellationToken);

            await RunDeliveriesAsync(tick);
            await RunRetriesAsync(tick);
            await RunDiscoveryAsync(tick, rotation);

            return new AutomationTickResult(false, tick.Deliveries, tick.Executions, tick.More);
        }
        finally
        {
            _guard.Exit();
        }
    }

    private async Task RunDeliveriesAsync(TickState tick)
    {
        if (!_notifications.IsEnabled)
        {
            return;
        }

        if (await _notifications.FinalizeAbandonedAsync(NotificationDeliveryPolicy.MaxDeliveriesPerTick) >= NotificationDeliveryPolicy.MaxDeliveriesPerTick)
        {
            tick.More = true;
        }

        while (tick.CanDeliver())
        {
            if (!await _notifications.DispatchNextAsync())
            {
                return;
            }

            tick.Deliveries++;
        }
    }

    private async Task RunRetriesAsync(TickState tick)
    {
        var finalized = await _store.FinalizeAbandonedAsync(_time.GetUtcNow(), AutomationExecutionPolicy.MaxAttempts, MaxExecutionsPerTick, CancellationToken.None);

        if (finalized >= MaxExecutionsPerTick)
        {
            tick.More = true;
        }

        if (_handlers.Count == 0)
        {
            return;
        }

        var handlersByType = _handlers.ToDictionary(handler => handler.AutomationType, StringComparer.Ordinal);

        while (tick.CanClaim())
        {
            var occurrence = await _store.ClaimNextRetryAsync(
                handlersByType.Keys,
                _time.GetUtcNow(),
                AutomationExecutionPolicy.Lease,
                AutomationExecutionPolicy.MaxAttempts,
                CancellationToken.None);

            if (occurrence is null)
            {
                return;
            }

            await ExecuteAsync(handlersByType[occurrence.AutomationType], occurrence);
            tick.Executions++;
        }
    }

    private async Task RunDiscoveryAsync(TickState tick, int rotation)
    {
        for (var index = 0; index < _handlers.Count; index++)
        {
            if (!tick.CanClaim())
            {
                return;
            }

            var handler = _handlers[(rotation + index) % _handlers.Count];
            var limit = MaxExecutionsPerTick - tick.Executions;
            var due = await handler.FindDueAsync(tick.NowForDiscovery, limit, tick.CancellationToken);

            if (due.Count >= limit)
            {
                tick.More = true;
            }

            foreach (var occurrence in due.Take(limit))
            {
                if (!tick.CanClaim())
                {
                    return;
                }

                var claimed = await TryClaimAsync(handler, occurrence);

                if (claimed is not null)
                {
                    await ExecuteAsync(handler, claimed);
                    tick.Executions++;
                }
            }
        }
    }

    private async Task<AutomationOccurrence?> TryClaimAsync(IAutomationHandler handler, DueOccurrence due)
    {
        var nowUtc = _time.GetUtcNow();
        var expiresAtUtc = due.ScheduledForUtc + handler.MaxLateness;

        // Not yet due, or expired: no row is created (AUTO-001 §6 "time zone changes").
        if (nowUtc < due.ScheduledForUtc || nowUtc >= expiresAtUtc)
        {
            return null;
        }

        var execution = AutomationExecution.Claim(
            due.UserId,
            handler.AutomationType,
            due.OccurrenceKey,
            due.TimeZoneId,
            due.ScheduledForUtc,
            expiresAtUtc,
            nowUtc,
            AutomationExecutionPolicy.Lease);

        if (!await _store.TryClaimAsync(execution, CancellationToken.None))
        {
            return null;
        }

        return new AutomationOccurrence(
            execution.Id,
            execution.UserId,
            execution.AutomationType,
            execution.OccurrenceKey,
            execution.TimeZoneId,
            execution.ScheduledForUtc,
            execution.ExpiresAtUtc,
            execution.AttemptCount);
    }

    // Runs one claimed attempt and records its outcome with a fenced completion. A false completion
    // means another tick took the lease over; that attempt now owns the row.
    private async Task ExecuteAsync(IAutomationHandler handler, AutomationOccurrence occurrence)
    {
        AutomationResult result;

        try
        {
            // The attempt is pointless once its lease can be taken over.
            using var lease = new CancellationTokenSource(AutomationExecutionPolicy.Lease, _time);
            result = await handler.ExecuteAsync(occurrence, lease.Token)
                ?? AutomationResult.RetryableFailure(AutomationExecutionPolicy.UnhandledCode);
        }
        catch (Exception)
        {
            // Never stored as text: only the stable code.
            result = AutomationResult.RetryableFailure(AutomationExecutionPolicy.UnhandledCode);
        }

        var completedAtUtc = _time.GetUtcNow();

        switch (result)
        {
            case AutomationResult.Success success:
                await _unitOfWork.TryInTransactionAsync(async cancellationToken =>
                {
                    if (!await _store.CompleteSucceededAsync(occurrence.ExecutionId, occurrence.Attempt, success.ResultId, completedAtUtc, cancellationToken))
                    {
                        return false;
                    }

                    // The artifact commits with the completion or not at all (AUTO-001 §8 step 1).
                    if (success.SaveArtifact is { } saveArtifact && !await saveArtifact(cancellationToken))
                    {
                        return false;
                    }

                    if (success.Notification is { } notification)
                    {
                        await _deliveries.EnqueueAsync(
                            LogicalNotification.ForAutomation(
                                occurrence.ExecutionId, occurrence.UserId, notification.Type, notification.ResourceType, notification.ResourceId, completedAtUtc),
                            completedAtUtc,
                            cancellationToken);
                    }

                    return true;
                }, CancellationToken.None);
                break;

            case AutomationResult.Inapplicable:
                await _store.CompleteSucceededAsync(occurrence.ExecutionId, occurrence.Attempt, resultId: null, completedAtUtc, CancellationToken.None);
                break;

            case AutomationResult.Permanent permanent:
                await _store.CompleteFinalAsync(
                    occurrence.ExecutionId, occurrence.Attempt, AutomationExecutionPolicy.PermanentPrefix + permanent.Code, completedAtUtc, CancellationToken.None);
                break;

            case AutomationResult.Retryable retryable:
                var outcome = AutomationExecutionPolicy.AfterRetryableFailure(occurrence.Attempt, retryable.Code, completedAtUtc, occurrence.ExpiresAtUtc);

                if (outcome.NextAttemptAtUtc is { } nextAttemptAtUtc)
                {
                    await _store.CompleteRetryableAsync(occurrence.ExecutionId, occurrence.Attempt, outcome.Code, nextAttemptAtUtc, CancellationToken.None);
                }
                else
                {
                    await _store.CompleteFinalAsync(occurrence.ExecutionId, occurrence.Attempt, outcome.Code, completedAtUtc, CancellationToken.None);
                }

                break;
        }
    }

    private sealed class TickState(DateTimeOffset deadlineUtc, TimeProvider time, CancellationToken cancellationToken)
    {
        // Discovery evaluates "is due" at the tick's start; each claim re-checks with the current time.
        public DateTimeOffset NowForDiscovery { get; } = deadlineUtc - TimeBudget;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public int Deliveries { get; set; }

        public int Executions { get; set; }

        public bool More { get; set; }

        public bool CanDeliver()
        {
            if (Deliveries >= NotificationDeliveryPolicy.MaxDeliveriesPerTick || time.GetUtcNow() >= deadlineUtc || CancellationToken.IsCancellationRequested)
            {
                More = true;
                return false;
            }

            return true;
        }

        // False once the execution cap or the time budget is reached (then work may remain), or the
        // caller cancelled.
        public bool CanClaim()
        {
            if (Executions >= MaxExecutionsPerTick || time.GetUtcNow() >= deadlineUtc || CancellationToken.IsCancellationRequested)
            {
                More = true;
                return false;
            }

            return true;
        }
    }
}
