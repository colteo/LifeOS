using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LifeOS.App.Services.Nutrition;
using LifeOS.Contracts.Nutrition;

namespace LifeOS.UnitTests.App;

// NUT-003 in the app: the plain .NET presentation rules (NutritionTargetDisplay), and the composition of
// the Nutrition page and the Home card, which the net10.0 test project cannot render. Those checks read
// the component sources.
public class NutritionTargetAppTests
{
    private static readonly DateOnly Today = new(2026, 10, 4);

    // ---- Presentation rules ----

    [Fact]
    public void ActiveTarget_ShowsKcalAndTheTargetedMacros()
    {
        var target = Target(2200, 160, 240, 70);

        Assert.Equal("2200 kcal", NutritionTargetDisplay.Kcal(target));
        Assert.Equal("160 P · 240 C · 70 F", NutritionTargetDisplay.Macros(target));
        Assert.Equal("Effective from 4 October", NutritionTargetDisplay.EffectiveFrom(Today, Today));
        Assert.Equal("Effective from 30 December 2025", NutritionTargetDisplay.EffectiveFrom(new DateOnly(2025, 12, 30), Today));
    }

    [Fact]
    public void PartialTargets_ShowOnlyWhatIsTargeted()
    {
        Assert.Null(NutritionTargetDisplay.Kcal(Target(null, 160, null, null)));
        Assert.Equal("160 P", NutritionTargetDisplay.Macros(Target(null, 160, null, null)));
        Assert.Null(NutritionTargetDisplay.Macros(Target(2200, null, null, null)));
        Assert.Equal("160 P · 70 F", NutritionTargetDisplay.Macros(Target(null, 159.6m, null, 70)));
    }

    [Fact]
    public void Comparisons_AreConsumedOverTarget_AndConsumedOnlyWithoutATarget()
    {
        Assert.Equal("1840 / 2200 kcal", NutritionTargetDisplay.KcalLine(1840, 2200));
        Assert.Equal("1840 kcal", NutritionTargetDisplay.KcalLine(1840, null));
        Assert.Equal("138 / 160 g", NutritionTargetDisplay.GramsLine(138.4m, 160));
        Assert.Equal("138 g", NutritionTargetDisplay.GramsLine(138.4m, null));
    }

    [Fact]
    public void CompactMacroLine_ComparesOnlyTargetedMacros_AndIsTheNut002LineWithoutATarget()
    {
        var withTarget = Summary(3, 2, 1480, 104, 151, 49) with { Target = new DailyNutritionTargetResponse(2200, 160, 240, 70) };
        var partialTarget = Summary(3, 3, 1840, 138, 191, 61) with { Target = new DailyNutritionTargetResponse(2200, null, 240, null) };
        var noTarget = Summary(3, 3, 1840, 138, 191, 61);

        Assert.Equal("104 / 160 P · 151 / 240 C · 49 / 70 F", NutritionTargetDisplay.MacrosLine(withTarget));
        Assert.Equal("138 P · 191 / 240 C · 61 F", NutritionTargetDisplay.MacrosLine(partialTarget));
        Assert.Equal(NutritionDisplay.Macros(138, 191, 61), NutritionTargetDisplay.MacrosLine(noTarget));
        Assert.Equal(NutritionDisplay.Kcal(1840), NutritionTargetDisplay.KcalLine(1840, noTarget.Target?.CaloriesKcal));
    }

    [Fact]
    public void Copy_NeverJudgesTheDay()
    {
        var copy = string.Join(" ", typeof(NutritionTargetDisplay).GetFields()
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetValue(null)!))
            + Source("Pages/Nutrition", "Nutrition.razor") + Source("Nutrition", "HomeNutritionCard.razor");

