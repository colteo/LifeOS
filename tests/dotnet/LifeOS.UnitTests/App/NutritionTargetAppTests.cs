using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LifeOS.App.Services.Nutrition;
using LifeOS.Contracts.Nutrition;

namespace LifeOS.UnitTests.App;

// NUT-003 in the app: the plain .NET presentation rules (NutritionTargetDisplay, TargetPlanDraft) and the
// composition of the Targets page, the Nutrition page and the Home card, which the net10.0 test project
// cannot render. Those checks read the component sources.
public class NutritionTargetAppTests
{
	private static readonly DateOnly Today = new(2026, 10, 4);

	// ---- Values and comparisons ----

	[Fact]
	public void Targets_ShowKcalAndOnlyTheTargetedMacros()
	{
		Assert.Equal("2200 kcal", NutritionTargetDisplay.Kcal(new(2200, 160, 240, 70)));
		Assert.Equal("160 P · 240 C · 70 F", NutritionTargetDisplay.Macros(new(2200, 160, 240, 70)));
		Assert.Null(NutritionTargetDisplay.Kcal(new(null, 160, null, null)));
		Assert.Equal("160 P · 70 F", NutritionTargetDisplay.Macros(new(null, 159.6m, null, 70)));
		Assert.Null(NutritionTargetDisplay.Macros(new(2200, null, null, null)));
	}

	[Fact]
	public void Comparisons_AreConsumedOverTarget_AndConsumedOnlyWithoutATarget()
	{
		Assert.Equal("1840 / 2200 kcal", NutritionTargetDisplay.KcalLine(1840, 2200));
		Assert.Equal("1840 kcal", NutritionTargetDisplay.KcalLine(1840, null));
		Assert.Equal("138 / 160 g", NutritionTargetDisplay.GramsLine(138.4m, 160));
		Assert.Equal("138 g", NutritionTargetDisplay.GramsLine(138.4m, null));

		var partial = Summary(3, 2, 1480, 104, 151, 49) with { Target = new(2200, 160, null, 70) };
		Assert.Equal("104 / 160 P · 151 C · 49 / 70 F", NutritionTargetDisplay.MacrosLine(partial));
		Assert.Equal(NutritionDisplay.Macros(104, 151, 49), NutritionTargetDisplay.MacrosLine(Summary(3, 2, 1480, 104, 151, 49)));
	}

	[Fact]
	public void Copy_StaysNeutral()
	{
		var copy = string.Join(" ", typeof(NutritionTargetDisplay).GetFields().Where(field => field.IsLiteral).Select(field => (string)field.GetValue(null)!))
			+ Page() + TargetsPage() + Source("Nutrition", "HomeNutritionCard.razor");

		foreach (var word in new[] { "exceeded", "over target", "you failed", "cheat", "reward", "punish", "good day", "bad day", "well done", "free meal" })
		{
			Assert.DoesNotContain(word, copy, StringComparison.OrdinalIgnoreCase);
		}
	}

	// ---- Periods ----

	[Fact]
	public void PeriodLabels_OmitTheCurrentYear()
	{
		Assert.Equal("7 Oct – 3 Nov", NutritionTargetDisplay.PeriodLabel(new(2026, 10, 7), new(2026, 11, 3), Today));
		Assert.Equal("2 Dec 2026 – 5 Jan 2027", NutritionTargetDisplay.PeriodLabel(new(2026, 12, 2), new(2027, 1, 5), Today));
		Assert.Equal("1 Feb – 3 Mar 2027", NutritionTargetDisplay.PeriodLabel(new(2027, 2, 1), new(2027, 3, 3), Today));
	}

