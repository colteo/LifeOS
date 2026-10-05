using LifeOS.Application.Automation;
using LifeOS.Domain.Automation;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Automation;

// AUTO-001 WP2: the tick engine with test-only handlers and the in-memory store. The PostgreSQL store
// (atomic claims, SKIP LOCKED, fencing under concurrency) is proven in LifeOS.IntegrationTests.
public class RunAutomationTickTests
{
    private static readonly DateTimeOffset Due = new(2026, 10, 4, 18, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserA = Guid.Parse("0192f0c3-0000-7000-8000-00000000000a");
    private static readonly Guid UserB = Guid.Parse("0192f0c3-0000-7000-8000-00000000000b");

    private readonly ManualTimeProvider _clock = new(Due.AddMinutes(3));
    private readonly InMemoryAutomationExecutionStore _store = new();
    private readonly AutomationTickGuard _guard = new();

    // ---- Zero handlers ----

    [Fact]
    public async Task ZeroHandlers_RunsAndExecutesNothing()
    {
        var result = await Tick().RunAsync();

        Assert.Equal(new AutomationTickResult(false, 0, false), result);
        Assert.Empty(_store.Rows);
    }

    // ---- Discovery, claim, execute once ----

    [Fact]
    public async Task DueOccurrence_ExecutesOnce_AndSucceeds()
    {
        var handler = new TestAutomationHandler();
        handler.AddDue(UserA, "2026-10-04", Due);
        var resultId = Guid.CreateVersion7();
        handler.Execute = _ => Task.FromResult(AutomationResult.Succeeded(resultId));

        var result = await Tick(handler).RunAsync();

        Assert.Equal(new AutomationTickResult(false, 1, false), result);
        var occurrence = Assert.Single(handler.Executed);
        Assert.Equal((UserA, "TestAutomation", "2026-10-04", "Europe/Rome", 1), (occurrence.UserId, occurrence.AutomationType, occurrence.OccurrenceKey, occurrence.TimeZoneId, occurrence.Attempt));
        Assert.Equal(Due.AddHours(24), occurrence.ExpiresAtUtc);

        var row = _store.Single("2026-10-04");
        Assert.Equal(AutomationExecutionStatus.Succeeded, row.Status);
        Assert.Equal(resultId, row.ResultId);
        Assert.Equal(_clock.UtcNow, row.CompletedAtUtc);
        Assert.Null(row.LeaseExpiresAtUtc);
        Assert.Null(row.NextAttemptAtUtc);
        Assert.Null(row.LastFailureCode);
    }

    [Fact]
    public async Task LaterTick_DoesNotExecuteTheSameOccurrenceAgain()
    {
        var handler = new TestAutomationHandler();
        handler.AddDue(UserA, "2026-10-04", Due);

        await Tick(handler).RunAsync();
        _clock.Advance(TimeSpan.FromMinutes(10));
        var second = await Tick(handler).RunAsync();

        Assert.Single(handler.Executed);
        Assert.Equal(0, second.Executions);
    }

    // Correctness never depends on the in-memory guard: two instances (separate guards) share one store.
    [Fact]
    public async Task TwoInstancesTickingAtOnce_ExecuteTheOccurrenceOnce()
    {
        var handler = new TestAutomationHandler();
        handler.AddDue(UserA, "2026-10-04", Due);

        await Task.WhenAll(
            Tick(handler, new AutomationTickGuard()).RunAsync(),
            Tick(handler, new AutomationTickGuard()).RunAsync());

        Assert.Single(handler.Executed);
        Assert.Single(_store.Rows);
    }

    [Fact]
    public async Task OccurrenceNotYetDue_OrAlreadyExpired_CreatesNoRow()
    {
        var handler = new TestAutomationHandler(maxLateness: TimeSpan.FromHours(1));
        handler.AddDue(UserA, "future", _clock.UtcNow.AddMinutes(1));
        handler.AddDue(UserA, "expired", _clock.UtcNow.AddHours(-1));
        handler.AddDue(UserA, "expires-now", _clock.UtcNow.AddHours(-1).AddTicks(1));

        await Tick(handler).RunAsync();

        Assert.Equal(["expires-now"], handler.Executed.Select(occurrence => occurrence.OccurrenceKey));
        Assert.Equal(["expires-now"], _store.Rows.Select(row => row.OccurrenceKey));
    }

    // ---- Failures and retries ----

    [Fact]
    public async Task RetryableFailure_IsRetriedAfterTheFixedDelays_ThenFinalAtMaxAttempts()
    {
        var handler = new TestAutomationHandler();
        handler.AddDue(UserA, "2026-10-04", Due);
        handler.Execute = _ => Task.FromResult(AutomationResult.RetryableFailure("SummaryUnavailable"));

        await Tick(handler).RunAsync();
        var row = _store.Single("2026-10-04");
        Assert.Equal((AutomationExecutionStatus.FailedRetryable, 1, "SummaryUnavailable"), (row.Status, row.AttemptCount, row.LastFailureCode));
        Assert.Equal(_clock.UtcNow.AddMinutes(10), row.NextAttemptAtUtc);
        Assert.Null(row.LeaseExpiresAtUtc);

        // Not before the next attempt is due.
        _clock.Advance(TimeSpan.FromMinutes(9));
        await Tick(handler).RunAsync();
        Assert.Single(handler.Executed);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await Tick(handler).RunAsync();
        Assert.Equal((AutomationExecutionStatus.FailedRetryable, 2), (row.Status, row.AttemptCount));
        Assert.Equal(_clock.UtcNow.AddMinutes(30), row.NextAttemptAtUtc);

        _clock.Advance(TimeSpan.FromMinutes(30));
        await Tick(handler).RunAsync();
        Assert.Equal((AutomationExecutionStatus.FailedFinal, 3, AutomationExecutionPolicy.MaxAttemptsReachedCode), (row.Status, row.AttemptCount, row.LastFailureCode));
        Assert.Equal(_clock.UtcNow, row.CompletedAtUtc);

        _clock.Advance(TimeSpan.FromHours(1));
        await Tick(handler).RunAsync();
        Assert.Equal([1, 2, 3], handler.Executed.Select(occurrence => occurrence.Attempt));
    }

    [Fact]
    public async Task RetryableFailure_ThatSucceedsLater_EndsSucceeded()
    {
        var handler = new TestAutomationHandler();
        handler.AddDue(UserA, "2026-10-04", Due);
        handler.Execute = occurrence => Task.FromResult(occurrence.Attempt == 1
            ? AutomationResult.RetryableFailure("Transient")
            : AutomationResult.Succeeded());

        await Tick(handler).RunAsync();
        _clock.Advance(TimeSpan.FromMinutes(10));
        await Tick(handler).RunAsync();

        var row = _store.Single("2026-10-04");
        Assert.Equal((AutomationExecutionStatus.Succeeded, 2), (row.Status, row.AttemptCount));
        Assert.Null(row.LastFailureCode);
    }

    [Fact]
    public async Task PermanentFailure_IsFinalImmediately()
    {
        var handler = new TestAutomationHandler();
        handler.AddDue(UserA, "2026-10-04", Due);
        handler.Execute = _ => Task.FromResult(AutomationResult.PermanentFailure("ModuleDisabled"));

        await Tick(handler).RunAsync();
        _clock.Advance(TimeSpan.FromHours(1));
        await Tick(handler).RunAsync();

        var row = _store.Single("2026-10-04");
        Assert.Equal((AutomationExecutionStatus.FailedFinal, "Permanent:ModuleDisabled"), (row.Status, row.LastFailureCode));
        Assert.Single(handler.Executed);
    }

    [Fact]
    public async Task HandlerException_IsRetryableUnhandled_WithoutStoringItsText()
    {
        var handler = new TestAutomationHandler();
        handler.AddDue(UserA, "2026-10-04", Due);
        handler.Execute = _ => throw new InvalidOperationException("secret detail user@example.com");

        await Tick(handler).RunAsync();

        var row = _store.Single("2026-10-04");
        Assert.Equal((AutomationExecutionStatus.FailedRetryable, AutomationExecutionPolicy.UnhandledCode), (row.Status, row.LastFailureCode));
    }

    [Fact]
    public async Task NotApplicable_IsSucceededWithoutResult_AndNotRediscovered()
    {
        var handler = new TestAutomationHandler();
        handler.AddDue(UserA, "2026-10-04", Due);
        handler.Execute = _ => Task.FromResult(AutomationResult.NotApplicable());

        await Tick(handler).RunAsync();
        _clock.Advance(TimeSpan.FromMinutes(10));
        await Tick(handler).RunAsync();

        var row = _store.Single("2026-10-04");
        Assert.Equal(AutomationExecutionStatus.Succeeded, row.Status);
        Assert.Null(row.ResultId);
        Assert.Single(handler.Executed);
    }

    [Fact]
    public async Task RetryDueAfterExpiry_IsNotExecuted_AndBecomesFinalExpired()
    {
        var handler = new TestAutomationHandler(maxLateness: TimeSpan.FromMinutes(15));
        handler.AddDue(UserA, "2026-10-04", Due);
        handler.Execute = _ => Task.FromResult(AutomationResult.RetryableFailure("Transient"));

        await Tick(handler).RunAsync();
        Assert.Equal(AutomationExecutionStatus.FailedRetryable, _store.Single("2026-10-04").Status);

        // The retry would be due at +10 min, but the occurrence expires at +12 min (15 − 3).
        _clock.Advance(TimeSpan.FromMinutes(12));
        await Tick(handler).RunAsync();

        var row = _store.Single("2026-10-04");
        Assert.Equal((AutomationExecutionStatus.FailedFinal, AutomationExecutionPolicy.ExpiredCode), (row.Status, row.LastFailureCode));
        Assert.Single(handler.Executed);
    }

    // ---- Lease expiry and fencing ----

    [Fact]
    public async Task StaleRunningLease_IsTakenOver_AndTheOldAttemptCannotComplete()
    {
        var handler = new TestAutomationHandler();
        var crashed = Claim(UserA, "2026-10-04");

        // Before the lease expires the row belongs to the crashed attempt.
        _clock.Advance(AutomationExecutionPolicy.Lease - TimeSpan.FromSeconds(1));
        await Tick(handler).RunAsync();
        Assert.Empty(handler.Executed);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await Tick(handler, new AutomationTickGuard()).RunAsync();

        Assert.Equal(2, Assert.Single(handler.Executed).Attempt);
        Assert.Equal(AutomationExecutionStatus.Succeeded, _store.Single("2026-10-04").Status);
        Assert.False(await _store.CompleteSucceededAsync(crashed.Id, 1, null, _clock.UtcNow, default));
        Assert.False(await _store.CompleteRetryableAsync(crashed.Id, 1, "Late", _clock.UtcNow, default));
        Assert.False(await _store.CompleteFinalAsync(crashed.Id, 1, "Late", _clock.UtcNow, default));
    }

    [Fact]
    public async Task OldAttemptFinishingAfterTakeover_IsFencedOut()
    {
        var handler = new TestAutomationHandler();
        handler.AddDue(UserA, "2026-10-04", Due);
        // Attempt 1 outlives its lease; meanwhile another instance takes the row over and succeeds.
        handler.Execute = async occurrence =>
        {
            if (occurrence.Attempt == 1)
            {
                _clock.Advance(AutomationExecutionPolicy.Lease);
                await Tick(handler, new AutomationTickGuard()).RunAsync();
                return AutomationResult.PermanentFailure("TooLate");
            }

            return AutomationResult.Succeeded();
        };

        await Tick(handler).RunAsync();

        var row = _store.Single("2026-10-04");
        Assert.Equal((AutomationExecutionStatus.Succeeded, 2, null), (row.Status, row.AttemptCount, row.LastFailureCode));
    }

    [Fact]
    public async Task StaleLeaseOfTheLastAttempt_BecomesFinal_WithoutExecuting()
    {
        var handler = new TestAutomationHandler();
        var claimed = Claim(UserA, "2026-10-04");
        var row = _store.Single("2026-10-04");
        row.AttemptCount = AutomationExecutionPolicy.MaxAttempts;

        _clock.Advance(AutomationExecutionPolicy.Lease);
        await Tick(handler).RunAsync();

        Assert.Empty(handler.Executed);
        Assert.Equal((AutomationExecutionStatus.FailedFinal, AutomationExecutionPolicy.MaxAttemptsReachedCode), (row.Status, row.LastFailureCode));
        Assert.Equal(claimed.Id, row.Id);
    }

    [Fact]
    public async Task RetriesOfUnregisteredTypes_AreNotClaimed()
    {
        var handler = new TestAutomationHandler("Other");
        Claim(UserA, "2026-10-04");

        _clock.Advance(AutomationExecutionPolicy.Lease);
        await Tick(handler).RunAsync();

        Assert.Empty(handler.Executed);
        Assert.Equal(AutomationExecutionStatus.Running, _store.Single("2026-10-04").Status);
    }

    // ---- Bounds ----

    [Fact]
    public async Task AtMost25ExecutionsPerTick_ThenMore()
    {
        var handler = new TestAutomationHandler();

        for (var index = 0; index < 30; index++)
        {
            handler.AddDue(Guid.CreateVersion7(), "2026-10-04", Due);
        }

        var first = await Tick(handler).RunAsync();

        Assert.Equal((25, true), (first.Executions, first.More));
        Assert.Equal(RunAutomationTick.MaxExecutionsPerTick, handler.Limits[0]);

        // The handler is expected to exclude claimed occurrences; this one does not, so drop them.
        handler.Due.RemoveRange(0, 25);
        _clock.Advance(AutomationTickGuard.MinimumInterval);
        var second = await Tick(handler).RunAsync();

        Assert.Equal((5, false), (second.Executions, second.More));
        Assert.Equal(30, _store.Rows.Count);
    }

    [Fact]
    public async Task RetriesAndDiscovery_ShareTheExecutionCap()
    {
        var handler = new TestAutomationHandler();

        for (var index = 0; index < 10; index++)
        {
            Claim(Guid.CreateVersion7(), "stale");
        }

        for (var index = 0; index < 20; index++)
        {
            handler.AddDue(Guid.CreateVersion7(), "2026-10-04", Due);
        }

        _clock.Advance(AutomationExecutionPolicy.Lease);
        var result = await Tick(handler).RunAsync();

        Assert.Equal((25, true), (result.Executions, result.More));
        Assert.Equal(10, handler.Executed.Count(occurrence => occurrence.Attempt == 2));
        Assert.Equal(15, handler.Limits.Single());
    }

    [Fact]
    public async Task NoNewClaimAfterTheTimeBudget()
    {
        var handler = new TestAutomationHandler();

        for (var index = 0; index < 5; index++)
        {
            handler.AddDue(Guid.CreateVersion7(), "2026-10-04", Due);
        }

        // Each execution takes 15 s: claims at 0 s and 15 s fit the 20 s budget, the third does not.
        handler.Execute = _ =>
        {
            _clock.Advance(TimeSpan.FromSeconds(15));
            return Task.FromResult(AutomationResult.Succeeded());
        };

        var result = await Tick(handler).RunAsync();

        Assert.Equal((2, true), (result.Executions, result.More));
        Assert.All(_store.Rows, row => Assert.Equal(AutomationExecutionStatus.Succeeded, row.Status));
    }

    [Fact]
    public async Task CancelledTick_ClaimsNothing()
    {
        var handler = new TestAutomationHandler();
        handler.AddDue(UserA, "2026-10-04", Due);

        var result = await Tick(handler).RunAsync(new CancellationToken(canceled: true));

        Assert.Equal((0, true), (result.Executions, result.More));
        Assert.Empty(_store.Rows);
    }

    // ---- Registry and guard ----

    [Fact]
    public async Task SeveralHandlers_EachRunTheirOwnOccurrences_InRotatedOrder()
    {
        var first = new TestAutomationHandler("First");
        var second = new TestAutomationHandler("Second");
        var order = new List<string>();
        first.Execute = _ => { order.Add("First"); return Task.FromResult(AutomationResult.Succeeded()); };
        second.Execute = _ => { order.Add("Second"); return Task.FromResult(AutomationResult.Succeeded()); };
        first.AddDue(UserA, "week-1", Due);
        second.AddDue(UserA, "week-1", Due);

        await Tick(first, second).RunAsync();

        first.AddDue(UserB, "week-1", Due);
        second.AddDue(UserB, "week-1", Due);
        _clock.Advance(AutomationTickGuard.MinimumInterval);
        await Tick(first, second).RunAsync();

        Assert.Equal(["First", "Second", "Second", "First"], order);
        Assert.Equal(4, _store.Rows.Count);
    }

    [Fact]
    public void DuplicateAutomationTypes_AreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => Tick(new TestAutomationHandler("Same"), new TestAutomationHandler("Same")));
    }

    [Fact]
    public async Task TickWithin60sOnThisInstance_IsSkipped()
    {
        var handler = new TestAutomationHandler();

        await Tick(handler).RunAsync();
        handler.AddDue(UserA, "2026-10-04", Due);
        _clock.Advance(AutomationTickGuard.MinimumInterval - TimeSpan.FromSeconds(1));

        Assert.Equal(AutomationTickResult.SkippedTick, await Tick(handler).RunAsync());
        Assert.Empty(handler.Executed);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, (await Tick(handler).RunAsync()).Executions);
    }

    [Fact]
    public async Task OverlappingTickOnThisInstance_IsSkipped()
    {
        var handler = new TestAutomationHandler();
        handler.AddDue(UserA, "2026-10-04", Due);
        AutomationTickResult? overlapping = null;
        handler.Execute = async _ =>
        {
            overlapping = await Tick(handler).RunAsync();
            return AutomationResult.Succeeded();
        };

        await Tick(handler).RunAsync();

        Assert.Equal(AutomationTickResult.SkippedTick, overlapping);
    }

    [Fact]
    public async Task DiscoveryFailure_ReleasesTheGuard()
    {
        var handler = new ThrowingHandler();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Tick(handler).RunAsync());

        _clock.Advance(AutomationTickGuard.MinimumInterval);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Tick(handler).RunAsync());
    }

    private RunAutomationTick Tick(params IAutomationHandler[] handlers) => Tick(handlers, _guard);

    private RunAutomationTick Tick(IAutomationHandler handler, AutomationTickGuard guard) => Tick([handler], guard);

    private RunAutomationTick Tick(IAutomationHandler[] handlers, AutomationTickGuard guard) => new(handlers, _store, guard, _clock);

    private AutomationExecution Claim(Guid userId, string key)
    {
        var execution = AutomationExecution.Claim(userId, "TestAutomation", key, "Europe/Rome", Due, Due.AddHours(24), _clock.UtcNow, AutomationExecutionPolicy.Lease);
        Assert.True(_store.TryClaimAsync(execution, default).Result);

        return execution;
    }

    private sealed class ThrowingHandler : IAutomationHandler
    {
        public string AutomationType => "Throwing";

        public TimeSpan MaxLateness => TimeSpan.FromHours(1);

        public Task<IReadOnlyList<DueOccurrence>> FindDueAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken) =>
            throw new InvalidOperationException();

        public Task<AutomationResult> ExecuteAsync(AutomationOccurrence occurrence, CancellationToken cancellationToken) =>
            throw new InvalidOperationException();
    }
}
