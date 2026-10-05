using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LifeOS.App.Services.Navigation;
using LifeOS.App.Services.Nutrition;
using LifeOS.Contracts.Nutrition;

namespace LifeOS.UnitTests.App;

// The Nutrition meal journal in the app (NUT-001): navigation (More, dock since NAV-001), the journal's plain .NET
// presentation rules, and the page composition, which the net10.0 test project cannot render. Those
// checks read the component sources.
public class NutritionAppTests
{
    private static readonly DateOnly Today = new(2026, 10, 3);

    // ---- Navigation ----

    [Fact]
    public void More_ListsFinanceGymNutrition_InThatOrder()
    {
        var more = Source("Pages", "More.razor");

        var finance = more.IndexOf("new(\"Finance\"", StringComparison.Ordinal);
        var gym = more.IndexOf("new(\"Gym\"", StringComparison.Ordinal);
        // NUT-003: the Nutrition entry opens the module hub (Food diary, Targets).
        var nutrition = more.IndexOf("new(\"Nutrition\", \"Food diary and targets\", \"nutrition\", \"nutrition/hub\")", StringComparison.Ordinal);
        Assert.True(finance >= 0 && finance < gym && gym < nutrition);
    }

    [Fact]
    public void NutritionPage_IsRoutedAtNutrition_AsTheFoodDiary_WithBackToTheNutritionHub()
    {
        var page = Page();

        Assert.StartsWith("@page \"/nutrition\"", page);
        Assert.Contains("<PageHeader Title=\"Food diary\" BackHref=\"nutrition/hub\" />", page);
    }

    // NAV-001: the Food diary has its own dock item; More no longer lights up on it.
    [Theory]
    [InlineData("nutrition", true)]
    [InlineData("nutrition/anything", true)]
    [InlineData("nutritionx", false)]
    [InlineData("nutrition/hub", false)]
    [InlineData("nutrition/targets", false)]
    [InlineData("", false)]
    public void Nutrition_IsActiveOnTheFoodDiary(string path, bool active)
    {
        Assert.Equal(active, Item("Nutrition").IsActive(path));
    }

    [Theory]
    [InlineData("nutrition")]
    [InlineData("nutrition/anything")]
    public void TransactionsPlusAndMore_AreNotActiveOnTheFoodDiary(string path)
    {
        Assert.False(Item("Transactions").IsActive(path));
        Assert.False(Item("Quick add").IsActive(path));
        Assert.False(Item("More").IsActive(path));
    }

    // NUT-002 put Nutrition on Home as its own card (NutritionAiAppTests).
    [Fact]
    public void Home_ShowsNutritionOnlyThroughItsCard()
    {
        var home = Source("Pages", "Home.razor");

        Assert.Contains("<HomeNutritionCard />", home);
        Assert.DoesNotContain("NutritionApi", home);
    }

    [Fact]
    public void NutritionIcon_IsAForkAndKnifeLineIcon()
    {
        var icon = Source("Shared", "AppIcon.razor");

        Assert.Contains("case \"nutrition\":", icon);
        Assert.Contains("@* Fork and knife. *@", icon);
    }

    // ---- Page composition ----

