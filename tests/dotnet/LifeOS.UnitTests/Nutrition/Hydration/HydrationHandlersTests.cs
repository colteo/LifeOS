using LifeOS.Application.Nutrition.Hydration;
using LifeOS.Domain.Nutrition.Hydration;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Nutrition.Hydration;

// HYD-001 §7 use cases on the in-memory repository. The clock is Wednesday 14 Oct 2026 10:00 in Rome
// (08:00 UTC); a test changes it when it needs another local today.
public class HydrationHandlersTests
{
    private const int Rome = 120;
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 14);

    private readonly InMemoryHydrationRepository _repository = new();
    private ManualTimeProvider _clock = new(Now);

    // ---- Add ----

    [Fact]
    public async Task Add_StoresTheIntake_AndReturnsTheUpdatedDay()
    {
        await AddAsync(500, Today, new TimeOnly(8, 10));

        var result = await AddAsync(250, Today, new TimeOnly(9, 30));

        Assert.Equal(HydrationResultStatus.Ok, result.Status);
        Assert.Equal((250, Today, new TimeOnly(9, 30)), (result.Value!.Intake.AmountMl, result.Value.Intake.DiaryDate, result.Value.Intake.DiaryTime));
        Assert.Equal(new HydrationDay(Today, 750, null), result.Value.Day);
        Assert.Equal(2, _repository.Intakes.Count);
        Assert.All(_repository.Intakes, intake => Assert.Equal(TestUsers.A, intake.UserId));
    }

    [Fact]
    public async Task Add_AllowsBackdating() =>
        Assert.Equal(HydrationResultStatus.Ok, (await AddAsync(250, Today.AddDays(-40), new TimeOnly(12, 0))).Status);

    [Fact]
    public async Task Add_RejectsAFutureDay_FromTheCallersLocalToday()
    {
        var result = await AddAsync(250, Today.AddDays(1), new TimeOnly(0, 5));

        Assert.Equal((HydrationResultStatus.Invalid, "date", HydrationLimits.FutureDayMessage), (result.Status, result.Field, result.Message));
        Assert.Empty(_repository.Intakes);
    }

    // 23:30 UTC on 14 Oct is already 15 Oct at +14 h, but still 14 Oct at -12 h.
    [Theory]
    [InlineData(840, "2026-10-15", true)]
    [InlineData(840, "2026-10-16", false)]
    [InlineData(-720, "2026-10-14", true)]
    [InlineData(-720, "2026-10-15", false)]
    public async Task Add_LocalTodayFollowsTheOffset_AroundMidnight(int offset, string date, bool accepted)
    {
        _clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 14, 23, 30, 0, TimeSpan.Zero));

        var result = await AddAsync(250, DateOnly.Parse(date), new TimeOnly(9, 0), offset);

        Assert.Equal(accepted ? HydrationResultStatus.Ok : HydrationResultStatus.Invalid, result.Status);
    }

    [Theory]
    [InlineData(0, 120, "amountMl")]
    [InlineData(4001, 120, "amountMl")]
    [InlineData(250, 900, "utcOffsetMinutes")]
    public async Task Add_InvalidInput_IsFieldKeyed(int amount, int offset, string field)
    {
        var result = await AddAsync(amount, Today, new TimeOnly(9, 0), offset);

        Assert.Equal((HydrationResultStatus.Invalid, field), (result.Status, result.Field));
        Assert.DoesNotContain("(Parameter", result.Message);
    }

    // ---- Update / delete ----

    [Fact]
    public async Task Update_ChangesAmountAndTime_OnTheSameDay()
    {
        var added = (await AddAsync(250, Today.AddDays(-1), new TimeOnly(8, 0))).Value!.Intake;
        await AddAsync(500, Today.AddDays(-1), new TimeOnly(9, 0));

        var result = await new UpdateHydrationIntakeHandler(_repository, Reader(), _clock)
            .HandleAsync(TestUsers.A, added.Id, new UpdateHydrationIntakeCommand(330, new TimeOnly(7, 45)), CancellationToken.None);

        Assert.Equal(HydrationResultStatus.Ok, result.Status);
        Assert.Equal((330, Today.AddDays(-1), new TimeOnly(7, 45)), (result.Value!.Intake.AmountMl, result.Value.Intake.DiaryDate, result.Value.Intake.DiaryTime));
        Assert.Equal(830, result.Value.Day.TotalMl);
    }

    [Fact]
    public async Task Update_AnotherUsersIntake_IsNotFound()
    {
        var added = (await AddAsync(250, Today, new TimeOnly(8, 0))).Value!.Intake;

        var result = await new UpdateHydrationIntakeHandler(_repository, Reader(), _clock)
            .HandleAsync(TestUsers.B, added.Id, new UpdateHydrationIntakeCommand(500, new TimeOnly(8, 0)), CancellationToken.None);

        Assert.Equal(HydrationResultStatus.NotFound, result.Status);
        Assert.Equal(250, _repository.Intakes.Single().AmountMl);
    }

    [Fact]
    public async Task Update_InvalidAmount_IsFieldKeyed()
    {
        var added = (await AddAsync(250, Today, new TimeOnly(8, 0))).Value!.Intake;

        var result = await new UpdateHydrationIntakeHandler(_repository, Reader(), _clock)
            .HandleAsync(TestUsers.A, added.Id, new UpdateHydrationIntakeCommand(0, new TimeOnly(8, 0)), CancellationToken.None);

        Assert.Equal((HydrationResultStatus.Invalid, "amountMl"), (result.Status, result.Field));
    }

    [Fact]
    public async Task Delete_ReturnsTheDayAfterwards_ForUndo()
    {
        await AddAsync(500, Today, new TimeOnly(8, 0));
        var added = (await AddAsync(250, Today, new TimeOnly(9, 0))).Value!.Intake;
        var handler = new DeleteHydrationIntakeHandler(_repository, Reader());

        Assert.Equal(HydrationResultStatus.NotFound, (await handler.HandleAsync(TestUsers.B, added.Id, CancellationToken.None)).Status);

        var result = await handler.HandleAsync(TestUsers.A, added.Id, CancellationToken.None);

        Assert.Equal(new HydrationDay(Today, 500, null), result.Value);
        Assert.Equal(HydrationResultStatus.NotFound, (await handler.HandleAsync(TestUsers.A, added.Id, CancellationToken.None)).Status);
    }

    // ---- Reads ----

    [Fact]
    public async Task Day_ListsOnlyThatDaysIntakes_NewestFirst()
    {
        await AddAsync(250, Today, new TimeOnly(8, 10));
        await AddAsync(500, Today, new TimeOnly(13, 20));
        await AddAsync(500, Today, new TimeOnly(10, 45));
        await AddAsync(330, Today.AddDays(-1), new TimeOnly(22, 0));
        await AddAsync(250, Today, new TimeOnly(12, 0), user: TestUsers.B);

        var result = await new GetHydrationDayHandler(_repository, Reader()).HandleAsync(TestUsers.A, Today, CancellationToken.None);

        Assert.Equal([new TimeOnly(13, 20), new TimeOnly(10, 45), new TimeOnly(8, 10)], result.Value!.Intakes.Select(intake => intake.DiaryTime));
        Assert.Equal(1250, result.Value.Day.TotalMl);
    }

    [Fact]
    public async Task Days_HaveZerosForEmptyDays_AndAnAverageOverEveryDay()
    {
        await AddAsync(1000, Today.AddDays(-2), new TimeOnly(9, 0));
        await AddAsync(1001, Today, new TimeOnly(9, 0));

        var result = await new GetHydrationDaysHandler(Reader()).HandleAsync(TestUsers.A, Today.AddDays(-3), Today, CancellationToken.None);

        Assert.Equal([0, 1000, 0, 1001], result.Value!.Days.Select(day => day.TotalMl));
        Assert.Equal([Today.AddDays(-3), Today.AddDays(-2), Today.AddDays(-1), Today], result.Value.Days.Select(day => day.Date));
        // 2001 / 4 = 500.25 → 500; no targets yet, so nothing is met.
        Assert.Equal(new HydrationRangeSummary(0, 0, 500), result.Value.Summary);
    }

    [Fact]
    public void Summary_RoundsTheAverageHalfAwayFromZero()
    {
        HydrationDay[] days = [new(Today, 1001, 1000), new(Today.AddDays(1), 0, 1000)];

        // 500.5 → 501; one of two days with a target is met.
        Assert.Equal(new HydrationRangeSummary(2, 1, 501), GetHydrationDaysHandler.Summarize(days));
    }

    [Theory]
    [InlineData("2026-10-14", "2026-10-13", "to")]
    [InlineData("2026-07-14", "2026-10-14", "to")]
    public async Task Days_RejectReversedOrTooLongRanges(string from, string to, string field)
    {
        var result = await new GetHydrationDaysHandler(Reader()).HandleAsync(TestUsers.A, DateOnly.Parse(from), DateOnly.Parse(to), CancellationToken.None);

        Assert.Equal((HydrationResultStatus.Invalid, field), (result.Status, result.Field));
    }

    [Fact]
    public async Task Days_Accept92Days()
    {
        var result = await new GetHydrationDaysHandler(Reader()).HandleAsync(TestUsers.A, Today.AddDays(-91), Today, CancellationToken.None);

        Assert.Equal(92, result.Value!.Days.Count);
    }

    // ---- H2: target and quick-add ----

    [Fact]
    public async Task Target_AppliesFromLocalToday_AndPastDaysKeepTheirs()
    {
        _clock = new ManualTimeProvider(Now.AddDays(-3));
        await SetTargetAsync(2000);
        _clock = new ManualTimeProvider(Now);
        await AddAsync(2000, Today.AddDays(-1), new TimeOnly(18, 0));

        var result = await SetTargetAsync(2500);

        Assert.Equal((Today, 2500, (DateOnly?)Today), (result.Value!.Date, result.Value.TargetMl, result.Value.TargetEffectiveFrom));
        Assert.Equal([150, 250, 500], result.Value.QuickAddMl);

        var days = await Reader().GetDaysAsync(TestUsers.A, Today.AddDays(-4), Today, CancellationToken.None);
        Assert.Equal([null, 2000, 2000, 2000, 2500], days.Select(day => day.TargetMl));
        Assert.True(days[3].IsTargetMet);
    }

    [Fact]
    public async Task Target_SavedTwiceOnTheSameDay_KeepsTheLast()
    {
        await SetTargetAsync(2000);
        await SetTargetAsync(3000);

        var settings = await new GetHydrationSettingsHandler(_repository).HandleAsync(TestUsers.A, Today, CancellationToken.None);

        Assert.Equal((3000, (DateOnly?)Today), (settings.Value!.TargetMl, settings.Value.TargetEffectiveFrom));
    }

    [Fact]
    public async Task Target_Removed_FromToday_YesterdayStillMet()
    {
        _clock = new ManualTimeProvider(Now.AddDays(-1));
        await SetTargetAsync(2000);
        await AddAsync(2000, Today.AddDays(-1), new TimeOnly(18, 0));
        _clock = new ManualTimeProvider(Now);

        var removed = await SetTargetAsync(null);
        await AddAsync(2500, Today, new TimeOnly(9, 0));

        Assert.Equal((null, (DateOnly?)Today), (removed.Value!.TargetMl, removed.Value.TargetEffectiveFrom));
        var range = await new GetHydrationDaysHandler(Reader()).HandleAsync(TestUsers.A, Today.AddDays(-1), Today, CancellationToken.None);
        Assert.Equal([true, false], range.Value!.Days.Select(day => day.IsTargetMet));
        Assert.Equal(new HydrationRangeSummary(1, 1, 2250), range.Value.Summary);
    }

    [Theory]
    [InlineData(249, 120, "targetMl")]
    [InlineData(10001, 120, "targetMl")]
    [InlineData(2000, -900, "utcOffsetMinutes")]
    public async Task Target_InvalidInput_IsFieldKeyed(int target, int offset, string field)
    {
        var result = await new SetHydrationTargetHandler(_repository, _clock).HandleAsync(TestUsers.A, target, offset, CancellationToken.None);

        Assert.Equal((HydrationResultStatus.Invalid, field), (result.Status, result.Field));
    }

    [Fact]
    public async Task Target_IsPerUser()
    {
        await SetTargetAsync(2000);

        var other = await new GetHydrationSettingsHandler(_repository).HandleAsync(TestUsers.B, Today, CancellationToken.None);

        Assert.Null(other.Value!.TargetMl);
    }

    [Fact]
    public async Task QuickAdd_DefaultsUntilChanged_ThenUsesTheSavedAmounts()
    {
        var get = new GetHydrationSettingsHandler(_repository);
        Assert.Equal([150, 250, 500], (await get.HandleAsync(TestUsers.A, Today, CancellationToken.None)).Value!.QuickAddMl);

        var saved = await new SetHydrationQuickAddHandler(_repository, _clock).HandleAsync(TestUsers.A, [200, 330, 750], Rome, CancellationToken.None);

        Assert.Equal([200, 330, 750], saved.Value!.QuickAddMl);
        Assert.Equal([200, 330, 750], (await get.HandleAsync(TestUsers.A, Today, CancellationToken.None)).Value!.QuickAddMl);
        Assert.Equal([150, 250, 500], (await get.HandleAsync(TestUsers.B, Today, CancellationToken.None)).Value!.QuickAddMl);
    }

    [Fact]
    public async Task QuickAdd_InvalidAmount_IsKeyedByIndex()
    {
        var result = await new SetHydrationQuickAddHandler(_repository, _clock).HandleAsync(TestUsers.A, [200, 0, 750], Rome, CancellationToken.None);

        Assert.Equal((HydrationResultStatus.Invalid, "amountsMl[1]"), (result.Status, result.Field));
    }

    // The port other modules use and the diary's own reads agree: one derivation rule.
    [Fact]
    public async Task DayReader_MatchesTheHandlers()
    {
        _clock = new ManualTimeProvider(Now.AddDays(-2));
        await SetTargetAsync(1500);
        _clock = new ManualTimeProvider(Now);
        await AddAsync(1500, Today.AddDays(-1), new TimeOnly(9, 0));
        await AddAsync(700, Today, new TimeOnly(9, 0));

        IHydrationDayReader port = Reader();
        var fromPort = await port.GetDaysAsync(TestUsers.A, Today.AddDays(-2), Today, CancellationToken.None);
        var range = await new GetHydrationDaysHandler(Reader()).HandleAsync(TestUsers.A, Today.AddDays(-2), Today, CancellationToken.None);
        var day = await new GetHydrationDayHandler(_repository, Reader()).HandleAsync(TestUsers.A, Today, CancellationToken.None);

        Assert.Equal(fromPort, range.Value!.Days);
        Assert.Equal(fromPort[^1], day.Value!.Day);
        Assert.Equal(new HydrationDay(Today, 700, 1500), fromPort[^1]);
        Assert.True(fromPort[1].IsTargetMet);
    }

    // ---- Helpers ----

    private HydrationDayReader Reader() => new(_repository);

    private Task<HydrationResult<HydrationSettingsView>> SetTargetAsync(int? target) =>
        new SetHydrationTargetHandler(_repository, _clock).HandleAsync(TestUsers.A, target, Rome, CancellationToken.None);

    private Task<HydrationResult<HydrationIntakeMutation>> AddAsync(int amount, DateOnly date, TimeOnly time, int offset = Rome, Guid? user = null) =>
        new AddHydrationIntakeHandler(_repository, Reader(), _clock)
            .HandleAsync(user ?? TestUsers.A, new AddHydrationIntakeCommand(amount, date, time, offset), CancellationToken.None);
}