	[Fact]
	public void Periods_AreCurrentUpcomingOrPast_WithInclusiveBoundaries()
	{
		Assert.Equal(TargetPeriodStatus.Current, NutritionTargetDisplay.StatusOf(Plan(Today, Today.AddDays(5)), Today));
		Assert.Equal(TargetPeriodStatus.Current, NutritionTargetDisplay.StatusOf(Plan(Today.AddDays(-5), Today), Today));
		Assert.Equal(TargetPeriodStatus.Upcoming, NutritionTargetDisplay.StatusOf(Plan(Today.AddDays(1), Today.AddDays(5)), Today));
		Assert.Equal(TargetPeriodStatus.Past, NutritionTargetDisplay.StatusOf(Plan(Today.AddDays(-9), Today.AddDays(-1)), Today));
	}

	[Fact]
	public void PeriodSummary_ShowsTheDefaultAndTheDaysThatDiffer()
	{
		var plan = Plan(new(2026, 10, 7), new(2026, 11, 3), new(2200, 160, 240, 70),
			("Monday", "Custom"), ("Wednesday", "Custom"), ("Friday", "Custom"), ("Sunday", "NoTarget"));

		Assert.Equal("2200 kcal default", NutritionTargetDisplay.DefaultLine(plan));
		Assert.Equal(["Mon · Wed · Fri custom", "Sun no target"], NutritionTargetDisplay.WeekLines(plan));
		Assert.Equal("No default target", NutritionTargetDisplay.DefaultLine(plan with { DefaultTarget = null }));
		Assert.Empty(NutritionTargetDisplay.WeekLines(Plan(Today, Today)));
	}

	[Fact]
	public void Overlaps_AreFoundExcludingTheEditedPeriod_AndAdjacentPeriodsAreFine()
	{
		var first = Plan(new(2026, 10, 7), new(2026, 11, 3));
		var plans = new[] { first, Plan(new(2026, 11, 4), new(2026, 12, 1)) };

		Assert.Equal(first, NutritionTargetDisplay.FindOverlap(plans, new(2026, 10, 28), new(2026, 11, 30), null));
		Assert.Null(NutritionTargetDisplay.FindOverlap(plans, new(2026, 12, 2), new(2027, 1, 5), null));
		Assert.Null(NutritionTargetDisplay.FindOverlap(plans, new(2026, 10, 1), new(2026, 11, 3), first.Id));
		Assert.Equal("This period overlaps 7 Oct – 3 Nov.", NutritionTargetDisplay.OverlapMessage(first, Today));
	}

	// ---- Plan editor draft ----

	[Fact]
	public void ANewDraft_StartsTodayForFourWeeks_WithEveryDayDefault()
	{
		var draft = TargetPlanDraft.New(Today);

		Assert.Equal((Today, Today.AddDays(27)), (draft.StartsOn, draft.EndsOn));
		Assert.Equal(TargetPlanDraft.Weekdays, draft.Days.Select(day => day.Weekday));
		Assert.All(draft.Days, day => Assert.Equal("Default", day.Mode));
	}

	[Fact]
	public void TheDraft_SendsCustomValuesOnlyForCustomDays()
	{
		var draft = TargetPlanDraft.New(Today);
		draft.Default.CaloriesKcal = 2200;
		draft.Days[0].Mode = "Custom";
		draft.Days[0].Target.CaloriesKcal = 2500;
		draft.Days[6].Target.CaloriesKcal = 2800;
		draft.Days[6].Mode = "NoTarget";

		var request = draft.ToRequest();

		Assert.Equal(new NutritionTargetValuesDto(2200, null, null, null), request.DefaultTarget);
		Assert.Equal(new NutritionTargetDayRuleDto("Monday", "Custom", new(2500, null, null, null)), request.WeeklyRules![0]);
		Assert.Equal(new NutritionTargetDayRuleDto("Sunday", "NoTarget", null), request.WeeklyRules[6]);
		Assert.Equal(7, request.WeeklyRules.Count);
	}

