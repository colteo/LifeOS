using LifeOS.Application.Nutrition;
using LifeOS.Domain.Nutrition;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Nutrition;

// NUT-003 use cases against in-memory repositories. The clock decides "today"; no AI is involved.
public class NutritionTargetHandlersTests
{
    // 08:00 UTC = 10:00 in UTC+2: the user's local today is the same calendar day.
    private static readonly DateTimeOffset Oct1 = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private const int Offset = 120;

    private readonly InMemoryNutritionTargetRepository _targets = new();
    private readonly InMemoryMealEntryRepository _meals = new();
    private readonly FakeNutritionEstimationService _ai = new();
    private readonly ManualTimeProvider _clock = new(Oct1);

    private static DateOnly Day(int october) => new(2026, 10, october);

    private void MoveTo(int october) => _clock.UtcNow = new DateTimeOffset(2026, 10, october, 8, 0, 0, TimeSpan.Zero);

    private Task<NutritionTargetResult> SetAsync(decimal? kcal, decimal? p = null, decimal? c = null, decimal? f = null, Guid? user = null,
        int offset = Offset) =>
        new SetNutritionTargetHandler(_targets, _clock)
            .HandleAsync(user ?? TestUsers.A, new SetNutritionTargetCommand(kcal, p, c, f, offset), CancellationToken.None);

    private Task<NutritionTargetResult> RemoveAsync(Guid? user = null, int offset = Offset) =>
        new RemoveNutritionTargetHandler(_targets, _clock).HandleAsync(user ?? TestUsers.A, offset, CancellationToken.None);

    private async Task<EffectiveNutritionTarget?> OnAsync(DateOnly date, Guid? user = null)
    {
        var result = await new GetNutritionTargetHandler(_targets).HandleAsync(user ?? TestUsers.A, date, CancellationToken.None);
        Assert.Equal(NutritionStatus.Ok, result.Status);
        return result.Target;
    }

    private Task<NutritionTargetResult> CurrentAsync(Guid? user = null, int offset = Offset) =>
        new GetCurrentNutritionTargetHandler(_targets, _clock).HandleAsync(user ?? TestUsers.A, offset, CancellationToken.None);

    [Fact]
    public async Task NoHistory_MeansNoTarget()
    {
        Assert.Null(await OnAsync(Day(1)));

        var current = await CurrentAsync();
        Assert.Equal(NutritionStatus.Ok, current.Status);
        Assert.Equal(Day(1), current.Date);
        Assert.Null(current.Target);
    }

    [Fact]
    public async Task Set_AppliesFromToday_AsManual()
    {
        var result = await SetAsync(2200, 160, 240, 70);

        Assert.Equal(NutritionStatus.Ok, result.Status);
        Assert.Equal(Day(1), result.Date);
        Assert.Equal(Day(1), result.Target!.EffectiveFrom);
        Assert.Equal(NutritionTargetSource.Manual, result.Target.Source);
        Assert.Equal(2200m, (await CurrentAsync()).Target!.Values.CaloriesKcal);
        Assert.Null(await OnAsync(Day(1).AddDays(-1)));
    }

    [Fact]
    public async Task TheSpecTimeline_ResolvesEveryDayToTheTargetEffectiveThen()
    {
        await SetAsync(2200, 160, 240, 70);
        MoveTo(18);
        await SetAsync(2400, 170, 260, 75);
        MoveTo(25);
        await RemoveAsync();

        Assert.Null(await OnAsync(Day(1).AddDays(-1)));
        Assert.Equal(2200m, (await OnAsync(Day(1)))!.Values.CaloriesKcal);
        Assert.Equal(2200m, (await OnAsync(Day(17)))!.Values.CaloriesKcal);
        Assert.Equal(Day(1), (await OnAsync(Day(17)))!.EffectiveFrom);
        Assert.Equal(2400m, (await OnAsync(Day(18)))!.Values.CaloriesKcal);
        Assert.Equal(75m, (await OnAsync(Day(24)))!.Values.FatGrams);
        Assert.Null(await OnAsync(Day(25)));
        Assert.Null(await OnAsync(Day(31)));

        // History is kept: three states, none deleted.
        Assert.Equal([Day(1), Day(18), Day(25)], _targets.States.Select(state => state.EffectiveFrom).Order());
    }