        foreach (var word in new[] { "over target", "over your", "exceeded", "you failed", "goal met", "on track", "too much", "well done", "great job" })
        {
            Assert.DoesNotContain(word, copy, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(2200, null, null, null)]
    [InlineData(null, 160, null, null)]
    [InlineData(2200, 160, 240, 70)]
    public void EachFieldIsOptional(int? kcal, int? protein, int? carbs, int? fat)
    {
        Assert.Null(NutritionTargetDisplay.Validate(kcal, protein, carbs, fat, hasTarget: false));
    }

    [Fact]
    public void AllBlank_IsRejected_AndPointsToRemoveTargetsWhenThereIsOne()
    {
        Assert.Equal(NutritionTargetDisplay.UseRemoveInstead, NutritionTargetDisplay.Validate(null, null, null, null, hasTarget: true));
        Assert.Contains("Remove targets", NutritionTargetDisplay.UseRemoveInstead);
        Assert.Equal(NutritionTargetDisplay.EnterAtLeastOne, NutritionTargetDisplay.Validate(null, null, null, null, hasTarget: false));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(null, -1)]
    public void ZeroOrNegative_IsRejected(int? kcal, int? protein)
    {
        Assert.Equal(NutritionTargetDisplay.MustBePositive, NutritionTargetDisplay.Validate(kcal, protein, null, null, hasTarget: false));
    }

    [Fact]
    public void TheRequest_CarriesNoEffectiveDate_OnlyTheValuesAndTheDeviceOffset()
    {
        var request = NutritionTargetDisplay.Request(2200, null, 240, null, 120);

        Assert.Equal(new SetNutritionTargetRequest(2200, null, 240, null, 120), request);
        Assert.DoesNotContain(typeof(SetNutritionTargetRequest).GetProperties(), property => property.Name.Contains("Date") || property.Name.Contains("Effective"));
    }

    // ---- Nutrition page ----

    [Fact]
    public void Page_HasADailyTargetsSection_AfterTheJournal()
    {
        var page = Page();

        Assert.Contains("<h2 class=\"lo-section-title\">Daily targets</h2>", page);
        Assert.True(page.IndexOf(">Meals</h2>", StringComparison.Ordinal) < page.IndexOf("@DailyTargets()", StringComparison.Ordinal));
        Assert.Contains("NutritionApi.GetCurrentTargetAsync(UtcOffsetMinutes)", page);
        Assert.Contains("Task.WhenAll(LoadAsync(), LoadTargetAsync())", page);
        Assert.Contains("Loading targets...", page);
    }

    [Fact]
    public void NoTarget_ShowsNoTargets_WithSetTargets()
    {
        var page = Page();

        Assert.Contains("<p class=\"lo-muted mb-0\">No targets</p>", page);
        Assert.Contains("@(targetState.Target is null ? \"Set targets\" : \"Edit\")", page);
        Assert.Matches(new Regex(@"class=""lo-header-action""[^>]*@onclick=""ShowTargetForm"""), page);
    }

    [Fact]
    public void ActiveTarget_ShowsNumbersAndEffectiveDate_WithoutProgressBars()
    {
        var page = Page();

        Assert.Contains("NutritionTargetDisplay.Kcal(target)", page);
        Assert.Contains("NutritionTargetDisplay.Macros(target)", page);
        Assert.Contains("@NutritionTargetDisplay.EffectiveFrom(target.EffectiveFrom, Today)", page);
        Assert.DoesNotContain("<progress", page);
        Assert.DoesNotContain("progress-bar", page);
    }

    [Fact]
    public void SetAndEdit_UseTheSameInlineForm_WithFourOptionalFields()
    {
        var page = Page();

        foreach (var (id, label) in new[] { ("target-kcal", "Calories"), ("target-protein", "Protein"), ("target-carbs", "Carbs"), ("target-fat", "Fat") })
        {
            Assert.Contains($"<label for=\"{id}\" class=\"form-label small mb-0\">{label}</label>", page);
            Assert.Matches(new Regex($@"<input id=""{id}""[^>]*type=""number""(?![^>]*required)[^>]*/>"), page);
        }

        Assert.Contains("@NutritionTargetDisplay.ChangesApplyFromToday", page);
        Assert.Equal("Changes apply from today.", NutritionTargetDisplay.ChangesApplyFromToday);
        Assert.Contains("NutritionTargetDisplay.Validate(targetKcal, targetProtein, targetCarbs, targetFat, targetState?.Target is not null)", page);
        Assert.Contains("NutritionApi.SetTargetAsync(", page);
        Assert.Contains("@onclick=\"CloseTargetPanel\">Cancel</button>", page);
    }

    [Fact]
    public void RemoveTargets_RequiresConfirmation()
    {
        var page = Page();

        Assert.Contains("@onclick=\"ShowConfirmRemoveTarget\">Remove targets</button>", page);
        Assert.Contains("@NutritionTargetDisplay.RemoveConfirmation", page);
        Assert.Contains("@onclick=\"RemoveTargetAsync\"", page);
        Assert.Contains("NutritionApi.RemoveTargetAsync(UtcOffsetMinutes)", page);
        // Remove is reachable only from the confirmation panel.
        Assert.Matches(new Regex(@"TargetPanelKind\.ConfirmRemove\)\s*\{[^}]*RemoveConfirmation"), page);
    }

    [Fact]
    public void DailyTotals_CompareWithTheSelectedDaysTarget_AndKeepTheCountLine()
    {
        var page = Page();

        Assert.Contains("@if (summary.Target is { } dayTarget)", page);
        Assert.Contains("NutritionTargetDisplay.KcalLine(summary.CaloriesKcal, dayTarget.CaloriesKcal)", page);
        Assert.Contains("<dt>Protein</dt><dd>@NutritionTargetDisplay.GramsLine(summary.ProteinGrams, dayTarget.ProteinGrams)</dd>", page);
        Assert.Contains("<dt>Carbs</dt><dd>@NutritionTargetDisplay.GramsLine(summary.CarbsGrams, dayTarget.CarbsGrams)</dd>", page);
        Assert.Contains("<dt>Fat</dt><dd>@NutritionTargetDisplay.GramsLine(summary.FatGrams, dayTarget.FatGrams)</dd>", page);
        // The day's target comes with the selected day's summary (historical), never from the current target.
        Assert.Contains("NutritionApi.GetSummaryAsync(day)", page);
        Assert.DoesNotContain("targetState.Target.CaloriesKcal", page);
        // Partial completeness stays above the totals.
        Assert.True(page.IndexOf("@NutritionDisplay.CountLine(summary)", StringComparison.Ordinal)
            < page.IndexOf("@if (summary.Target is { } dayTarget)", StringComparison.Ordinal));
    }

    [Fact]
    public void SavingOrRemovingTargets_ReloadsTheDay_AndNeverCallsAi()
    {
        var page = Page();
        var targetCode = page[page.IndexOf("private async Task LoadTargetAsync()", StringComparison.Ordinal)..page.IndexOf("// On success the form or panel closes", StringComparison.Ordinal)];

        Assert.Contains("await LoadAsync();", targetCode);
        Assert.DoesNotContain("EstimateAsync", targetCode);
        Assert.DoesNotContain("AnalyzeDayAsync", targetCode);
    }

    [Fact]
    public void Nut002Actions_AreUnchanged()
    {
        var page = Page();

        Assert.Contains("@(meal.Nutrition is null ? \"Estimate\" : \"Re-estimate\")", page);
        Assert.Contains("NutritionApi.AnalyzeDayAsync(day)", page);
        Assert.Contains("NutritionApi.CreateMealAsync(", page);
        Assert.Contains("NutritionApi.UpdateMealAsync(meal.Id,", page);
        Assert.Contains("NutritionApi.DeleteMealAsync(meal.Id)", page);
    }

    [Fact]
    public void TargetForm_FitsAPhone()
    {
        var css = File.ReadAllText(Path.Combine(ComponentsRoot(), "Pages", "Nutrition", "Nutrition.razor.css"));

        Assert.Matches(new Regex(@"\.target-fields \{[^}]*grid-template-columns: 4\.5rem minmax\(0, 1fr\) 2rem;"), css);
    }

    // ---- Home ----

    [Fact]
    public void HomeCard_ComparesWithTodaysTarget_AndKeepsTheNut002LinesWithout()
    {
        var card = Source("Nutrition", "HomeNutritionCard.razor");

        Assert.Contains("@NutritionTargetDisplay.KcalLine(summary.CaloriesKcal, summary.Target?.CaloriesKcal)", card);
        Assert.Contains("@NutritionTargetDisplay.MacrosLine(summary)", card);
        Assert.Contains("@if (NutritionDisplay.ShowsTotals(summary))", card);
        Assert.Contains("@NutritionDisplay.CountLine(summary)", card);
        // The action still depends only on completeness, never on the target.
        Assert.Contains("NutritionDisplay.AnalyzeLabel(summary, isToday: true)", card);
        Assert.DoesNotContain("Target is", card);
        Assert.DoesNotContain("TargetAsync", card);
    }

    private static NutritionTargetResponse Target(decimal? kcal, decimal? p, decimal? c, decimal? f) => new(Today, kcal, p, c, f, "Manual");

    private static DailyNutritionSummaryResponse Summary(int meals, int analyzed, decimal kcal = 0, decimal p = 0, decimal c = 0, decimal f = 0) =>
        new(Today, meals, analyzed, meals > 0 && meals == analyzed, kcal, p, c, f);

    private static string Page() => Source("Pages/Nutrition", "Nutrition.razor");

    private static string ComponentsRoot([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", "..", "src", "dotnet", "LifeOS.App", "Components"));

    private static string Source(string folder, string fileName) =>
        File.ReadAllText(Path.Combine(ComponentsRoot(), folder, fileName));
}