	[Fact]
	public void TheDraft_RoundTripsAPeriod()
	{
		var plan = Plan(new(2026, 10, 7), new(2026, 11, 3), new(2200, null, null, null), ("Sunday", "Custom"));
		plan = plan with { WeeklyRules = plan.WeeklyRules.Select(rule => rule.Weekday == "Sunday" ? rule with { Target = new(2800, null, null, null) } : rule).ToList() };

		var draft = TargetPlanDraft.From(plan);

		Assert.Equal(plan.Id, draft.Id);
		Assert.True(draft.Days[6].IsCustom);
		Assert.Equal(2800m, draft.Days[6].Target.CaloriesKcal);
		Assert.Equal(plan.WeeklyRules, draft.ToRequest().WeeklyRules);
	}

	[Fact]
	public void TheDraft_ExplainsInvalidConfigurations()
	{
		var draft = TargetPlanDraft.New(Today);
		Assert.Contains("Monday uses the default target, but the period has none.", draft.Validate([], Today));

		draft.Default.CaloriesKcal = 2200;
		draft.Days[4].Mode = "Custom";
		Assert.Equal(["Friday: enter at least one custom target."], draft.Validate([], Today));

		draft.Days[4].Target.CaloriesKcal = 0;
		Assert.Contains("Targets must be more than 0.", draft.Validate([], Today));

		draft.Days[4].Target.CaloriesKcal = 2500;
		draft.EndsOn = draft.StartsOn!.Value.AddDays(-1);
		Assert.Equal(["The period must end on or after its first day."], draft.Validate([], Today));

		draft.EndsOn = draft.StartsOn.Value;
		Assert.Empty(draft.Validate([], Today));
	}

	[Fact]
	public void TheDraft_ReportsOverlapsLive_ExcludingItself()
	{
		var existing = Plan(new(2026, 10, 7), new(2026, 11, 3));
		var draft = TargetPlanDraft.New(Today);
		draft.Default.CaloriesKcal = 2200;
		draft.StartsOn = new(2026, 10, 28);
		draft.EndsOn = new(2026, 11, 30);

		Assert.Equal("This period overlaps 7 Oct – 3 Nov.", draft.OverlapError([existing], Today));
		Assert.Contains("This period overlaps 7 Oct – 3 Nov.", draft.Validate([existing], Today));

		draft.StartsOn = new(2026, 11, 4);
		Assert.Null(draft.OverlapError([existing], Today));
		Assert.Null(TargetPlanDraft.From(existing).OverlapError([existing], Today));
	}

	// ---- Targets page ----

	[Fact]
	public void TargetsPage_HasItsRoute_AndGroupsCurrentUpcomingPast()
	{
		var page = TargetsPage();

		Assert.StartsWith("@page \"/nutrition/targets\"", page);
		Assert.Contains("<PageHeader Title=\"Nutrition targets\" BackHref=\"nutrition\" />", page);
		Assert.Contains("@Group(\"Current\", TargetPeriodStatus.Current)", page);
		Assert.Contains("@Group(\"Upcoming\", TargetPeriodStatus.Upcoming)", page);
		Assert.Contains("aria-expanded=\"@(showPast ? \"true\" : \"false\")\"", page);
		Assert.Contains("NutritionApi.GetTargetPlansAsync()", page);
		Assert.Contains("No target periods yet.", page);
	}

	[Fact]
	public void TargetsPage_HasNewPeriod_AndEditPerPeriod()
	{
		var page = TargetsPage();

		Assert.Matches(new Regex(@"class=""lo-header-action""[^>]*@onclick=""NewPeriod"">\s*<AppIcon Name=""plus""[^>]*/>\s*New period"), page);
		Assert.Contains("@onclick=\"() => Edit(plan)\">Edit</button>", page);
		Assert.Contains("@NutritionTargetDisplay.PeriodLabel(plan.StartsOn, plan.EndsOn, Today)", page);
		Assert.Contains("@NutritionTargetDisplay.DefaultLine(plan)", page);
		Assert.Contains("NutritionTargetDisplay.WeekLines(plan)", page);
	}