    [Fact]
    public async Task SameDayEdits_ReplaceTodaysState_WithoutNewHistoryRows()
    {
        await SetAsync(2200);
        MoveTo(4);
        await SetAsync(2300, 150);
        var firstToday = _targets.States.Single(state => state.EffectiveFrom == Day(4));
        _clock.Advance(TimeSpan.FromHours(3));

        var result = await SetAsync(null, 165);

        Assert.Equal(2, _targets.States.Count);
        var today = _targets.States.Single(state => state.EffectiveFrom == Day(4));
        Assert.Equal(firstToday.Id, today.Id);
        Assert.Equal(firstToday.CreatedAtUtc, today.CreatedAtUtc);
        Assert.Equal(_clock.UtcNow, today.UpdatedAtUtc);
        Assert.Equal((null, 165m), (result.Target!.Values.CaloriesKcal, result.Target.Values.ProteinGrams));

        // Yesterday and before are unchanged.
        Assert.Equal(2200m, (await OnAsync(Day(3)))!.Values.CaloriesKcal);
    }

    [Fact]
    public async Task ATomorrowChange_LeavesEarlierDaysOnTheOldTarget()
    {
        await SetAsync(2200);
        MoveTo(2);
        await SetAsync(2500);

        Assert.Equal(2200m, (await OnAsync(Day(1)))!.Values.CaloriesKcal);
        Assert.Equal(2500m, (await OnAsync(Day(2)))!.Values.CaloriesKcal);
        Assert.Equal(2500m, (await OnAsync(Day(30)))!.Values.CaloriesKcal);
    }

    [Fact]
    public async Task Remove_CreatesANoTargetsStateFromToday_AndKeepsThePast()
    {
        await SetAsync(2200, 160);
        MoveTo(5);

        var result = await RemoveAsync();

        Assert.Equal(NutritionStatus.Ok, result.Status);
        Assert.Null(result.Target);
        Assert.Null((await CurrentAsync()).Target);
        Assert.Equal(160m, (await OnAsync(Day(4)))!.Values.ProteinGrams);
        Assert.Equal(2, _targets.States.Count);
        Assert.False(_targets.States.Single(state => state.EffectiveFrom == Day(5)).HasTargets);
    }

    [Fact]
    public async Task RemoveOnTheDayItWasSet_TurnsTodayIntoNoTargets_WithoutErasingEarlierHistory()
    {
        await SetAsync(2200);
        MoveTo(6);
        await SetAsync(2600);

        await RemoveAsync();

        Assert.Null(await OnAsync(Day(6)));
        Assert.Equal(2200m, (await OnAsync(Day(5)))!.Values.CaloriesKcal);
        Assert.Equal(2, _targets.States.Count);
    }

    [Fact]
    public async Task SetAfterRemoveOnTheSameDay_ReplacesTheNoTargetsState()
    {
        await SetAsync(2200);
        await RemoveAsync();
        await SetAsync(1800);

        Assert.Equal(1800m, (await CurrentAsync()).Target!.Values.CaloriesKcal);
        Assert.Single(_targets.States);
    }

    [Fact]
    public async Task RemoveWithNothingActive_WritesNothing()
    {
        Assert.Equal(NutritionStatus.Ok, (await RemoveAsync()).Status);
        Assert.Empty(_targets.States);

        await SetAsync(2200);
        MoveTo(3);
        await RemoveAsync();
        MoveTo(4);
        await RemoveAsync();

        Assert.Equal(2, _targets.States.Count);
    }

    [Fact]
    public async Task PartialTargets_AreValid()
    {
        var result = await SetAsync(null, 160);

        Assert.Equal(NutritionStatus.Ok, result.Status);
        Assert.Equal((null, 160m, null, null),
            (result.Target!.Values.CaloriesKcal, result.Target.Values.ProteinGrams, result.Target.Values.CarbsGrams, result.Target.Values.FatGrams));
    }

    [Theory]
    [InlineData(null, null, null, null, "target")]
    [InlineData(0.0, null, null, null, "caloriesKcal")]
    [InlineData(null, -5.0, null, null, "proteinGrams")]
    [InlineData(null, null, 1000.1, null, "carbsGrams")]
    [InlineData(10000.1, null, null, null, "caloriesKcal")]
    [InlineData(null, null, null, 0.0, "fatGrams")]
    public async Task InvalidTargets_NameTheField_AndChangeNothing(double? kcal, double? p, double? c, double? f, string field)
    {
        var result = await SetAsync((decimal?)kcal, (decimal?)p, (decimal?)c, (decimal?)f);

        Assert.Equal(NutritionStatus.Invalid, result.Status);
        Assert.Equal(field, result.Field);
        Assert.DoesNotContain("Parameter", result.Message);
        Assert.Empty(_targets.States);
    }

    [Theory]
    [InlineData(841)]
    [InlineData(-841)]
    public async Task OutOfRangeOffsets_AreInvalid(int offset)
    {
        Assert.Equal("utcOffsetMinutes", (await SetAsync(2200, offset: offset)).Field);
        Assert.Equal("utcOffsetMinutes", (await RemoveAsync(offset: offset)).Field);
        Assert.Equal("utcOffsetMinutes", (await CurrentAsync(offset: offset)).Field);
        Assert.Empty(_targets.States);
    }

