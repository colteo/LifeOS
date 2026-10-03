using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LifeOS.App.Services.Nutrition;
using LifeOS.Contracts.Nutrition;

namespace LifeOS.UnitTests.App;

// NUT-002 in the app: the plain .NET presentation rules (NutritionDisplay), and the composition of the
// Home card and the Nutrition page, which the net10.0 test project cannot render. Those checks read the
// component sources.
public class NutritionAiAppTests
{
    private static readonly DateOnly Today = new(2026, 10, 3);

    // ---- Daily states ----

    [Fact]
    public void NoMeals()
    {
        var summary = Summary(0, 0);

        Assert.Equal(NutritionDayState.NoMeals, NutritionDisplay.StateOf(summary));
        Assert.Equal("No meals logged", NutritionDisplay.CountLine(summary));
        Assert.False(NutritionDisplay.ShowsTotals(summary));
        Assert.Null(NutritionDisplay.AnalyzeLabel(summary, isToday: true));
    }

    [Fact]
    public void NoneAnalyzed()
    {
        var summary = Summary(3, 0);

        Assert.Equal(NutritionDayState.NotAnalyzed, NutritionDisplay.StateOf(summary));
        Assert.Equal("3 meals · not analyzed", NutritionDisplay.CountLine(summary));
        Assert.False(NutritionDisplay.ShowsTotals(summary));
        Assert.Equal("Analyze today", NutritionDisplay.AnalyzeLabel(summary, isToday: true));
        Assert.Equal("Analyze day", NutritionDisplay.AnalyzeLabel(summary, isToday: false));
        Assert.Equal("1 meal · not analyzed", NutritionDisplay.CountLine(Summary(1, 0)));
    }

    [Fact]
    public void PartiallyAnalyzed_AlwaysCarriesTheCounts()
    {
        var summary = Summary(3, 2, 1480, 104, 151, 49);

        Assert.Equal(NutritionDayState.Partial, NutritionDisplay.StateOf(summary));
        Assert.Equal("3 meals · 2 analyzed", NutritionDisplay.CountLine(summary));
        Assert.True(NutritionDisplay.ShowsTotals(summary));
        Assert.Equal("Analyze remaining", NutritionDisplay.AnalyzeLabel(summary, isToday: true));
        Assert.Equal("Analyze remaining", NutritionDisplay.AnalyzeLabel(summary, isToday: false));
    }

    [Fact]
    public void FullyAnalyzed()
    {
        var summary = Summary(3, 3, 1920, 138, 204, 65) with { AllAnalyzed = true };

        Assert.Equal(NutritionDayState.Complete, NutritionDisplay.StateOf(summary));
        Assert.Equal("3 meals · all analyzed", NutritionDisplay.CountLine(summary));
        Assert.Null(NutritionDisplay.AnalyzeLabel(summary, isToday: true));
    }

    // ---- Values and provenance ----

    [Fact]
    public void Values_AreWholeNumbers_WithoutFakePrecision()
    {
        Assert.Equal("620 kcal", NutritionDisplay.Kcal(619.5m));
        Assert.Equal("1480 kcal", NutritionDisplay.Kcal(1480.0m));
        Assert.Equal("52 P · 58 C · 20 F", NutritionDisplay.Macros(52.3m, 57.5m, 19.6m));
    }

