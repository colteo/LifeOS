using LifeOS.App.Services.Gym;
using LifeOS.Contracts.Gym.Sessions;

namespace LifeOS.UnitTests.Gym;

// The active workout's client helpers (plain .NET files of the MAUI app): input parsing, prefills,
// execution order and timers derived from server timestamps.
public class WorkoutSessionAppHelpersTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 18, 0, 0, TimeSpan.Zero);

    // ---- Input parsing ----

    [Theory]
    [InlineData("82.5", 82.5)]
    [InlineData("82,5", 82.5)]
    [InlineData(" 100 ", 100)]
    [InlineData("2.25", 2.25)]
    [InlineData("1000", 1000)]
    public void Weight_AcceptsKilogramsWithCommaOrDot(string text, double expected)
    {
        Assert.True(WorkoutSessionDisplay.TryParseWeight(text, out var weight, out var error));
        Assert.Equal((decimal)expected, weight);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Weight_EmptyIsBodyweight(string? text)
    {
        Assert.True(WorkoutSessionDisplay.TryParseWeight(text, out var weight, out _));
        Assert.Null(weight);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1000.5")]
    [InlineData("82.555")]
    [InlineData("8.2.5")]
    [InlineData("abc")]
    public void Weight_RejectsInvalidValues(string text)
    {
        Assert.False(WorkoutSessionDisplay.TryParseWeight(text, out var weight, out var error));
        Assert.Null(weight);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("8", 8)]
    [InlineData(" 12 ", 12)]
    [InlineData("999", 999)]
    public void Reps_AcceptsAWholeNumber(string text, int expected)
    {
        Assert.True(WorkoutSessionDisplay.TryParseReps(text, out var reps, out _));
        Assert.Equal(expected, reps);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("1000")]
    [InlineData("8.5")]
    [InlineData("-3")]
    public void Reps_RejectsInvalidValues(string? text)
    {
        Assert.False(WorkoutSessionDisplay.TryParseReps(text, out _, out var error));
        Assert.NotNull(error);
    }

    // ---- Display ----

    [Fact]
    public void Performed_ShowsLoadOrBodyweight()
    {
        Assert.Equal("82.5 kg × 8", WorkoutSessionDisplay.Performed(Set(1, 8, 8, actual: 8, weight: 82.5m)));
        Assert.Equal("BW × 12", WorkoutSessionDisplay.Performed(Set(1, 8, 8, actual: 12)));
        Assert.Equal("8–10 reps", WorkoutSessionDisplay.Target(Set(1, 8, 10)));
    }

    [Fact]
    public void Prefill_UsesRecordedValues_ThenThePreviousSetOfTheExercise_ThenTheTarget()
    {
        var exercise = Exercise(1, Set(1, 8, 10, actual: 9, weight: 80m), Set(2, 8, 10), Set(3, 6, 6));
        var first = Exercise(1, Set(1, 8, 10), Set(2, 8, 10));

        Assert.Equal(("80", "9"), WorkoutSessionDisplay.Prefill(exercise, exercise.Sets[0]));
        Assert.Equal(("80", "9"), WorkoutSessionDisplay.Prefill(exercise, exercise.Sets[1]));
        Assert.Equal(("", "8"), WorkoutSessionDisplay.Prefill(first, first.Sets[0]));
    }

    // ---- Execution order (Single / Superset) ----

    [Fact]
    public void Rows_Single_EachSetEndsItsRound()
    {
        var block = Block("Single", Exercise(1, Set(1, 8, 8), Set(2, 8, 8)));

        var rows = WorkoutSessionDisplay.Rows(block);

        Assert.Equal([("Set 1", true), ("Set 2", true)], rows.Select(row => (row.Label, row.EndsRound)));
    }

    [Fact]
    public void Rows_Superset_AlternatesAAndB_RestFollowsB_AndKeepsUnevenSets()
    {
        var block = Block("Superset",
            Exercise(1, Set(1, 10, 10), Set(2, 10, 10), Set(3, 10, 10)),
            Exercise(2, Set(1, 12, 12), Set(2, 12, 12)));

        var rows = WorkoutSessionDisplay.Rows(block);

        Assert.Equal(
            [("A1", false), ("B1", true), ("A2", false), ("B2", true), ("A3", true)],
            rows.Select(row => (row.Label, row.EndsRound)));
    }

    [Fact]
    public void NextSet_IsTheFirstPendingSetInExecutionOrder()
    {
        var a = Exercise(1, Set(1, 10, 10, actual: 10), Set(2, 10, 10));
        var b = Exercise(2, Set(1, 12, 12), Set(2, 12, 12));
        var session = Session(rest: null, Block("Superset", a, b));

        Assert.Equal(b.Sets[0].Id, WorkoutSessionDisplay.NextSetId(session));
    }

    // ---- Timers ----

    [Fact]
    public void Elapsed_IsDerivedFromTheServerStartTime()
    {
        var clock = WorkoutClock.Align(serverTimeUtc: Start.AddMinutes(5), deviceNowUtc: Start.AddMinutes(5));

        Assert.Equal(TimeSpan.FromMinutes(12), clock.Elapsed(Start, null, Start.AddMinutes(12)));
        Assert.Equal(TimeSpan.FromMinutes(47), clock.Elapsed(Start, Start.AddMinutes(47), Start.AddHours(3)));
        Assert.Equal("12:00", WorkoutClock.Format(TimeSpan.FromMinutes(12)));
        Assert.Equal("1:02:05", WorkoutClock.Format(new TimeSpan(1, 2, 5)));
    }

    [Fact]
    public void Elapsed_CorrectsAWrongDeviceClock()
    {
        // The device clock runs 10 minutes behind the server.
        var clock = WorkoutClock.Align(serverTimeUtc: Start.AddMinutes(5), deviceNowUtc: Start.AddMinutes(-5));

        Assert.Equal(TimeSpan.FromMinutes(6), clock.Elapsed(Start, null, Start.AddMinutes(-4)));
        Assert.Equal(TimeSpan.Zero, WorkoutClock.Device.Elapsed(Start, null, Start.AddMinutes(-4)));
    }

    [Fact]
    public void RestCountdown_IsTheEndTimeMinusNow_RoundedUp()
    {
        var rest = new WorkoutRestResponse(Start, Start.AddSeconds(90), 90);
        var clock = WorkoutClock.Device;

        Assert.Equal(TimeSpan.FromSeconds(90), clock.RestRemaining(rest, Start));
        Assert.Equal("1:00", WorkoutClock.FormatCountdown(clock.RestRemaining(rest, Start.AddSeconds(30.4))!.Value));
        Assert.Equal("0:01", WorkoutClock.FormatCountdown(clock.RestRemaining(rest, Start.AddSeconds(89.5))!.Value));
        Assert.Null(clock.RestRemaining(rest, Start.AddSeconds(90)));
        Assert.Null(clock.RestRemaining(null, Start));
    }

    [Fact]
    public void RestCountdown_AfterBackgrounding_ContinuesFromTheTimestamps()
    {
        var rest = new WorkoutRestResponse(Start, Start.AddSeconds(120), 120);
        var clock = WorkoutClock.Align(Start.AddSeconds(1), Start.AddSeconds(1));

        // The app was in the background for 75 seconds: no ticks ran, the countdown still matches.
        Assert.Equal(TimeSpan.FromSeconds(44), clock.RestRemaining(rest, Start.AddSeconds(76)));

        // Back after the rest ended: nothing to show.
        Assert.Null(clock.RestRemaining(rest, Start.AddMinutes(5)));
    }

    [Fact]
    public void RestSkips_AreRememberedPerRest()
    {
        var skips = new RestSkips();
        var sessionId = Guid.NewGuid();

        skips.Skip(sessionId, Start);

        Assert.True(skips.IsSkipped(sessionId, Start));
        Assert.False(skips.IsSkipped(sessionId, Start.AddMinutes(3)));
        Assert.False(skips.IsSkipped(Guid.NewGuid(), Start));
    }

    // ---- Helpers ----

    private static WorkoutSessionSetResponse Set(int position, int min, int max, int? actual = null, decimal? weight = null) =>
        new(Guid.NewGuid(), position, min, max, actual, weight, actual is null ? null : Start.AddMinutes(position));

    private static WorkoutSessionExerciseResponse Exercise(int position, params WorkoutSessionSetResponse[] sets) =>
        new(Guid.NewGuid(), position, Guid.NewGuid(), $"Exercise {position}", null, sets);

    private static WorkoutSessionBlockResponse Block(string kind, params WorkoutSessionExerciseResponse[] exercises) =>
        new(Guid.NewGuid(), 1, kind, 90, exercises);

    private static WorkoutSessionResponse Session(WorkoutRestResponse? rest, params WorkoutSessionBlockResponse[] blocks) =>
        new(Guid.NewGuid(), null, null, "Program", "Push", "InProgress", Start, null, 0, 0, rest, Start, blocks);
}
