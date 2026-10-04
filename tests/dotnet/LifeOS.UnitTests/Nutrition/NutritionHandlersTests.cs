using LifeOS.Application.Nutrition;
using LifeOS.Domain.Nutrition;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Nutrition;

// NUT-002 use cases against the in-memory repository and a fake estimator (no AI, no network).
public class NutritionHandlersTests
{
    // 18:00 UTC = 20:00 in UTC+2: the user's local today is 3 October.
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 3);
    private static readonly DateOnly Yesterday = Today.AddDays(-1);
    private const int Offset = 120;

    private readonly InMemoryMealEntryRepository _repository = new();
    private readonly InMemoryNutritionTargetRepository _targets = new();
    private readonly FakeNutritionEstimationService _ai = new();
    private readonly ManualTimeProvider _clock = new(Now);

    private MealNutritionEstimation Estimation => new(_ai, _repository, _clock);

    private async Task<Guid> MealAsync(string description, TimeOnly time, DateOnly? day = null, MealType? type = null, Guid? user = null)
    {
        var result = await new CreateMealHandler(_repository, _clock)
            .HandleAsync(user ?? TestUsers.A, new CreateMealCommand(description, type, day ?? Today, time, Offset), CancellationToken.None);
        Assert.Equal(MealResultStatus.Ok, result.Status);
        return result.Meal!.Id;
    }

    private Task<MealEstimateResult> EstimateAsync(Guid meal, Guid? user = null) =>
        new EstimateMealNutritionHandler(_repository, Estimation).HandleAsync(user ?? TestUsers.A, meal, CancellationToken.None);

    private Task<MealResult> SetAsync(Guid meal, NutritionSource source, decimal kcal = 620, decimal p = 52, decimal c = 58, decimal f = 20,
        Guid? user = null) =>
        new SetMealNutritionHandler(_repository, _clock)
            .HandleAsync(user ?? TestUsers.A, meal, new SetMealNutritionCommand(kcal, p, c, f, source), CancellationToken.None);

    private Task<AnalyzeDayResult> AnalyzeAsync(DateOnly day, Guid? user = null) =>
        new AnalyzeDayHandler(_repository, _targets, Estimation).HandleAsync(user ?? TestUsers.A, day, CancellationToken.None);

    private Task<LazyCloseResult> LazyCloseAsync(int offset = Offset, Guid? user = null) =>
        new LazyCloseNutritionHandler(_repository, Estimation, _clock).HandleAsync(user ?? TestUsers.A, offset, CancellationToken.None);

    private async Task<DailyNutritionSummary> SummaryAsync(DateOnly day, Guid? user = null) =>
        (await new GetDailyNutritionSummaryHandler(_repository, _targets).HandleAsync(user ?? TestUsers.A, day, CancellationToken.None)).Summary!;

    private Task<MealResult> UpdateAsync(Guid meal, string description, TimeOnly time, MealType? type = null, bool clear = false) =>
        new UpdateMealHandler(_repository, _repository, _clock)
            .HandleAsync(TestUsers.A, meal, new UpdateMealCommand(description, type, time, clear), CancellationToken.None);

    private static NutritionEstimationResult ByDescription(MealEstimationInput input) => input.Description switch
    {
        "Pasta" => FakeNutritionEstimationService.Estimate(700, 25, 110, 15),
        "Insalata" => FakeNutritionEstimationService.Estimate(150.04m, 3, 10, 11),
        "Caffè" => FakeNutritionEstimationService.Estimate(2, 0.1m, 0, 0),
        "???" => FakeNutritionEstimationService.NotEstimable,
        "down" => FakeNutritionEstimationService.Unavailable,
        _ => FakeNutritionEstimationService.Estimate(620, 52, 58, 20)
    };

    // ---- Writing a meal never invokes AI ----

    [Fact]
    public async Task CreatingEditingAndDeletingMeals_NeverCallTheEstimator()
    {
        var id = await MealAsync("Pollo con le patate", new TimeOnly(13, 0));
        await UpdateAsync(id, "Pollo con le patate e insalata", new TimeOnly(13, 30));
        await new GetMealsForDateHandler(_repository).HandleAsync(TestUsers.A, Today, CancellationToken.None);
        await new DeleteMealHandler(_repository).HandleAsync(TestUsers.A, id, CancellationToken.None);

        Assert.Empty(_ai.Inputs);
    }

    // ---- Single meal ----

    [Fact]
    public async Task Estimate_ReturnsAProposal_SendsOnlyTextAndType_AndPersistsNothing()
    {
        var id = await MealAsync("Pollo con le patate", new TimeOnly(13, 0), type: MealType.Lunch);
        _ai.Respond = _ => FakeNutritionEstimationService.Estimate(620.04m, 52.25m, 58, 20, "about 180 g chicken", " ", "about 250 g potatoes ");

        var result = await EstimateAsync(id);

        Assert.Equal(NutritionStatus.Ok, result.Status);
        Assert.Equal(NutritionValues.Create(620, 52.3m, 58, 20), result.Proposal!.Values);
        Assert.Equal(["about 180 g chicken", "about 250 g potatoes"], result.Proposal.Assumptions);
        Assert.Equal([new MealEstimationInput("Pollo con le patate", MealType.Lunch)], _ai.Inputs);
        Assert.Empty(_repository.Snapshots);
    }

    [Fact]
    public async Task Confirm_PersistsAiConfirmed()
    {
        var id = await MealAsync("Pollo con le patate", new TimeOnly(13, 0));

        var result = await SetAsync(id, NutritionSource.AiConfirmed);

        Assert.Equal(MealResultStatus.Ok, result.Status);
        Assert.Equal((NutritionSource.AiConfirmed, 620m), (result.Meal!.Nutrition!.Source, result.Meal.Nutrition.Values.CaloriesKcal));
        Assert.Equal(NutritionSource.AiConfirmed, _repository.SnapshotOf(id)!.Source);
        Assert.Empty(_ai.Inputs);
    }

    [Fact]
    public async Task EditedProposal_PersistsUserAdjusted()
    {
        var id = await MealAsync("Pollo con le patate", new TimeOnly(13, 0));
        await EstimateAsync(id);

        var result = await SetAsync(id, NutritionSource.UserAdjusted, 700, 60, 55, 22);

        Assert.Equal(MealResultStatus.Ok, result.Status);
        var stored = _repository.SnapshotOf(id)!;
        Assert.Equal((NutritionSource.UserAdjusted, 700m, 60m, 55m, 22m),
            (stored.Source, stored.CaloriesKcal, stored.ProteinGrams, stored.CarbsGrams, stored.FatGrams));
    }

    [Fact]
    public async Task EditingAnExistingSnapshot_IsUserAdjusted_KeepsItsIdentity_AndNeverCallsAi()
    {
        var id = await MealAsync("Pollo con le patate", new TimeOnly(13, 0));
        await SetAsync(id, NutritionSource.AiConfirmed);
        var original = _repository.SnapshotOf(id)!;
        _clock.Advance(TimeSpan.FromHours(1));

        await SetAsync(id, NutritionSource.UserAdjusted, 500, 40, 40, 15);

        var stored = Assert.Single(_repository.Snapshots);
        Assert.Equal((original.Id, original.CreatedAtUtc, Now.AddHours(1)), (stored.Id, stored.CreatedAtUtc, stored.UpdatedAtUtc));
        Assert.Equal((NutritionSource.UserAdjusted, 500m), (stored.Source, stored.CaloriesKcal));
        Assert.Empty(_ai.Inputs);
    }

    [Fact]
    public async Task ReEstimate_DoesNotOverwrite_UntilConfirmed()
    {
        var id = await MealAsync("Pollo con le patate", new TimeOnly(13, 0));
        await SetAsync(id, NutritionSource.UserAdjusted, 500, 40, 40, 15);
        _ai.Respond = _ => FakeNutritionEstimationService.Estimate(800, 70, 60, 30);

        var proposal = await EstimateAsync(id);

        Assert.Equal(800m, proposal.Proposal!.Values.CaloriesKcal);
        Assert.Equal((500m, NutritionSource.UserAdjusted), (_repository.SnapshotOf(id)!.CaloriesKcal, _repository.SnapshotOf(id)!.Source));

        await SetAsync(id, NutritionSource.AiConfirmed, 800, 70, 60, 30);

        Assert.Equal((800m, NutritionSource.AiConfirmed), (_repository.SnapshotOf(id)!.CaloriesKcal, _repository.SnapshotOf(id)!.Source));
    }

    [Theory]
    [InlineData(NutritionEstimationFailure.Unavailable, NutritionStatus.EstimationUnavailable)]
    [InlineData(NutritionEstimationFailure.NotEstimable, NutritionStatus.NotEstimable)]
    public async Task ProviderFailure_ChangesNothing(NutritionEstimationFailure failure, NutritionStatus expected)
    {
        var id = await MealAsync("Pollo con le patate", new TimeOnly(13, 0));
        await SetAsync(id, NutritionSource.UserAdjusted, 500, 40, 40, 15);
        _ai.Respond = _ => NutritionEstimationResult.Failed(failure);

        var result = await EstimateAsync(id);

        Assert.Equal((expected, (MealNutritionProposal?)null), (result.Status, result.Proposal));
        Assert.Equal((500m, NutritionSource.UserAdjusted), (_repository.SnapshotOf(id)!.CaloriesKcal, _repository.SnapshotOf(id)!.Source));
        Assert.Equal("Pollo con le patate", _repository.Entries.Single().Description);
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(20000, 0, 0, 0)]
    [InlineData(0, 0, 0, 5000)]
    public async Task OutOfRangeEstimates_AreNotEstimable_NeverClamped(decimal kcal, decimal p, decimal c, decimal f)
    {
        var id = await MealAsync("Pollo con le patate", new TimeOnly(13, 0));
        _ai.Respond = _ => FakeNutritionEstimationService.Estimate(kcal, p, c, f);

        Assert.Equal(NutritionStatus.NotEstimable, (await EstimateAsync(id)).Status);
        Assert.Empty(_repository.Snapshots);
    }

    [Theory]
    [InlineData(NutritionSource.AiRequested)]
    [InlineData(NutritionSource.AiAutoClosed)]
    public async Task Set_AcceptsOnlyExplicitSources(NutritionSource source)
    {
        var id = await MealAsync("Pollo con le patate", new TimeOnly(13, 0));

        var result = await SetAsync(id, source);

        Assert.Equal((MealResultStatus.Invalid, "source"), (result.Status, result.Field));
        Assert.Empty(_repository.Snapshots);
    }

    [Theory]
    [InlineData(-1, 0, 0, 0, "caloriesKcal")]
    [InlineData(0, 0, 1001, 0, "carbsGrams")]
    public async Task Set_InvalidValues_NameTheField_AndChangeNothing(decimal kcal, decimal p, decimal c, decimal f, string field)
    {
        var id = await MealAsync("Pollo con le patate", new TimeOnly(13, 0));

        var result = await SetAsync(id, NutritionSource.UserAdjusted, kcal, p, c, f);

        Assert.Equal((MealResultStatus.Invalid, field), (result.Status, result.Field));
        Assert.Empty(_repository.Snapshots);
    }

    // ---- Day ----

    [Fact]
    public async Task AnalyzeDay_EstimatesOnlyMissingMeals_AsAiRequested_AndSkipsExistingSnapshots()
    {
        _ai.Respond = ByDescription;
        var pasta = await MealAsync("Pasta", new TimeOnly(13, 0));
        var salad = await MealAsync("Insalata", new TimeOnly(20, 0));
        var adjusted = await MealAsync("Pollo", new TimeOnly(8, 0));
        await MealAsync("Altro giorno", new TimeOnly(9, 0), Yesterday);
        await SetAsync(adjusted, NutritionSource.UserAdjusted, 300, 30, 0, 18);

        var result = await AnalyzeAsync(Today);

        Assert.Equal(NutritionStatus.Ok, result.Status);
        Assert.Equal(new NutritionAnalysisResult(2, 0, false, false), result.Analysis);
        // Earliest first, only the two missing meals of that day.
        Assert.Equal(["Pasta", "Insalata"], _ai.Inputs.Select(input => input.Description));
        Assert.Equal((NutritionSource.AiRequested, 700m), (_repository.SnapshotOf(pasta)!.Source, _repository.SnapshotOf(pasta)!.CaloriesKcal));
        Assert.Equal((NutritionSource.AiRequested, 150.0m), (_repository.SnapshotOf(salad)!.Source, _repository.SnapshotOf(salad)!.CaloriesKcal));
        Assert.Equal((NutritionSource.UserAdjusted, 300m), (_repository.SnapshotOf(adjusted)!.Source, _repository.SnapshotOf(adjusted)!.CaloriesKcal));
        Assert.Equal(new DailyNutritionSummary(Today, 3, 3, 1150.0m, 58m, 120m, 44m), result.Summary);
        Assert.True(result.Summary!.AllAnalyzed);
    }

    [Fact]
    public async Task AnalyzeDay_PartialFailure_KeepsSuccesses_AndRetryProcessesOnlyTheMissingMeal()
    {
        _ai.Respond = ByDescription;
        await MealAsync("Pasta", new TimeOnly(8, 0));
        var unclear = await MealAsync("???", new TimeOnly(10, 0));
        await MealAsync("Insalata", new TimeOnly(13, 0));
        await MealAsync("Caffè", new TimeOnly(16, 0));

        var first = await AnalyzeAsync(Today);

        Assert.Equal(new NutritionAnalysisResult(3, 1, false, true), first.Analysis);
        Assert.Equal((4, 3, false), (first.Summary!.MealCount, first.Summary.AnalyzedMealCount, first.Summary.AllAnalyzed));
        Assert.Null(_repository.SnapshotOf(unclear));
        Assert.Equal(3, _repository.Snapshots.Count);

        _ai.Respond = _ => FakeNutritionEstimationService.Estimate(100, 1, 20, 1);
        var retry = await AnalyzeAsync(Today);

        Assert.Equal(new NutritionAnalysisResult(1, 0, false, false), retry.Analysis);
        Assert.Equal(["Pasta", "???", "Insalata", "Caffè", "???"], _ai.Inputs.Select(input => input.Description));
        Assert.True(retry.Summary!.AllAnalyzed);
    }

    [Fact]
    public async Task AnalyzeDay_StopsWhenEstimationIsUnavailable_LeavingTheRestPending()
    {
        _ai.Respond = ByDescription;
        await MealAsync("Pasta", new TimeOnly(8, 0));
        await MealAsync("down", new TimeOnly(10, 0));
        await MealAsync("Insalata", new TimeOnly(13, 0));

        var result = await AnalyzeAsync(Today);

        Assert.Equal(new NutritionAnalysisResult(1, 0, true, true), result.Analysis);
        Assert.Equal(["Pasta", "down"], _ai.Inputs.Select(input => input.Description));
        Assert.Equal((3, 1), (result.Summary!.MealCount, result.Summary.AnalyzedMealCount));
    }

    [Fact]
    public async Task AnalyzeDay_IsBoundedPerRequest()
    {
        for (var minute = 0; minute < NutritionDays.MaxMealsPerRun + 2; minute++)
        {
            await MealAsync($"Snack {minute}", new TimeOnly(10, minute));
        }

        var first = await AnalyzeAsync(Today);
        var second = await AnalyzeAsync(Today);

        Assert.Equal(new NutritionAnalysisResult(20, 0, false, true), first.Analysis);
        Assert.Equal(new NutritionAnalysisResult(2, 0, false, false), second.Analysis);
        Assert.Equal(22, _ai.Inputs.Count);
    }

    [Fact]
    public async Task AnalyzeDay_WithNothingMissing_CallsNoAi()
    {
        var id = await MealAsync("Pasta", new TimeOnly(8, 0));
        await SetAsync(id, NutritionSource.AiConfirmed);

        var result = await AnalyzeAsync(Today);
        var empty = await AnalyzeAsync(Yesterday);

        Assert.Equal(new NutritionAnalysisResult(0, 0, false, false), result.Analysis);
        Assert.Equal(new DailyNutritionSummary(Yesterday, 0, 0, 0, 0, 0, 0), empty.Summary);
        Assert.False(empty.Summary!.AllAnalyzed);
        Assert.Empty(_ai.Inputs);
    }

    [Fact]
    public async Task Summary_IsTheDeterministicSumOfCurrentSnapshots_WithPartialCounts()
    {
        var a = await MealAsync("A", new TimeOnly(8, 0));
        var b = await MealAsync("B", new TimeOnly(13, 0));
        await MealAsync("C", new TimeOnly(20, 0));
        var other = await MealAsync("D", new TimeOnly(9, 0), Yesterday);
        await SetAsync(a, NutritionSource.AiConfirmed, 740.1m, 52.2m, 80.3m, 20.4m);
        await SetAsync(b, NutritionSource.UserAdjusted, 739.9m, 51.8m, 70.7m, 28.6m);
        await SetAsync(other, NutritionSource.UserAdjusted, 1000, 100, 100, 100);

        var summary = await SummaryAsync(Today);

        Assert.Equal(new DailyNutritionSummary(Today, 3, 2, 1480.0m, 104.0m, 151.0m, 49.0m), summary);
        Assert.False(summary.AllAnalyzed);
    }

    [Fact]
    public async Task Summary_InvalidDate_IsInvalid()
    {
        var result = await new GetDailyNutritionSummaryHandler(_repository, _targets).HandleAsync(TestUsers.A, DateOnly.MaxValue, CancellationToken.None);

        Assert.Equal(NutritionStatus.Invalid, result.Status);
        Assert.Equal(NutritionStatus.Invalid, (await AnalyzeAsync(DateOnly.MinValue)).Status);
    }

    // ---- Lazy close ----

    [Fact]
    public async Task LazyClose_NeverAnalyzesToday_AndClosesPastMealsAsAiAutoClosed()
    {
        var today = await MealAsync("Oggi", new TimeOnly(8, 0));
        var past = await MealAsync("Ieri", new TimeOnly(20, 0), Yesterday);

        var result = await LazyCloseAsync();

        Assert.Equal(new NutritionAnalysisResult(1, 0, false, false), result.Analysis);
        Assert.Null(_repository.SnapshotOf(today));
        Assert.Equal(NutritionSource.AiAutoClosed, _repository.SnapshotOf(past)!.Source);
        Assert.Equal(["Ieri"], _ai.Inputs.Select(input => input.Description));
    }

    [Fact]
    public async Task LazyClose_TodayIsTheUsersLocalDay()
    {
        // 18:00 UTC is already 4 October in UTC+8: 3 October is then a past day.
        var meal = await MealAsync("Cena", new TimeOnly(20, 0));

        await LazyCloseAsync(offset: 0);
        Assert.Null(_repository.SnapshotOf(meal));

        await LazyCloseAsync(offset: 480);
        Assert.Equal(NutritionSource.AiAutoClosed, _repository.SnapshotOf(meal)!.Source);
    }

    [Fact]
    public async Task LazyClose_LeavesExistingAndUserAdjustedSnapshotsUntouched()
    {
        var confirmed = await MealAsync("Confermato", new TimeOnly(8, 0), Yesterday);
        var adjusted = await MealAsync("Modificato", new TimeOnly(13, 0), Yesterday);
        var missing = await MealAsync("Mancante", new TimeOnly(20, 0), Yesterday);
        await SetAsync(confirmed, NutritionSource.AiConfirmed, 111, 1, 1, 1);
        await SetAsync(adjusted, NutritionSource.UserAdjusted, 222, 2, 2, 2);

        var result = await LazyCloseAsync();

        Assert.Equal(new NutritionAnalysisResult(1, 0, false, false), result.Analysis);
        Assert.Equal((111m, NutritionSource.AiConfirmed), (_repository.SnapshotOf(confirmed)!.CaloriesKcal, _repository.SnapshotOf(confirmed)!.Source));
        Assert.Equal((222m, NutritionSource.UserAdjusted), (_repository.SnapshotOf(adjusted)!.CaloriesKcal, _repository.SnapshotOf(adjusted)!.Source));
        Assert.Equal(NutritionSource.AiAutoClosed, _repository.SnapshotOf(missing)!.Source);
        Assert.Equal(["Mancante"], _ai.Inputs.Select(input => input.Description));
    }

    [Fact]
    public async Task LazyClose_IsBounded_Deterministic_AndLeavesTheBacklogForTheNextTrigger()
    {
        // 25 past meals over 5 days; the most recent day is closed first.
        for (var day = 1; day <= 5; day++)
        {
            for (var meal = 0; meal < 5; meal++)
            {
                await MealAsync($"d{day}m{meal}", new TimeOnly(8 + meal, 0), Today.AddDays(-day));
            }
        }

        var first = await LazyCloseAsync();

        Assert.Equal(new NutritionAnalysisResult(NutritionDays.MaxMealsPerRun, 0, false, true), first.Analysis);
        Assert.Equal(
            Enumerable.Range(1, 4).SelectMany(day => Enumerable.Range(0, 5).Select(meal => $"d{day}m{meal}")),
            _ai.Inputs.Select(input => input.Description));
        Assert.Equal(0, (await SummaryAsync(Today.AddDays(-5))).AnalyzedMealCount);

        var second = await LazyCloseAsync();

        Assert.Equal(new NutritionAnalysisResult(5, 0, false, false), second.Analysis);
        Assert.True((await SummaryAsync(Today.AddDays(-5))).AllAnalyzed);
        Assert.Equal(25, _ai.Inputs.Count);
    }

    [Fact]
    public async Task LazyClose_RepeatedCalls_AreIdempotent()
    {
        await MealAsync("Ieri", new TimeOnly(20, 0), Yesterday);

        await LazyCloseAsync();
        var again = await LazyCloseAsync();

        Assert.Equal(new NutritionAnalysisResult(0, 0, false, false), again.Analysis);
        Assert.Single(_repository.Snapshots);
        Assert.Single(_ai.Inputs);
    }

    [Fact]
    public async Task LazyClose_ProviderFailure_IsNonDestructive_AndRetriedLater()
    {
        var past = await MealAsync("Ieri", new TimeOnly(20, 0), Yesterday);
        var adjusted = await MealAsync("Modificato", new TimeOnly(13, 0), Yesterday);
        await SetAsync(adjusted, NutritionSource.UserAdjusted, 222, 2, 2, 2);
        _ai.Respond = _ => FakeNutritionEstimationService.Unavailable;

        var failed = await LazyCloseAsync();

        Assert.Equal(new NutritionAnalysisResult(0, 0, true, true), failed.Analysis);
        Assert.Null(_repository.SnapshotOf(past));
        Assert.Equal(222m, _repository.SnapshotOf(adjusted)!.CaloriesKcal);

        _ai.Respond = _ => FakeNutritionEstimationService.Estimate(400, 20, 40, 10);
        var later = await LazyCloseAsync();

        Assert.Equal(new NutritionAnalysisResult(1, 0, false, false), later.Analysis);
        Assert.Equal(NutritionSource.AiAutoClosed, _repository.SnapshotOf(past)!.Source);
    }

    [Fact]
    public async Task LazyClose_ARetroactiveMeal_ReopensOnlyThatMeal()
    {
        await MealAsync("Pranzo", new TimeOnly(13, 0), Yesterday);
        await LazyCloseAsync();
        Assert.True((await SummaryAsync(Yesterday)).AllAnalyzed);

        await MealAsync("Spuntino", new TimeOnly(17, 0), Yesterday);
        Assert.Equal((2, 1, false), ((await SummaryAsync(Yesterday)).MealCount, (await SummaryAsync(Yesterday)).AnalyzedMealCount,
            (await SummaryAsync(Yesterday)).AllAnalyzed));

        await LazyCloseAsync();

        Assert.True((await SummaryAsync(Yesterday)).AllAnalyzed);
        Assert.Equal(["Pranzo", "Spuntino"], _ai.Inputs.Select(input => input.Description));
    }

    [Fact]
    public async Task LazyClose_InvalidOffset_IsInvalid_AndCallsNoAi()
    {
        await MealAsync("Ieri", new TimeOnly(20, 0), Yesterday);

        var result = await LazyCloseAsync(offset: 841);

        Assert.Equal((NutritionStatus.Invalid, "utcOffsetMinutes"), (result.Status, result.Field));
        Assert.Empty(_ai.Inputs);
    }

    [Fact]
    public async Task ADescriptionChangedDuringAnEstimate_IsNotStoredForTheNewText()
    {
        var id = await MealAsync("Pasta", new TimeOnly(13, 0), Yesterday);
        _ai.BeforeAnswer = async _ =>
        {
            _ai.BeforeAnswer = null;
            Assert.Equal(MealResultStatus.Ok, (await UpdateAsync(id, "Riso", new TimeOnly(13, 0))).Status);
        };

        var result = await LazyCloseAsync();

        Assert.Equal(0, result.Analysis!.Analyzed);
        Assert.Null(_repository.SnapshotOf(id));
    }

    // ---- Meal changes ----

    [Fact]
    public async Task DescriptionChange_WithNutrition_RequiresConfirmation_ThenClearsIt()
    {
        var id = await MealAsync("Pasta", new TimeOnly(13, 0), type: MealType.Lunch);
        await SetAsync(id, NutritionSource.AiConfirmed);

        var refused = await UpdateAsync(id, "Pasta al pomodoro", new TimeOnly(13, 0), MealType.Lunch);

        Assert.Equal((MealResultStatus.NutritionClearRequired, MealResult.NutritionClearMessage), (refused.Status, refused.Message));
        Assert.Equal("Pasta", _repository.Entries.Single().Description);
        Assert.NotNull(_repository.SnapshotOf(id));

        var confirmed = await UpdateAsync(id, "Pasta al pomodoro", new TimeOnly(13, 0), MealType.Lunch, clear: true);

        Assert.Equal(MealResultStatus.Ok, confirmed.Status);
        Assert.Equal(("Pasta al pomodoro", (MealNutritionSummary?)null), (confirmed.Meal!.Description, confirmed.Meal.Nutrition));
        Assert.Null(_repository.SnapshotOf(id));
        Assert.Empty(_ai.Inputs);
    }

    [Fact]
    public async Task TimeOrTypeOnlyEdit_KeepsNutrition_EvenWithoutConfirmation()
    {
        var id = await MealAsync("Pasta", new TimeOnly(13, 0), type: MealType.Lunch);
        await SetAsync(id, NutritionSource.UserAdjusted, 500, 40, 40, 15);

        // Outer whitespace is not a description change (it is trimmed).
        var result = await UpdateAsync(id, "  Pasta ", new TimeOnly(19, 30), MealType.Dinner);

        Assert.Equal(MealResultStatus.Ok, result.Status);
        Assert.Equal((500m, NutritionSource.UserAdjusted), (result.Meal!.Nutrition!.Values.CaloriesKcal, result.Meal.Nutrition.Source));
        Assert.Equal(500m, _repository.SnapshotOf(id)!.CaloriesKcal);
    }

    [Fact]
    public async Task DescriptionChange_WithoutNutrition_NeedsNoConfirmation()
    {
        var id = await MealAsync("Pasta", new TimeOnly(13, 0));

        Assert.Equal(MealResultStatus.Ok, (await UpdateAsync(id, "Riso", new TimeOnly(13, 0))).Status);
    }

    [Fact]
    public async Task DeletingAMeal_DeletesItsNutrition()
    {
        var id = await MealAsync("Pasta", new TimeOnly(13, 0));
        await SetAsync(id, NutritionSource.AiConfirmed);

        await new DeleteMealHandler(_repository).HandleAsync(TestUsers.A, id, CancellationToken.None);

        Assert.Empty(_repository.Snapshots);
        Assert.Equal(new DailyNutritionSummary(Today, 0, 0, 0, 0, 0, 0), await SummaryAsync(Today));
    }

    [Fact]
    public async Task MealList_IncludesEachMealsNutrition()
    {
        var analyzed = await MealAsync("Pasta", new TimeOnly(13, 0));
        await MealAsync("Caffè", new TimeOnly(16, 0));
        await SetAsync(analyzed, NutritionSource.AiConfirmed);

        var meals = (await new GetMealsForDateHandler(_repository).HandleAsync(TestUsers.A, Today, CancellationToken.None)).Meals;

        Assert.Equal(["Caffè", "Pasta"], meals.Select(meal => meal.Description));
        Assert.Null(meals[0].Nutrition);
        Assert.Equal(NutritionSource.AiConfirmed, meals[1].Nutrition!.Source);
    }

    // ---- Ownership ----

    [Fact]
    public async Task OtherUsers_CanNeitherEstimateNorSetNorSeeNorAnalyzeTheMeal()
    {
        var id = await MealAsync("Pasta", new TimeOnly(13, 0), Yesterday);
        await MealAsync("Pasta di B", new TimeOnly(13, 0), Yesterday, user: TestUsers.B);

        Assert.Equal(NutritionStatus.NotFound, (await EstimateAsync(id, TestUsers.B)).Status);
        Assert.Equal(MealResultStatus.NotFound, (await SetAsync(id, NutritionSource.UserAdjusted, user: TestUsers.B)).Status);
        Assert.Equal(0, (await SummaryAsync(Today, TestUsers.B)).MealCount);
        await LazyCloseAsync(user: TestUsers.B);

        Assert.Null(_repository.SnapshotOf(id));
        Assert.Equal(["Pasta di B"], _ai.Inputs.Select(input => input.Description));
        Assert.Equal(1, (await SummaryAsync(Yesterday, TestUsers.A)).MealCount);
        Assert.Equal(0, (await SummaryAsync(Yesterday, TestUsers.A)).AnalyzedMealCount);
    }

    [Fact]
    public async Task MissingMeal_IsNotFound_WithoutCallingAi()
    {
        Assert.Equal(NutritionStatus.NotFound, (await EstimateAsync(Guid.CreateVersion7())).Status);
        Assert.Equal(MealResultStatus.NotFound, (await SetAsync(Guid.CreateVersion7(), NutritionSource.AiConfirmed)).Status);
        Assert.Empty(_ai.Inputs);
    }
}