    [Fact]
    public void AddMeal_IsTheMealsSectionAction_AndOpensInline()
    {
        var page = Page();

        Assert.Matches(new Regex(@"<h2 class=""lo-section-title"">Meals</h2>\s*<button type=""button"" class=""lo-header-action"" aria-expanded="), page);
        Assert.Matches(new Regex(@"Add meal\s*</button>"), page);
        Assert.Contains("aria-controls=\"add-meal-form\"", page);
        Assert.Contains("<div id=\"add-meal-form\" class=\"pt-2\">", page);
        Assert.Contains("What did you eat?", page);
        Assert.Contains("<textarea id=\"meal-description\"", page);
        Assert.Contains("<input id=\"meal-time\" type=\"time\"", page);
        Assert.Contains("MealJournal.CreateRequest(formDescription, formMealType, selectedDay, time, TimeZoneInfo.Local)", page);
        // No separate date picker: the day comes from the page.
        Assert.DoesNotContain("type=\"date\"", page);
        Assert.DoesNotContain("modal", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MealType_IsOptional_InTheForm()
    {
        var page = Page();

        Assert.Contains("<label for=\"meal-type\" class=\"form-label small\">Meal type</label>", page);
        Assert.Contains("<option value=\"\">Optional</option>", page);
        Assert.Contains("@foreach (var type in MealJournal.MealTypes)", page);
    }

    [Fact]
    public void EachMeal_HasAnInlineEditDeletePanel_OneAtATime_WithDeleteConfirmation()
    {
        var page = Page();

        Assert.Contains("<AppIcon Name=\"more-vertical\" />", page);
        Assert.Contains("aria-expanded=\"@(IsActionOpen(meal.Id) ? \"true\" : \"false\")\"", page);
        Assert.Contains("@ActionPanel(meal)", page);
        Assert.Contains(">Edit</button>", page);
        Assert.Contains(">Delete</button>", page);
        Assert.Contains("case ActionPanelKind.ConfirmDelete:", page);
        Assert.Contains("This can't be undone.", page);
        Assert.Contains("NutritionApi.UpdateMealAsync(meal.Id,", page);
        Assert.Contains("NutritionApi.DeleteMealAsync(meal.Id)", page);
        // Opening anything closes everything else.
        Assert.Equal(2, Regex.Matches(page, @"var was(Open|Adding) = [^;]+;\s*Close\(\);").Count);
    }

    [Fact]
    public void MealList_IsNewestFirst_AsAChronologicalJournal_NotGroupedByType()
    {
        var page = Page();

        Assert.Contains("meals = MealJournal.NewestFirst(result.Value!);", page);
        Assert.Contains("@MealJournal.Heading(meal)", page);
        Assert.DoesNotContain("GroupBy", page);
        foreach (var section in new[] { ">Breakfast<", ">Lunch<", ">Dinner<" })
        {
            Assert.DoesNotContain(section, page);
        }
    }

    [Fact]
    public void Description_WrapsAndKeepsLineBreaks_NeverTruncated()
    {
        var page = Page();
        var css = File.ReadAllText(Path.Combine(ComponentsRoot(), "Pages", "Nutrition", "Nutrition.razor.css"));

        Assert.Contains("<span class=\"meal-description d-block\">@meal.Description</span>", page);
        Assert.Contains("white-space: pre-line;", css);
        Assert.Contains("overflow-wrap: anywhere;", css);
        Assert.DoesNotContain("ellipsis", css);
        Assert.DoesNotContain("nowrap", css);
    }

    // NUT-002 shows API-provided nutrition on the page; still no food database, no micronutrients, and
    // no client-side totals (the API sums them). The journal helpers themselves stay nutrition-free.
    [Fact]
    public void Page_HasNoFoodDatabaseMicronutrientsOrClientSideTotals()
    {
        // The page title "Food diary" (NUT-003 navigation label) is the only allowed use of "food".
        var page = Page().Replace("<PageHeader Title=\"Food diary\"", "<PageHeader", StringComparison.Ordinal);

        foreach (var word in new[] { "fibre", "fiber", "sugar", "sodium", "vitamin", "ingredient", "recipe", "serving", "portion", "food" })
        {
            Assert.DoesNotContain(word, page, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain(".Sum(", page);
        foreach (var word in new[] { "calorie", "kcal", "protein", "carb", "macro" })
        {
            Assert.DoesNotContain(word, MealJournalSource(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Page_HasEmptyLoadingAndRetryStates_AndClosesFormsWhenTheDayChanges()
    {
        var page = Page();

        Assert.Contains("@MealJournal.EmptyMessage(selectedDay, Today)", page);
        Assert.Contains("Loading meals...", page);
        Assert.Contains(">Retry</button>", page);
        Assert.Matches(new Regex(@"private async Task ChangeDayAsync\(int days\)[\s\S]*?Close\(\);\s*selectedDay = day;\s*meals = \[\];"), page);
        Assert.Contains("aria-label=\"Previous day\"", page);
        Assert.Contains("aria-label=\"Next day\"", page);
    }

    // ---- MealJournal ----

    [Fact]
    public void DayLabel_IsToday_OrTheFullDate()
    {
        Assert.Equal("Today", MealJournal.DayLabel(Today, Today));
        Assert.Equal("2 October 2026", MealJournal.DayLabel(new DateOnly(2026, 10, 2), Today));
        Assert.Equal("29 February 2028", MealJournal.DayLabel(new DateOnly(2028, 2, 29), new DateOnly(2028, 3, 1)));
    }

    [Fact]
    public void EmptyMessage_DependsOnTheDay()
    {
        Assert.Equal("No meals logged today.", MealJournal.EmptyMessage(Today, Today));
        Assert.Equal("No meals logged on this day.", MealJournal.EmptyMessage(Today.AddDays(-1), Today));
    }

    [Fact]
    public void Navigation_StopsAtToday()
    {
        Assert.False(MealJournal.CanGoForward(Today, Today));
        Assert.True(MealJournal.CanGoForward(Today.AddDays(-1), Today));
    }

    [Fact]
    public void Heading_IsTimeAndOptionalType()
    {
        Assert.Equal("13:10 · Lunch", MealJournal.Heading(Meal(new TimeOnly(13, 10), "Lunch")));
        Assert.Equal("08:05", MealJournal.Heading(Meal(new TimeOnly(8, 5), null)));
    }

    [Fact]
    public void DefaultTime_IsNowOnToday_AndMiddayOnOtherDays()
    {
        var now = new DateTime(2026, 10, 3, 13, 10, 42);

        Assert.Equal(new TimeOnly(13, 10), MealJournal.DefaultTime(Today, now));
        Assert.Equal(new TimeOnly(12, 0), MealJournal.DefaultTime(Today.AddDays(-1), now));
    }

    [Fact]
    public void NewestFirst_OrdersByTimeThenId_Descending()
    {
        var a = Meal(new TimeOnly(8, 15), "Breakfast");
        var b = Meal(new TimeOnly(20, 15), "Dinner");
        var c = Meal(new TimeOnly(13, 10), null);
        var d = Meal(new TimeOnly(13, 10), "Lunch");

        var ordered = MealJournal.NewestFirst([a, c, b, d]);

        Assert.Equal(b, ordered[0]);
        Assert.Equal(new[] { c, d }.OrderByDescending(meal => meal.Id), ordered.Skip(1).Take(2));
        Assert.Equal(a, ordered[3]);
    }

    [Fact]
    public void Requests_CarryTheDiaryDay_TheTimeAndTheDeviceOffset_WithEmptyTypeAsNone()
    {
        var rome = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");

        var summer = MealJournal.CreateRequest("Caffè", "", Today, new TimeOnly(10, 30), rome);
        Assert.Equal(new CreateMealRequest("Caffè", null, Today, new TimeOnly(10, 30), 120), summer);

        var winter = MealJournal.CreateRequest("Pasta", "Lunch", new DateOnly(2026, 12, 1), new TimeOnly(13, 0), rome);
        Assert.Equal(60, winter.UtcOffsetMinutes);
        Assert.Equal("Lunch", winter.MealType);

        Assert.Equal(new UpdateMealRequest("Pasta", null, new TimeOnly(9, 0)), MealJournal.UpdateRequest("Pasta", " ", new TimeOnly(9, 0)));
    }

    [Fact]
    public void MealTypes_MatchTheApi()
    {
        Assert.Equal(Enum.GetNames<LifeOS.Domain.Nutrition.MealType>(), MealJournal.MealTypes);
    }

    private static MealResponse Meal(TimeOnly time, string? type) =>
        new(Guid.CreateVersion7(), "x", type, Today, time, default, default, default);

    private static DockItem Item(string label) => DockNavigation.Items.Single(item => item.Label == label);

    private static string Page() => Source("Pages/Nutrition", "Nutrition.razor");

    private static string MealJournalSource() =>
        File.ReadAllText(Path.Combine(ComponentsRoot(), "..", "Services", "Nutrition", "MealJournal.cs"));

    private static string ComponentsRoot([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", "..", "src", "dotnet", "LifeOS.App", "Components"));

    private static string Source(string folder, string fileName) =>
        File.ReadAllText(Path.Combine(ComponentsRoot(), folder, fileName));
}