	[Fact]
	public void PlanEditor_HasDatesDefaultAndAWeeklyPattern()
	{
		var page = TargetsPage();

		Assert.Contains("<input id=\"period-from\" type=\"date\" class=\"form-control\" @bind=\"value.StartsOn\" @bind:after=\"CheckOverlap\" />", page);
		Assert.Contains("<input id=\"period-to\" type=\"date\" class=\"form-control\" @bind=\"value.EndsOn\" @bind:after=\"CheckOverlap\" />", page);
		Assert.Contains("@Fields(value.Default, \"default\")", page);
		Assert.Contains(">Weekly pattern</p>", page);
		Assert.Contains("@foreach (var mode in TargetPlanDraft.Modes)", page);
		Assert.Equal(["Default", "Custom", "NoTarget"], TargetPlanDraft.Modes);
		Assert.Equal("No target", NutritionTargetDisplay.ModeLabel("NoTarget"));
	}

	[Fact]
	public void PlanEditor_ShowsCustomFieldsOnlyForCustomDays()
	{
		var page = TargetsPage();

		Assert.Matches(new Regex(@"@if \(day\.IsCustom\)\s*\{\s*<div class=""targets-day__custom"">\s*@Fields\(day\.Target"), page);
	}

	[Fact]
	public void PlanEditor_ShowsOverlapsAndKeepsSaveDisabledUntilResolved()
	{
		var page = TargetsPage();

		Assert.Contains("private void CheckOverlap() => overlap = draft?.OverlapError(plans ?? [], Today);", page);
		Assert.Contains("disabled=\"@(isSaving || overlap is not null)\"", page);
		Assert.Contains("<div class=\"alert alert-warning mt-2 mb-0 py-2 small\" role=\"alert\">@overlap</div>", page);
		// A 409 from the API (a period saved meanwhile) keeps the editor open with its message.
		Assert.Contains("errors = result.Errors;", page);
	}

	[Fact]
	public void DeletingAPeriod_RequiresConfirmation()
	{
		var page = TargetsPage();

		Assert.Contains("@onclick=\"() => confirmingDelete = true\">Delete period</button>", page);
		Assert.Contains("@NutritionTargetDisplay.DeleteConfirmation(existing, Today)", page);
		Assert.Contains("NutritionApi.DeleteTargetPlanAsync(id)", page);
		Assert.Contains("Meals are not affected.", NutritionTargetDisplay.DeleteConfirmation(Plan(Today, Today), Today));
	}

	[Fact]
	public void TargetsPage_FitsAPhone_WithoutACalendar()
	{
		var css = File.ReadAllText(Path.Combine(ComponentsRoot(), "Pages", "Nutrition", "NutritionTargets.razor.css"));

		Assert.Matches(new Regex(@"\.targets-fields \{[^}]*grid-template-columns: repeat\(2, minmax\(0, 1fr\)\);"), css);
		Assert.DoesNotContain("<table", TargetsPage());
	}

	// ---- Nutrition page ----

	[Fact]
	public void NutritionPage_LinksToTheTargetsPage()
	{
		Assert.Contains("<a href=\"nutrition/targets\" class=\"btn btn-link btn-sm px-0\">Target periods</a>", Page());
	}

	[Fact]
	public void DailyTotals_CompareWithTheResolvedTarget_AndKeepTheCountLine()
	{
		var page = Page();

		Assert.Contains("@if (summary.Target is { } dayTarget)", page);
		Assert.Contains("NutritionTargetDisplay.KcalLine(summary.CaloriesKcal, dayTarget.CaloriesKcal)", page);
		Assert.Contains("<dt>Protein</dt><dd>@NutritionTargetDisplay.GramsLine(summary.ProteinGrams, dayTarget.ProteinGrams)</dd>", page);
		Assert.True(page.IndexOf("@NutritionDisplay.CountLine(summary)", StringComparison.Ordinal)
			< page.IndexOf("@if (summary.Target is { } dayTarget)", StringComparison.Ordinal));
	}

