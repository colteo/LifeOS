using LifeOS.Application.Automation;
using LifeOS.Domain.Automation;

namespace LifeOS.UnitTests.Automation;

public class AutomationExecutionTests
{
    private static readonly DateTimeOffset Due = new(2026, 10, 4, 18, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.CreateVersion7();

    [Fact]
    public void Claim_StartsAttemptOne_RunningWithALease()
    {
        var now = Due.AddMinutes(3);

        var execution = AutomationExecution.Claim(UserId, "WeeklyReview", "2026-10-04", "Europe/Rome", Due, Due.AddHours(24), now, TimeSpan.FromMinutes(5));

        Assert.Equal(7, execution.Id.Version);
        Assert.Equal((AutomationExecutionStatus.Running, 1), (execution.Status, execution.AttemptCount));
        Assert.Equal((now, now, now.AddMinutes(5)), (execution.CreatedAtUtc, execution.StartedAtUtc, execution.LeaseExpiresAtUtc));
        Assert.Null(execution.NextAttemptAtUtc);
        Assert.Null(execution.CompletedAtUtc);
        Assert.Null(execution.LastFailureCode);
    }

    [Fact]
    public void Claim_NormalizesInstantsToUtc()
    {
        var rome = TimeSpan.FromHours(2);

        var execution = AutomationExecution.Claim(UserId, "T", "k", "Europe/Rome", Due.ToOffset(rome), Due.AddHours(1).ToOffset(rome), Due.ToOffset(rome), TimeSpan.FromMinutes(5));

        Assert.Equal(TimeSpan.Zero, execution.ScheduledForUtc.Offset);
        Assert.Equal(TimeSpan.Zero, execution.CreatedAtUtc.Offset);
    }

    [Theory]
    [InlineData(-1)] // not yet due
    [InlineData(60)] // at expiry
    [InlineData(61)] // after expiry
    public void Claim_OutsideTheDueWindow_IsRefused(int minutesAfterDue)
    {
        Assert.Throws<InvalidOperationException>(() => AutomationExecution.Claim(
            UserId, "T", "k", "Europe/Rome", Due, Due.AddHours(1), Due.AddMinutes(minutesAfterDue), TimeSpan.FromMinutes(5)));
    }

    [Theory]
    [InlineData("", "k")]
    [InlineData("Weekly Review", "k")]
    [InlineData("T", "")]
    [InlineData("T", "key with spaces")]
    [InlineData("T", "ключ")]
    public void Claim_RejectsTypesAndKeysThatAreNotStableCodes(string type, string key)
    {
        Assert.Throws<ArgumentException>(() => AutomationExecution.Claim(UserId, type, key, "Europe/Rome", Due, Due.AddHours(1), Due, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Claim_RejectsInvalidArguments()
    {
        Assert.Throws<ArgumentException>(() => AutomationExecution.Claim(Guid.Empty, "T", "k", "Europe/Rome", Due, Due.AddHours(1), Due, TimeSpan.FromMinutes(5)));
        Assert.Throws<ArgumentException>(() => AutomationExecution.Claim(UserId, "T", "k", " ", Due, Due.AddHours(1), Due, TimeSpan.FromMinutes(5)));
        Assert.Throws<ArgumentException>(() => AutomationExecution.Claim(UserId, "T", "k", new string('a', 65), Due, Due.AddHours(1), Due, TimeSpan.FromMinutes(5)));
        Assert.Throws<ArgumentException>(() => AutomationExecution.Claim(UserId, "T", "k", "Europe/Rome", Due, Due, Due, TimeSpan.FromMinutes(5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => AutomationExecution.Claim(UserId, "T", "k", "Europe/Rome", Due, Due.AddHours(1), Due, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("Expired", true)]
    [InlineData("Permanent:ModuleDisabled", true)]
    [InlineData("0192f0c3-0000-7000-8000-000000000001:2026-10-04T20.00", true)]
    [InlineData("Object reference not set to an instance of an object.", false)]
    [InlineData("a\nb", false)]
    [InlineData("", false)]
    public void StableCodes_AreShortMachineReadableTokens(string code, bool stable)
    {
        Assert.Equal(stable, AutomationExecution.IsStableCode(code));
    }

    [Fact]
    public void StableCodes_AreAtMost64Characters()
    {
        Assert.True(AutomationExecution.IsStableCode(new string('a', 64)));
        Assert.False(AutomationExecution.IsStableCode(new string('a', 65)));
    }

    [Fact]
    public void Results_AcceptOnlyStableFailureCodes()
    {
        Assert.Throws<ArgumentException>(() => AutomationResult.RetryableFailure("Timeout after 30s"));
        Assert.Throws<ArgumentException>(() => AutomationResult.PermanentFailure(new string('a', 55)));
        Assert.Equal(new string('a', 54), Assert.IsType<AutomationResult.Permanent>(AutomationResult.PermanentFailure(new string('a', 54))).Code);
    }

    // ---- Policy (AUTO-001 §10) ----

    [Fact]
    public void Policy_UsesTheDesignValues()
    {
        Assert.Equal(3, AutomationExecutionPolicy.MaxAttempts);
        Assert.Equal(TimeSpan.FromMinutes(5), AutomationExecutionPolicy.Lease);
        Assert.Equal([TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)], AutomationExecutionPolicy.RetryDelays);
        Assert.Equal(25, RunAutomationTick.MaxExecutionsPerTick);
        Assert.Equal(TimeSpan.FromSeconds(20), RunAutomationTick.TimeBudget);
        Assert.Equal(TimeSpan.FromSeconds(60), AutomationTickGuard.MinimumInterval);
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 30)]
    public void Policy_RetryableFailureWithAttemptsLeft_IsRetriedAfterTheFixedDelay(int attempt, int delayMinutes)
    {
        var outcome = AutomationExecutionPolicy.AfterRetryableFailure(attempt, "Transient", Due, Due.AddHours(24));

        Assert.False(outcome.IsFinal);
        Assert.Equal(("Transient", Due.AddMinutes(delayMinutes)), (outcome.Code, outcome.NextAttemptAtUtc!.Value));
    }

    [Fact]
    public void Policy_RetryableFailureOfTheLastAttempt_IsFinal()
    {
        var outcome = AutomationExecutionPolicy.AfterRetryableFailure(3, "Transient", Due, Due.AddHours(24));

        Assert.True(outcome.IsFinal);
        Assert.Equal(AutomationExecutionPolicy.MaxAttemptsReachedCode, outcome.Code);
    }

    [Fact]
    public void Policy_RetryableFailureAtOrAfterExpiry_IsFinal()
    {
        var outcome = AutomationExecutionPolicy.AfterRetryableFailure(1, "Transient", Due.AddHours(24), Due.AddHours(24));

        Assert.True(outcome.IsFinal);
        Assert.Equal(AutomationExecutionPolicy.ExpiredCode, outcome.Code);
    }
}