    [Theory]
    [InlineData("AiConfirmed", "Confirmed estimate")]
    [InlineData("AiRequested", "AI estimate")]
    [InlineData("AiAutoClosed", "Auto-estimated")]
    [InlineData("UserAdjusted", "Adjusted")]
    public void SourceLabels_AreConcise_AndNeverNameAProvider(string source, string label)
    {
        Assert.Equal(label, NutritionDisplay.SourceLabel(source));
        Assert.DoesNotContain("groq", label, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gpt", label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Confirm_SendsTheProposalUnchanged_AndEditSendsAdjustedValues()
    {
        var proposal = new NutritionEstimateResponse(620, 52, 58, 20, ["about 180 g chicken"]);

        Assert.Equal(new SetMealNutritionRequest(620, 52, 58, 20, "AiConfirmed"), NutritionDisplay.Confirm(proposal));
        Assert.Equal(new SetMealNutritionRequest(700, 60, 50, 25, "UserAdjusted"), NutritionDisplay.Adjusted(700, 60, 50, 25));
    }

    [Theory]
    [InlineData("Pasta", " Pasta ", false)]
    [InlineData("Pasta", "Pasta al pomodoro", true)]
    [InlineData("Pasta", "pasta", true)]
    public void DescriptionChanged_IgnoresOnlyOuterWhitespace(string original, string edited, bool changed)
    {
        Assert.Equal(changed, NutritionDisplay.DescriptionChanged(original, edited));
    }

    [Fact]
    public void AnalysisMessages_ExplainPartialOrUnavailableResults()
    {
        Assert.Null(NutritionDisplay.AnalysisMessage(new NutritionAnalysisResponse(3, 0, false, false, null)));
        Assert.Equal("1 meal could not be analyzed. Try again, or estimate it on its own.",
            NutritionDisplay.AnalysisMessage(new NutritionAnalysisResponse(3, 1, false, true, null)));
        Assert.Equal("Nutrition estimation is unavailable right now. Try again later.",
            NutritionDisplay.AnalysisMessage(new NutritionAnalysisResponse(1, 0, true, true, null)));
        Assert.Equal("More meals are waiting. Analyze remaining to continue.",
            NutritionDisplay.AnalysisMessage(new NutritionAnalysisResponse(20, 0, false, true, null)));
    }

    [Fact]
    public void UpdateRequest_CarriesTheClearConfirmationOnlyWhenGiven()
    {
        Assert.Null(MealJournal.UpdateRequest("Pasta", "", new TimeOnly(13, 0)).ClearNutrition);
        Assert.True(MealJournal.UpdateRequest("Pasta", "", new TimeOnly(13, 0), clearNutrition: true).ClearNutrition);
    }

    // ---- Home card ----

    [Fact]
    public void HomeCard_ShowsTodayCountsTotals_AndOneAction()
    {
        var card = Source("Nutrition", "HomeNutritionCard.razor");

        Assert.Matches(new Regex(@"<h2 class=""lo-section-title"">Nutrition</h2>"), card);
        Assert.Contains(">Today</p>", card);
        Assert.Contains("@NutritionDisplay.CountLine(summary)", card);
        Assert.Contains("@if (NutritionDisplay.ShowsTotals(summary))", card);
        Assert.Contains("NutritionDisplay.AnalyzeLabel(summary, isToday: true)", card);
        Assert.Contains("NutritionApi.AnalyzeDayAsync(Today, lifetime.Token)", card);
        Assert.Contains("<a href=\"nutrition\" class=\"btn btn-outline-secondary w-100 mt-3 lo-home-nutrition__action\">Open nutrition</a>", card);
    }

    [Fact]
    public void HomeCard_LoadsAndFailsOnItsOwn()
    {
        var card = Source("Nutrition", "HomeNutritionCard.razor");

        Assert.Contains("Loading nutrition...", card);
        Assert.Contains(">Retry</button>", card);
        Assert.Contains("NutritionApi.GetSummaryAsync(Today, lifetime.Token)", card);
        Assert.DoesNotContain("BudgetsApi", card);
        Assert.DoesNotContain("SessionsApi", card);
    }

    [Fact]
    public void HomeCard_LazyClosesPastDays_AfterFirstRender_Cancellably_WithoutFireAndForget()
    {
        var card = Source("Nutrition", "HomeNutritionCard.razor");

        Assert.Matches(new Regex(@"OnAfterRenderAsync\(bool firstRender\)\s*\{\s*if \(firstRender\)\s*\{\s*await LazyCloseAsync\(\);"), card);
        Assert.Contains("NutritionApi.LazyCloseAsync((int)DateTimeOffset.Now.Offset.TotalMinutes, lifetime.Token)", card);
        Assert.Contains("@implements IDisposable", card);
        Assert.Contains("lifetime.Cancel();", card);
        Assert.Contains("catch (OperationCanceledException)", card);
        Assert.DoesNotContain("_ = ", card);
        Assert.DoesNotContain("Task.Run", card);
        // Lazy close is never started from loading, so the summary renders first.
        Assert.DoesNotMatch(new Regex(@"OnInitializedAsync\(\)[^;]*LazyClose"), card);
    }

    [Fact]
    public void Home_KeepsBudgetAndTraining_AndAddsNutritionLast()
    {
        var home = Source("Pages", "Home.razor");

        Assert.Contains("<MonthlyBudgetCard", home);
        Assert.Contains("<HomeTrainingCard />", home);
        Assert.True(home.IndexOf("<HomeTrainingCard />", StringComparison.Ordinal) < home.IndexOf("<HomeNutritionCard />", StringComparison.Ordinal));
    }

    // ---- Nutrition page ----

    [Fact]
    public void Page_HasADailyNutritionSection_WithTheDayAction()
    {
        var page = Page();

        Assert.Matches(new Regex(@"<h2 class=""lo-section-title"">Daily nutrition</h2>\s*@if \(summary is not null && NutritionDisplay.AnalyzeLabel\(summary, isToday: false\)"), page);
        Assert.Contains("NutritionApi.AnalyzeDayAsync(day)", page);
        Assert.Contains("@NutritionDisplay.CountLine(summary)", page);
        Assert.Contains("NutritionApi.GetSummaryAsync(day)", page);
        Assert.True(page.IndexOf("Daily nutrition", StringComparison.Ordinal) < page.IndexOf(">Meals</h2>", StringComparison.Ordinal));
    }

    [Fact]
    public void Meals_ShowNutritionOrNotAnalyzed_WithEstimateOrReEstimate()
    {
        var page = Page();

        Assert.Contains("@MealNutrition(meal)", page);
        Assert.Contains("Not analyzed", page);
        Assert.Contains("@(meal.Nutrition is null ? \"Estimate\" : \"Re-estimate\")", page);
        Assert.Contains("@NutritionDisplay.SourceLabel(nutrition.Source)", page);
        // The description stays first and primary.
        Assert.True(page.IndexOf("<span class=\"meal-description d-block\">@meal.Description</span>", StringComparison.Ordinal)
            < page.IndexOf("@MealNutrition(meal)", StringComparison.Ordinal));
    }

    [Fact]
    public void Proposal_IsInline_WithConfirmEditCancel_AndAssumptions()
    {
        var page = Page();

        Assert.Contains("case ActionPanelKind.Proposal:", page);
        Assert.Contains(">AI estimate</p>", page);
        Assert.Contains("Based on", page);
        Assert.Contains("@foreach (var assumption in estimate.Assumptions)", page);
        Assert.Contains("@onclick=\"ConfirmProposalAsync\">@(isSaving ? \"Saving...\" : \"Confirm\")", page);
        Assert.Contains("@onclick=\"ShowEditNutrition\">Edit</button>", page);
        Assert.Contains("NutritionApi.SetNutritionAsync(meal.Id, NutritionDisplay.Confirm(estimate))", page);
        Assert.Contains("NutritionApi.SetNutritionAsync(meal.Id, NutritionDisplay.Adjusted(kcal, protein, carbs, fat))", page);
        Assert.Contains("NutritionApi.EstimateAsync(meal.Id)", page);
        // Cancel only closes the panel: nothing is written.
        Assert.Matches(new Regex(@"private void Close\(\)\s*\{[^}]*proposal = null;"), page);
        Assert.DoesNotContain("modal", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescriptionChange_OnAnAnalyzedMeal_AsksBeforeClearingNutrition()
    {
        var page = Page();

        Assert.Contains("case ActionPanelKind.ConfirmClearNutrition:", page);
        Assert.Contains("@NutritionApiClient.NutritionClearRequiredMessage", page);
        Assert.Contains("NutritionDisplay.DescriptionChanged(meal.Description, formDescription)", page);
        Assert.Contains("clearNutrition: actionPanel == ActionPanelKind.ConfirmClearNutrition", page);
        Assert.Contains("Save and clear", page);
    }

    [Fact]
    public void AddingAMeal_NeverCallsAi()
    {
        var page = Page();
        var add = Regex.Match(page, @"private async Task AddAsync\(\)[\s\S]*?\n\t\}").Value;

        Assert.Contains("NutritionApi.CreateMealAsync(", add);
        Assert.DoesNotContain("Estimate", add);
        Assert.DoesNotContain("Analyze", add);
    }

    [Fact]
    public void ExistingSnapshots_CanBeEditedFromTheMealActions()
    {
        var page = Page();

        Assert.Contains("@if (meal.Nutrition is not null)", page);
        Assert.Contains(">Edit nutrition</button>", page);
        Assert.Contains("<label for=\"nutrition-kcal\" class=\"form-label small\">kcal</label>", page);
    }

    [Fact]
    public void LongDescriptionsAndAssumptions_StillWrap()
    {
        var css = File.ReadAllText(Path.Combine(ComponentsRoot(), "Pages", "Nutrition", "Nutrition.razor.css"));

        Assert.Contains("white-space: pre-line;", css);
        Assert.Matches(new Regex(@"\.proposal-assumptions \{[^}]*overflow-wrap: anywhere;"), css);
        Assert.Matches(new Regex(@"\.meal-nutrition__values \{[^}]*min-width: 0;"), css);
        Assert.DoesNotContain("ellipsis", css);
    }

    private static DailyNutritionSummaryResponse Summary(int meals, int analyzed, decimal kcal = 0, decimal p = 0, decimal c = 0, decimal f = 0) =>
        new(Today, meals, analyzed, meals > 0 && meals == analyzed, kcal, p, c, f);

    private static string Page() => Source("Pages/Nutrition", "Nutrition.razor");

    private static string ComponentsRoot([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", "..", "src", "dotnet", "LifeOS.App", "Components"));

    private static string Source(string folder, string fileName) =>
        File.ReadAllText(Path.Combine(ComponentsRoot(), folder, fileName));
}