    [Fact]
    public async Task Today_IsTheUsersLocalDay_NotTheServersUtcDate()
    {
        // 22:30 UTC on 3 October: already 4 October at UTC+2, still 3 October at UTC-5.
        _clock.UtcNow = new DateTimeOffset(2026, 10, 3, 22, 30, 0, TimeSpan.Zero);

        Assert.Equal(Day(4), (await SetAsync(2200, offset: 120)).Target!.EffectiveFrom);
        Assert.Equal(Day(3), (await SetAsync(2000, user: TestUsers.B, offset: -300)).Target!.EffectiveFrom);
        Assert.Equal(Day(4), (await CurrentAsync(offset: 120)).Date);
        Assert.Equal(Day(3), (await CurrentAsync(offset: 0)).Date);

        // At UTC the user's 4 October target does not apply yet.
        Assert.Null((await CurrentAsync(offset: 0)).Target);
    }

    [Fact]
    public async Task Targets_AreScopedToTheUser()
    {
        await SetAsync(2200);

        Assert.Null(await OnAsync(Day(1), TestUsers.B));
        Assert.Null((await CurrentAsync(TestUsers.B)).Target);

        await RemoveAsync(TestUsers.B);
        Assert.Equal(2200m, (await CurrentAsync()).Target!.Values.CaloriesKcal);
        Assert.Single(_targets.States);
    }

    [Fact]
    public async Task GetByDate_RejectsNonCalendarDates()
    {
        var result = await new GetNutritionTargetHandler(_targets).HandleAsync(TestUsers.A, DateOnly.MaxValue, CancellationToken.None);

        Assert.Equal(NutritionStatus.Invalid, result.Status);
        Assert.Equal("date", result.Field);
    }

    // ---- Daily summaries ----

    [Fact]
    public async Task DailySummary_CarriesTheTargetEffectiveOnThatDay()
    {
        await SetAsync(2200, 160, null, 70);
        MoveTo(10);
        await SetAsync(2400);
        MoveTo(12);
        await RemoveAsync();

        Assert.Null((await SummaryAsync(Day(1).AddDays(-1))).Target);
        var early = (await SummaryAsync(Day(9))).Target!;
        Assert.Equal((2200m, 160m, null, 70m), (early.CaloriesKcal, early.ProteinGrams, early.CarbsGrams, early.FatGrams));
        Assert.Equal(2400m, (await SummaryAsync(Day(11))).Target!.CaloriesKcal);
        Assert.Null((await SummaryAsync(Day(12))).Target);
    }

    [Fact]
    public async Task DailySummary_TotalsAreUnchangedByTargets()
    {
        await SetAsync(2200, 160, 240, 70);
        await new CreateMealHandler(_meals, _clock).HandleAsync(TestUsers.A,
            new CreateMealCommand("Pasta", null, Day(1), new TimeOnly(13, 0), Offset), CancellationToken.None);

        var summary = await SummaryAsync(Day(1));

        Assert.Equal((1, 0, 0m), (summary.MealCount, summary.AnalyzedMealCount, summary.CaloriesKcal));
        Assert.False(summary.AllAnalyzed);
        Assert.Equal(2200m, summary.Target!.CaloriesKcal);
    }

    [Fact]
    public async Task AnalyzeDay_ReturnsTheSummaryWithItsTarget_AndTheTargetDoesNotChangeWhatAiIsAsked()
    {
        await SetAsync(2200);
        await new CreateMealHandler(_meals, _clock).HandleAsync(TestUsers.A,
            new CreateMealCommand("Pasta", null, Day(1), new TimeOnly(13, 0), Offset), CancellationToken.None);

        var result = await new AnalyzeDayHandler(_meals, _targets, new MealNutritionEstimation(_ai, _meals, _clock))
            .HandleAsync(TestUsers.A, Day(1), CancellationToken.None);

        Assert.Equal(2200m, result.Summary!.Target!.CaloriesKcal);
        Assert.Equal([new MealEstimationInput("Pasta", null)], _ai.Inputs);
    }

    [Fact]
    public async Task SettingAndRemovingTargets_NeverCallAi()
    {
        await SetAsync(2200);
        await CurrentAsync();
        await RemoveAsync();

        Assert.Empty(_ai.Inputs);
    }

    private async Task<DailyNutritionSummary> SummaryAsync(DateOnly day) =>
        (await new GetDailyNutritionSummaryHandler(_meals, _targets).HandleAsync(TestUsers.A, day, CancellationToken.None)).Summary!;
}