	[Fact]
	public void TheDayTarget_IsResolvedForTheSelectedDay()
	{
		var page = Page();

		Assert.Contains("NutritionApi.GetResolvedTargetAsync(day)", page);
		Assert.Contains("@NutritionTargetDisplay.NoPeriodForDay", page);
		Assert.Contains("@NutritionTargetDisplay.NoTargetForDay", page);
		Assert.Contains("@(resolved?.Override is null ? \"Target\" : \"Target override\")", page);
	}

	[Fact]
	public void EditForThisDay_IsOfferedOnlyInsideAPeriod_WithCustomOrNoTarget()
	{
		var page = Page();

		Assert.Contains("@if (resolved is { CoveredByPlan: true } && !editingOverride)", page);
		Assert.Contains("@(resolved.Override is null ? \"Edit for this day\" : \"Edit\")", page);
		Assert.Contains("Use custom target", page);
		Assert.Contains("No target for this day", page);
		Assert.Contains("NutritionApi.SetTargetOverrideAsync(day,", page);
	}

	[Fact]
	public void RemoveOverride_ReturnsTheDayToItsRule()
	{
		var page = Page();

		Assert.Contains("@(overrideSaving ? \"Removing...\" : \"Remove override\")", page);
		Assert.Contains("NutritionApi.RemoveTargetOverrideAsync(day)", page);
	}

	[Fact]
	public void Nut001And002Actions_AreUnchanged()
	{
		var page = Page();

		Assert.Contains("@(meal.Nutrition is null ? \"Estimate\" : \"Re-estimate\")", page);
		Assert.Contains("NutritionApi.AnalyzeDayAsync(day)", page);
		Assert.Contains("NutritionApi.CreateMealAsync(", page);
		Assert.Contains("NutritionApi.UpdateMealAsync(meal.Id,", page);
		Assert.Contains("NutritionApi.DeleteMealAsync(meal.Id)", page);
		var targetCode = page[page.IndexOf("private void ShowOverride()", StringComparison.Ordinal)..page.IndexOf("// On success the form or panel closes", StringComparison.Ordinal)];
		Assert.DoesNotContain("EstimateAsync", targetCode);
		Assert.DoesNotContain("AnalyzeDayAsync", targetCode);
	}

	// ---- Home ----

	[Fact]
	public void Home_UsesOnlyTodaysResolvedTarget()
	{
		var card = Source("Nutrition", "HomeNutritionCard.razor");

		Assert.Contains("@NutritionTargetDisplay.KcalLine(summary.CaloriesKcal, summary.Target?.CaloriesKcal)", card);
		Assert.Contains("@NutritionTargetDisplay.MacrosLine(summary)", card);
		Assert.Contains("NutritionDisplay.AnalyzeLabel(summary, isToday: true)", card);
		Assert.DoesNotContain("Plan", card);
		Assert.DoesNotContain("Weekday", card);
		Assert.DoesNotContain("Override", card);
	}

	private static NutritionTargetPlanResponse Plan(DateOnly startsOn, DateOnly endsOn, NutritionTargetValuesDto? defaultTarget = null,
		params (string Day, string Mode)[] modes) =>
		new(Guid.NewGuid(), startsOn, endsOn, defaultTarget ?? new(2200, null, null, null),
			TargetPlanDraft.Weekdays.Select(day => new NutritionTargetDayRuleDto(day,
				modes.FirstOrDefault(mode => mode.Day == day).Mode ?? "Default", null)).ToList());

	private static DailyNutritionSummaryResponse Summary(int meals, int analyzed, decimal kcal = 0, decimal p = 0, decimal c = 0, decimal f = 0) =>
		new(Today, meals, analyzed, meals > 0 && meals == analyzed, kcal, p, c, f);

	private static string Page() => Source("Pages/Nutrition", "Nutrition.razor");

	private static string TargetsPage() => Source("Pages/Nutrition", "NutritionTargets.razor");

	private static string ComponentsRoot([CallerFilePath] string testFile = "") =>
		Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", "..", "src", "dotnet", "LifeOS.App", "Components"));

	private static string Source(string folder, string fileName) =>
		File.ReadAllText(Path.Combine(ComponentsRoot(), folder, fileName));
}
