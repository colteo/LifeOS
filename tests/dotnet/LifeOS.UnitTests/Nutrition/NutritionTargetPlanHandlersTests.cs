using LifeOS.Application.Nutrition;
using LifeOS.Domain.Nutrition;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Nutrition;

// NUT-003 use cases against in-memory repositories: plan creation/editing/deletion with overlap
// rejection, daily overrides, resolution, and the daily summary. No AI and no Gym data are involved.
public class NutritionTargetPlanHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 10, 0, 0, TimeSpan.Zero);
    private static DateOnly D(int month, int day) => new(month == 1 ? 2027 : 2026, month, day);

    private readonly InMemoryNutritionTargetPlanRepository _plans = new();
    private readonly InMemoryMealEntryRepository _meals = new();
    private readonly FakeNutritionEstimationService _ai = new();
    private readonly ManualTimeProvider _clock = new(Now);

    private SaveNutritionTargetPlanHandler Save => new(_plans, _clock);

    private static readonly NutritionTargetInput Base = new(2200, 160, 240, 70);

    private static List<NutritionTargetDayRuleInput> Week(NutritionTargetDayMode mode = NutritionTargetDayMode.Default) =>
        NutritionTargetPlan.Week.Select(day => new NutritionTargetDayRuleInput(day, mode, null)).ToList();

    // Mon/Wed/Fri 2500 kcal, Sun 2800 kcal (or no target), the rest default.
    private static List<NutritionTargetDayRuleInput> TrainingWeek(bool sundayNoTarget = false) =>
        NutritionTargetPlan.Week.Select(day => day switch
        {
            DayOfWeek.Monday or DayOfWeek.Wednesday or DayOfWeek.Friday =>
                new NutritionTargetDayRuleInput(day, NutritionTargetDayMode.Custom, new(2500, null, null, null)),
            DayOfWeek.Sunday => sundayNoTarget
                ? new NutritionTargetDayRuleInput(day, NutritionTargetDayMode.NoTarget, null)
                : new NutritionTargetDayRuleInput(day, NutritionTargetDayMode.Custom, new(2800, null, null, null)),
            _ => new NutritionTargetDayRuleInput(day, NutritionTargetDayMode.Default, null)
        }).ToList();

    private async Task<NutritionTargetPlan> CreateAsync(DateOnly startsOn, DateOnly endsOn, List<NutritionTargetDayRuleInput>? rules = null,
        NutritionTargetInput? defaultTarget = null, Guid? user = null)
    {
        var result = await Save.CreateAsync(user ?? TestUsers.A, new(startsOn, endsOn, defaultTarget ?? Base, rules ?? Week()), CancellationToken.None);
        Assert.Equal(NutritionTargetStatus.Ok, result.Status);
        return result.Plan!;
    }

    private async Task<ResolvedNutritionTarget> ResolveAsync(DateOnly date, Guid? user = null)
    {
        var result = await new GetResolvedNutritionTargetHandler(_plans).HandleAsync(user ?? TestUsers.A, date, CancellationToken.None);
        Assert.Equal(NutritionTargetStatus.Ok, result.Status);
        return result.Resolved!;
    }

    private Task<ResolvedNutritionTargetResult> OverrideAsync(DateOnly date, NutritionTargetOverrideMode mode, NutritionTargetInput? target = null,
        Guid? user = null) =>
        new SetNutritionTargetOverrideHandler(_plans, _clock).HandleAsync(user ?? TestUsers.A, date, mode, target, CancellationToken.None);

    // ---- Plans ----

    [Fact]
    public async Task MultipleFuturePlans_AndGaps_AreValid()
    {
        await CreateAsync(D(10, 7), D(11, 3));
        await CreateAsync(D(11, 4), D(12, 1));
        await CreateAsync(D(12, 10), D(1, 5));

        var plans = await new GetNutritionTargetPlansHandler(_plans).HandleAsync(TestUsers.A, CancellationToken.None);

        Assert.Equal([D(10, 7), D(11, 4), D(12, 10)], plans.Select(plan => plan.StartsOn));
        Assert.False((await ResolveAsync(D(12, 5))).CoveredByPlan);
        Assert.Null((await ResolveAsync(D(12, 5))).Target);
    }

    [Fact]
    public async Task AnOverlappingPlan_IsRejected_WithAReadableMessage_AndNothingChanges()
    {
        await CreateAsync(D(10, 7), D(11, 3));

        var result = await Save.CreateAsync(TestUsers.A, new(D(10, 28), D(11, 30), Base, Week()), CancellationToken.None);

        Assert.Equal(NutritionTargetStatus.Overlap, result.Status);
        Assert.Equal("This period overlaps 7 Oct – 3 Nov 2026.", result.Message);
        Assert.Single(_plans.Plans);
        Assert.Equal(D(11, 3), _plans.Plans.Single().EndsOn);
    }

    [Fact]
    public async Task EditingIntoAnotherPlan_IsRejected_ExcludingItself()
    {
        var a = await CreateAsync(D(10, 1), D(10, 31));
        await CreateAsync(D(11, 1), D(11, 30));

        var intoB = await Save.UpdateAsync(TestUsers.A, a.Id, new(D(10, 1), D(11, 10), Base, Week()), CancellationToken.None);
        var withinItself = await Save.UpdateAsync(TestUsers.A, a.Id, new(D(10, 5), D(10, 31), Base, TrainingWeek()), CancellationToken.None);

        Assert.Equal(NutritionTargetStatus.Overlap, intoB.Status);
        Assert.Equal("This period overlaps 1 Nov – 30 Nov 2026.", intoB.Message);
        Assert.Equal(NutritionTargetStatus.Ok, withinItself.Status);
        Assert.Equal(D(10, 5), _plans.Plans.Single(plan => plan.Id == a.Id).StartsOn);
    }

    [Fact]
    public async Task AConcurrentOverlapSavedAfterTheCheck_IsStillRejected()
    {
        var concurrent = NutritionTargetPlan.Create(TestUsers.A, D(10, 20), D(10, 25), NutritionTargetValues.Create(2000, null, null, null),
            NutritionTargetPlan.Week.Select(day => new NutritionTargetDayRuleSpec(day, NutritionTargetDayMode.Default)).ToList(), Now);
        _plans.SaveConcurrentlyBeforeNextWrite = concurrent;

        var result = await Save.CreateAsync(TestUsers.A, new(D(10, 7), D(11, 3), Base, Week()), CancellationToken.None);

        Assert.Equal(NutritionTargetStatus.Overlap, result.Status);
        Assert.Equal("This period overlaps 20 Oct – 25 Oct 2026.", result.Message);
        Assert.Equal([concurrent.Id], _plans.Plans.Select(plan => plan.Id));
    }

    [Fact]
    public async Task OtherUsersPlans_NeverConflict_AndAreInvisible()
    {
        var other = await CreateAsync(D(10, 7), D(11, 3), user: TestUsers.B);

        await CreateAsync(D(10, 7), D(11, 3));

        Assert.Equal(NutritionTargetStatus.NotFound,
            (await new GetNutritionTargetPlanHandler(_plans).HandleAsync(TestUsers.A, other.Id, CancellationToken.None)).Status);
        Assert.Equal(NutritionTargetStatus.NotFound,
            (await Save.UpdateAsync(TestUsers.A, other.Id, new(D(10, 7), D(11, 3), Base, Week()), CancellationToken.None)).Status);
        Assert.False(await new DeleteNutritionTargetPlanHandler(_plans).HandleAsync(TestUsers.A, other.Id, CancellationToken.None));
        Assert.Equal(2, _plans.Plans.Count);
    }

    [Theory]
    [InlineData(true, "endsOn")]
    [InlineData(false, "weeklyRules.Monday")]
    public async Task InvalidPlans_NameTheField_AndStoreNothing(bool endBeforeStart, string field)
    {
        var command = endBeforeStart
            ? new NutritionTargetPlanCommand(D(11, 3), D(10, 7), Base, Week())
            : new NutritionTargetPlanCommand(D(10, 7), D(11, 3), null, Week());

        var result = await Save.CreateAsync(TestUsers.A, command, CancellationToken.None);

        Assert.Equal(NutritionTargetStatus.Invalid, result.Status);
        Assert.Equal(field, result.Field);
        Assert.DoesNotContain("Parameter", result.Message);
        Assert.Empty(_plans.Plans);
    }

    [Fact]
    public async Task InvalidValues_NameTheirSection()
    {
        var zeroDefault = await Save.CreateAsync(TestUsers.A, new(D(10, 7), D(11, 3), new(0, null, null, null), Week()), CancellationToken.None);
        var rules = TrainingWeek();
        rules[0] = rules[0] with { Target = new(null, null, null, 1000.1m) };
        var tooHighMonday = await Save.CreateAsync(TestUsers.A, new(D(10, 7), D(11, 3), Base, rules), CancellationToken.None);
        rules[0] = rules[0] with { Target = null };
        var emptyCustom = await Save.CreateAsync(TestUsers.A, new(D(10, 7), D(11, 3), Base, rules), CancellationToken.None);

        Assert.Equal("defaultTarget.caloriesKcal", zeroDefault.Field);
        Assert.Equal("weeklyRules.Monday.fatGrams", tooHighMonday.Field);
        Assert.StartsWith("Monday:", tooHighMonday.Message);
        Assert.Equal("weeklyRules.Monday", emptyCustom.Field);
        Assert.Empty(_plans.Plans);
    }

    [Fact]
    public async Task DeletingAPlan_RemovesItAndItsOverrides_AndLeavesOtherPlans()
    {
        var first = await CreateAsync(D(10, 7), D(11, 3));
        var second = await CreateAsync(D(11, 4), D(12, 1));
        await OverrideAsync(D(10, 23), NutritionTargetOverrideMode.NoTarget);
        await OverrideAsync(D(11, 10), NutritionTargetOverrideMode.NoTarget);

        Assert.True(await new DeleteNutritionTargetPlanHandler(_plans).HandleAsync(TestUsers.A, first.Id, CancellationToken.None));

        Assert.Equal([second.Id], _plans.Plans.Select(plan => plan.Id));
        Assert.Equal([D(11, 10)], _plans.Overrides.Select(item => item.Date));
        Assert.Null((await ResolveAsync(D(10, 23))).Target);
        Assert.False((await ResolveAsync(D(10, 23))).CoveredByPlan);
    }

    [Fact]
    public async Task ShorteningAPlan_DropsItsOverridesOutsideTheNewPeriod()
    {
        var plan = await CreateAsync(D(10, 7), D(11, 3));
        await OverrideAsync(D(10, 10), NutritionTargetOverrideMode.NoTarget);
        await OverrideAsync(D(11, 1), NutritionTargetOverrideMode.NoTarget);

        await Save.UpdateAsync(TestUsers.A, plan.Id, new(D(10, 7), D(10, 20), Base, Week()), CancellationToken.None);

        Assert.Equal([D(10, 10)], _plans.Overrides.Select(item => item.Date));
    }

    // ---- Resolution ----

    [Fact]
    public async Task Resolution_FollowsTheWeeklyPattern()
    {
        await CreateAsync(D(10, 7), D(11, 3), TrainingWeek());

        Assert.Equal(2500m, (await ResolveAsync(D(10, 12))).Target!.CaloriesKcal); // Monday custom
        Assert.Equal(2200m, (await ResolveAsync(D(10, 13))).Target!.CaloriesKcal); // Tuesday default
        Assert.Equal(160m, (await ResolveAsync(D(10, 13))).Target!.ProteinGrams);
        Assert.Equal(2500m, (await ResolveAsync(D(10, 7))).Target!.CaloriesKcal);  // Wednesday, first day
        Assert.Equal(2800m, (await ResolveAsync(D(10, 11))).Target!.CaloriesKcal); // Sunday custom
        Assert.Equal(2200m, (await ResolveAsync(D(11, 3))).Target!.CaloriesKcal);  // Tuesday, last day
        Assert.False((await ResolveAsync(D(11, 4))).CoveredByPlan);
        Assert.False((await ResolveAsync(D(10, 6))).CoveredByPlan);
    }

    [Fact]
    public async Task ANoTargetWeekday_IsCoveredButHasNoTarget()
    {
        await CreateAsync(D(10, 7), D(11, 3), TrainingWeek(sundayNoTarget: true));

        var sunday = await ResolveAsync(D(10, 11));

        Assert.True(sunday.CoveredByPlan);
        Assert.Null(sunday.Target);
    }

    [Fact]
    public async Task AdjacentPlans_EachResolveTheirOwnDays()
    {
        await CreateAsync(D(10, 7), D(11, 3));
        await CreateAsync(D(11, 4), D(12, 1), defaultTarget: new(2300, null, null, null));

        Assert.Equal(2200m, (await ResolveAsync(D(11, 3))).Target!.CaloriesKcal);
        Assert.Equal(2300m, (await ResolveAsync(D(11, 4))).Target!.CaloriesKcal);
    }

    [Fact]
    public async Task PastPlans_StillResolve_AndExplicitEditsChangeThem()
    {
        _clock.UtcNow = new DateTimeOffset(2026, 12, 15, 10, 0, 0, TimeSpan.Zero);
        var past = await CreateAsync(D(10, 7), D(11, 3));

        Assert.Equal(2200m, (await ResolveAsync(D(10, 20))).Target!.CaloriesKcal);

        await Save.UpdateAsync(TestUsers.A, past.Id, new(D(10, 7), D(11, 3), new(2100, null, null, null), Week()), CancellationToken.None);

        Assert.Equal(2100m, (await ResolveAsync(D(10, 20))).Target!.CaloriesKcal);
    }

    // ---- Overrides ----

    [Fact]
    public async Task ACustomOverride_ChangesOnlyItsDate_AndRemovingFallsBackToTheWeekdayRule()
    {
        await CreateAsync(D(10, 7), D(11, 3), TrainingWeek());

        var set = await OverrideAsync(D(10, 21), NutritionTargetOverrideMode.Custom, new(3000, null, null, null));

        Assert.Equal(NutritionTargetStatus.Ok, set.Status);
        Assert.Equal(3000m, set.Resolved!.Target!.CaloriesKcal);
        Assert.Equal(NutritionTargetOverrideMode.Custom, set.Resolved.Override!.Mode);
        Assert.Equal(2500m, (await ResolveAsync(D(10, 28))).Target!.CaloriesKcal);

        var removed = await new RemoveNutritionTargetOverrideHandler(_plans).HandleAsync(TestUsers.A, D(10, 21), CancellationToken.None);

        Assert.Equal(2500m, removed.Resolved!.Target!.CaloriesKcal);
        Assert.Null(removed.Resolved.Override);
        Assert.Empty(_plans.Overrides);
    }

    [Fact]
    public async Task ANoTargetOverride_RemovesThatDaysTarget()
    {
        await CreateAsync(D(10, 7), D(11, 3));

        var result = await OverrideAsync(D(10, 23), NutritionTargetOverrideMode.NoTarget);

        Assert.Null(result.Resolved!.Target);
        Assert.True(result.Resolved.CoveredByPlan);
        Assert.Equal(NutritionTargetOverrideMode.NoTarget, result.Resolved.Override!.Mode);
    }

    [Fact]
    public async Task SettingAnOverrideAgain_ReplacesIt_OnePerDate()
    {
        await CreateAsync(D(10, 7), D(11, 3));

        await OverrideAsync(D(10, 23), NutritionTargetOverrideMode.NoTarget);
        await OverrideAsync(D(10, 23), NutritionTargetOverrideMode.Custom, new(1900, null, null, null));

        Assert.Single(_plans.Overrides);
        Assert.Equal(1900m, (await ResolveAsync(D(10, 23))).Target!.CaloriesKcal);
    }

    [Fact]
    public async Task AnOverrideOutsideEveryPlan_IsRejected()
    {
        await CreateAsync(D(10, 7), D(11, 3));

        var result = await OverrideAsync(D(11, 4), NutritionTargetOverrideMode.Custom, new(3000, null, null, null));

        Assert.Equal(NutritionTargetStatus.Invalid, result.Status);
        Assert.Equal("date", result.Field);
        Assert.Equal(SetNutritionTargetOverrideHandler.OutsidePlanMessage, result.Message);
        Assert.Empty(_plans.Overrides);
    }

    [Theory]
    [InlineData(NutritionTargetOverrideMode.Custom, false, "target")]
    [InlineData(NutritionTargetOverrideMode.NoTarget, true, "target")]
    public async Task OverrideValues_MustMatchTheMode(NutritionTargetOverrideMode mode, bool withValues, string field)
    {
        await CreateAsync(D(10, 7), D(11, 3));

        var result = await OverrideAsync(D(10, 23), mode, withValues ? new(3000, null, null, null) : null);

        Assert.Equal(field, result.Field);
        Assert.Empty(_plans.Overrides);
    }

    [Fact]
    public async Task OverridesAreUserScoped()
    {
        await CreateAsync(D(10, 7), D(11, 3), user: TestUsers.B);

        Assert.Equal(NutritionTargetStatus.Invalid, (await OverrideAsync(D(10, 23), NutritionTargetOverrideMode.NoTarget)).Status);
        Assert.False((await ResolveAsync(D(10, 23))).CoveredByPlan);
    }

    // ---- Daily summary ----

    [Fact]
    public async Task TheDailySummary_CarriesTheResolvedTarget_WithUnchangedTotals()
    {
        await CreateAsync(D(10, 7), D(11, 3), TrainingWeek(sundayNoTarget: true));
        await OverrideAsync(D(10, 23), NutritionTargetOverrideMode.Custom, new(3000, null, null, null));
        await new CreateMealHandler(_meals, _clock).HandleAsync(TestUsers.A,
            new CreateMealCommand("Pasta", null, D(10, 12), new TimeOnly(13, 0), 120), CancellationToken.None);

        var monday = await SummaryAsync(D(10, 12));

        Assert.Equal((1, 0, 0m), (monday.MealCount, monday.AnalyzedMealCount, monday.CaloriesKcal));
        Assert.Equal(2500m, monday.Target!.CaloriesKcal);
        Assert.Equal(3000m, (await SummaryAsync(D(10, 23))).Target!.CaloriesKcal);
        Assert.Null((await SummaryAsync(D(10, 11))).Target);
        Assert.Null((await SummaryAsync(D(11, 4))).Target);
    }

    [Fact]
    public async Task AnalyzeDay_ReturnsTheResolvedTarget_AndAiSeesOnlyTheMeal()
    {
        await CreateAsync(D(10, 7), D(11, 3), TrainingWeek());
        await new CreateMealHandler(_meals, _clock).HandleAsync(TestUsers.A,
            new CreateMealCommand("Pasta", null, D(10, 12), new TimeOnly(13, 0), 120), CancellationToken.None);

        var result = await new AnalyzeDayHandler(_meals, _plans, new MealNutritionEstimation(_ai, _meals, _clock))
            .HandleAsync(TestUsers.A, D(10, 12), CancellationToken.None);

        Assert.Equal(2500m, result.Summary!.Target!.CaloriesKcal);
        Assert.Equal([new MealEstimationInput("Pasta", null)], _ai.Inputs);
    }

    [Fact]
    public async Task PlanningTargets_NeverCallsAi()
    {
        var plan = await CreateAsync(D(10, 7), D(11, 3), TrainingWeek());
        await OverrideAsync(D(10, 23), NutritionTargetOverrideMode.NoTarget);
        await ResolveAsync(D(10, 23));
        await new DeleteNutritionTargetPlanHandler(_plans).HandleAsync(TestUsers.A, plan.Id, CancellationToken.None);

        Assert.Empty(_ai.Inputs);
    }

    private async Task<DailyNutritionSummary> SummaryAsync(DateOnly day) =>
        (await new GetDailyNutritionSummaryHandler(_meals, _plans).HandleAsync(TestUsers.A, day, CancellationToken.None)).Summary!;
}
