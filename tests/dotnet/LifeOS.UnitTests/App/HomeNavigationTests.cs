using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LifeOS.App.Services.Navigation;

namespace LifeOS.UnitTests.App;

// Home and the global navigation (APP-001, refreshed by NAV-001): the dock's items, active rules and
// quick-add choices (plain .NET), and the Home, dock and header composition, which the net10.0 test
// project cannot render. Those checks read the component sources.
public class HomeNavigationTests
{
    // ---- Dock items ----

    [Fact]
    public void Dock_IsHome_Transactions_Plus_Nutrition_More_InThatOrder()
    {
        Assert.Equal(["Home", "Transactions", "Quick add", "Nutrition", "More"], DockNavigation.Items.Select(item => item.Label));
        Assert.Equal(["", "finance/transactions", null, "nutrition", "more"], DockNavigation.Items.Select(item => item.Href));
        Assert.Equal([false, false, true, false, false], DockNavigation.Items.Select(item => item.IsPrimary));
        Assert.Equal(["home", "transactions", "plus", "nutrition", "grid"], DockNavigation.Items.Select(item => item.Icon));
    }

    [Fact]
    public void Dock_HasNoPortfolioOrSettings()
    {
        Assert.DoesNotContain(DockNavigation.Items, item => item.Label is "Portfolio" or "Settings");
        Assert.DoesNotContain(DockNavigation.Items, item => item.Href is "portfolio" or "settings");
    }

    // ---- Active rules: at most one destination, "+" never ----

    [Theory]
    [InlineData("", "Home")]
    [InlineData("finance/transactions", "Transactions")]
    [InlineData("finance/transactions/0198c0de-0000-7000-8000-000000000001", "Transactions")]
    [InlineData("finance/transactions/0198c0de-0000-7000-8000-000000000001/edit", "Transactions")]
    [InlineData("finance/transactions/new", null)]
    [InlineData("nutrition", "Nutrition")]
    [InlineData("nutrition/hub", "More")]
    [InlineData("nutrition/targets", "More")]
    [InlineData("nutrition/targets/0198c0de-0000-7000-8000-000000000001", "More")]
    [InlineData("more", "More")]
    [InlineData("finance", "More")]
    [InlineData("finance/accounts", "More")]
    [InlineData("finance/categories", "More")]
    [InlineData("finance/analytics", "More")]
    [InlineData("finance/planned-expenses", "More")]
    [InlineData("finance/recurring", "More")]
    [InlineData("gym", "More")]
    [InlineData("gym/train", "More")]
    [InlineData("gym/programs/0198c0de-0000-7000-8000-000000000001", "More")]
    [InlineData("gym/sessions/0198c0de-0000-7000-8000-000000000001", "More")]
    [InlineData("settings", null)]
    [InlineData("portfolio", null)]
    public void ExactlyTheExpectedDestination_IsActive(string path, string? expected)
    {
        var active = DockNavigation.Items.Where(item => item.IsActive(path)).Select(item => item.Label).ToArray();

        Assert.Equal(expected is null ? [] : new[] { expected }, active);
    }

    [Theory]
    [InlineData("/", "Home")]
    [InlineData("/Finance/Transactions/", "Transactions")]
    [InlineData("finance/transactions?tab=planned", "Transactions")]
    [InlineData("nutrition?add=meal", "Nutrition")]
    [InlineData("/Nutrition/#top", "Nutrition")]
    [InlineData("nutrition/targets?from=hub", "More")]
    [InlineData("More#top", "More")]
    public void ActiveRules_ApplyToTheNormalizedPath(string rawPath, string expected)
    {
        var path = DockNavigation.Normalize(rawPath);

        Assert.Equal([expected], DockNavigation.Items.Where(item => item.IsActive(path)).Select(item => item.Label));
    }

    [Theory]
    [InlineData("")]
    [InlineData("finance/transactions")]
    [InlineData("finance/transactions/new")]
    [InlineData("nutrition")]
    [InlineData("more")]
    [InlineData("settings")]
    public void QuickAdd_IsNeverActive(string path)
    {
        Assert.False(DockNavigation.Items.Single(item => item.IsPrimary).IsActive(path));
    }

    [Theory]
    [InlineData("finance/transactionsx", "Transactions")]
    [InlineData("nutritionx", "Nutrition")]
    [InlineData("morex", "More")]
    [InlineData("gymnastics", "More")]
    public void Sections_MatchWholeSegmentsOnly(string path, string label)
    {
        Assert.False(Item(label).IsActive(path));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("/finance/transactions/", "finance/transactions")]
    [InlineData("finance/transactions?tab=planned", "finance/transactions")]
    [InlineData("More#top", "more")]
    public void Normalize_DropsQueryFragmentSlashesAndCase(string path, string expected)
    {
        Assert.Equal(expected, DockNavigation.Normalize(path));
    }

    // ---- Quick add ----

    [Fact]
    public void QuickAdd_OffersTransactionAndMeal_ThroughTheExistingFlows()
    {
        Assert.Equal(["Transaction", "Meal"], DockNavigation.QuickAddActions.Select(action => action.Label));
        Assert.Equal(["Add transaction", "Add meal"], DockNavigation.QuickAddActions.Select(action => action.AccessibleLabel));
        Assert.Equal("finance/transactions/new", DockNavigation.QuickAddActions[0].Href);
        Assert.Equal("nutrition?add=meal", DockNavigation.QuickAddActions[1].Href);
        Assert.DoesNotContain(DockNavigation.QuickAddActions, action => action.Href.StartsWith("gym", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("meal", true)]
    [InlineData("Meal", true)]
    [InlineData("transaction", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAddMeal_RecognizesOnlyTheMealQuery(string? value, bool expected)
    {
        Assert.Equal(expected, DockNavigation.IsAddMeal(value));
    }

    [Fact]
    public void FoodDiary_OpensItsOwnAddForm_FromTheQuickAddQuery_AndDropsIt()
    {
        var page = Source("Pages/Nutrition", "Nutrition.razor");

        Assert.Contains("[SupplyParameterFromQuery(Name = DockNavigation.AddQueryName)]", page);
        Assert.Contains("DockNavigation.IsAddMeal(AddQuery)", page);
        Assert.Contains("Navigation.NavigateTo(\"nutrition\", replace: true);", page);
        // The quick add and "+ Add meal" open the same form: no second meal-entry form.
        Assert.Equal(2, Count(page, "OpenAdd();"));
        Assert.Equal(1, Count(page, "@MealForm(\"Add meal\""));
    }

    // ---- Dock markup ----

    [Fact]
    public void DockMarkup_IsLabelled_AndMarksTheActivePage()
    {
        var dock = Source("Layout", "BottomDock.razor");

        Assert.Contains("<nav class=\"lo-dock\" aria-label=\"Main\"", dock);
        Assert.Contains("DockNavigation.Items", dock);
        Assert.Contains("aria-current=\"@(active ? \"page\" : null)\"", dock);
        Assert.Contains("grid-template-columns: 1fr 1fr 3.75rem 1fr 1fr;", Source("Layout", "BottomDock.razor.css"));
    }

    [Fact]
    public void DockPlus_IsAButtonThatTogglesTheQuickAddPanel_NotALink()
    {
        var dock = Source("Layout", "BottomDock.razor");

        Assert.Matches(new Regex(@"<button type=""button"" class=""lo-dock__primary"" aria-label=""@item.Label"" aria-controls=""quick-add""\s+aria-expanded=""@\(quickAddOpen \? ""true"" : ""false""\)"" @onclick=""ToggleQuickAdd"">"), dock);
        Assert.DoesNotContain("class=\"lo-dock__primary\" href", dock);
        Assert.DoesNotContain("NewTransactionHref", dock);
    }

    [Fact]
    public void QuickAddPanel_ListsTheChoicesAsLinks_AndClosesOnChoiceBackdropEscapeAndNavigation()
    {
        var dock = Source("Layout", "BottomDock.razor");

        Assert.Contains("@foreach (var action in DockNavigation.QuickAddActions)", dock);
        Assert.Contains("<a href=\"@action.Href\" class=\"lo-dock__quick-action\" aria-label=\"@action.AccessibleLabel\" @onclick=\"CloseQuickAdd\">", dock);
        Assert.Contains("<div class=\"lo-dock__backdrop\" aria-hidden=\"true\" @onclick=\"CloseQuickAdd\"></div>", dock);
        Assert.Contains("args.Key == \"Escape\"", dock);
        Assert.Matches(new Regex(@"OnLocationChanged\([^)]*\)\s*\{[^}]*quickAddOpen = false;"), dock);
        // The dock holds no Nutrition or Finance logic: only the shared navigation model.
        Assert.DoesNotContain("NutritionApi", dock);
        Assert.DoesNotContain("@inject NutritionApiClient", dock);
    }

    [Fact]
    public void DockIcons_ExistInAppIcon()
    {
        var icon = Source("Shared", "AppIcon.razor");

        foreach (var name in DockNavigation.Items.Select(item => item.Icon)
                     .Concat(DockNavigation.QuickAddActions.Select(action => action.Icon))
                     .Append("close"))
        {
            Assert.Contains($"case \"{name}\":", icon);
        }
    }

    [Fact]
    public void HeaderWordmark_LinksHome_AndSettingsIsUnchanged()
    {
        var header = Source("Layout", "AppHeader.razor");

        Assert.Contains("<a href=\"\" class=\"lo-wordmark\" aria-label=\"LifeOS home\">LifeOS</a>", header);
        Assert.Contains("<a href=\"settings\" class=\"lo-icon-btn\" aria-label=\"Settings\">", header);
    }

    // ---- Home ----

    // NUT-002 added the Nutrition card after Finance and Training.
    [Fact]
    public void Home_HasTheFinanceTrainingAndNutritionSections()
    {
        var home = Source("Pages", "Home.razor");

        Assert.Contains("<MonthlyBudgetCard", home);
        Assert.Contains("<HomeTrainingCard />", home);
        Assert.Contains("<HomeNutritionCard />", home);
        Assert.True(home.IndexOf("<MonthlyBudgetCard", StringComparison.Ordinal) < home.IndexOf("<HomeTrainingCard />", StringComparison.Ordinal));
        Assert.True(home.IndexOf("<HomeTrainingCard />", StringComparison.Ordinal) < home.IndexOf("<HomeNutritionCard />", StringComparison.Ordinal));
        Assert.DoesNotContain("PortfolioCard", home);
        Assert.DoesNotContain("Recent transactions", home);
        Assert.DoesNotContain("TransactionRow", home);
        Assert.DoesNotContain("AccountsApi", home);
        Assert.DoesNotContain("TransactionsApi", home);
        Assert.Equal(3, Count(home, "<section class=\"lo-section\">"));
    }

    [Fact]
    public void HomeFinance_IsTheModuleHeading_WithOpenToTheFinanceHub()
    {
        var home = Source("Pages", "Home.razor");

        Assert.Matches(new Regex(@"<section class=""lo-section"">\s*<div class=""lo-section-header"">\s*<h2 class=""lo-section-title"">Finance</h2>\s*<a href=""finance"" class=""lo-header-action"" aria-label=""Open Finance"">Open</a>\s*</div>\s*<MonthlyBudgetCard"), home);
        Assert.DoesNotContain("<h2 class=\"lo-section-title\">Monthly budget</h2>", home);
        Assert.DoesNotContain("<h2 class=\"lo-section-title\">Monthly budget</h2>", Source("Finance", "MonthlyBudgetCard.razor"));
    }

    [Fact]
    public void HomeFinance_KeepsMonthlyBudgetAsASubheading_WithEdit()
    {
        var card = Source("Finance", "MonthlyBudgetCard.razor");

        var cardStart = card.IndexOf("<div class=\"lo-card p-3\">", StringComparison.Ordinal);
        var subheading = card.IndexOf("<h3 class=\"lo-budget-title\">Monthly budget</h3>", StringComparison.Ordinal);
        Assert.True(cardStart >= 0 && cardStart < subheading);
        Assert.Contains("aria-label=\"Edit monthly budget\" @onclick=\"Edit\">Edit</button>", card);
    }

    [Fact]
    public void HomeFinance_KeepsPortfolioAndAnalyticsShortcuts_InEveryState()
    {
        var home = Source("Pages", "Home.razor");
        var card = Source("Finance", "MonthlyBudgetCard.razor");

        Assert.Contains("<a href=\"portfolio\" class=\"btn btn-outline-secondary lo-home-action\">", home);
        Assert.Contains("<a href=\"finance/analytics\" class=\"btn btn-outline-secondary lo-home-action\">", home);
        Assert.Contains("<Actions>", home);
        // Rendered after the state branches (loading, error, editing, no budget, budget), not inside one.
        Assert.Matches(new Regex(@"\}\s*@if \(Actions is not null\)\s*\{\s*<div class=""lo-budget-actions"">@Actions</div>"), card);
    }

    [Fact]
    public void HomeFinance_FreeToSpendLeads_ThenSpentOfBudget_Expected_AndSafePerDay()
    {
        var card = Source("Finance", "MonthlyBudgetCard.razor");

        var month = card.IndexOf("@LocalMonth.Label(month) · @budget.Currency", StringComparison.Ordinal);
        var free = card.IndexOf("<div class=\"lo-budget-free__amount", StringComparison.Ordinal);
        var spent = card.IndexOf("@Money(budget.Spent) spent of @Money(budget.Amount)", StringComparison.Ordinal);
        var expected = card.IndexOf("@Money(budget.ExpectedExpensesTotal) expected", StringComparison.Ordinal);
        var safe = card.IndexOf("@Money(safe)/day", StringComparison.Ordinal);

        Assert.True(month >= 0 && month < free && free < spent && spent < expected && expected < safe);
        Assert.Contains(">@Money(budget.FreeToSpend)</div>", card);
        Assert.Contains("<div class=\"lo-muted\">free to spend</div>", card);
        Assert.Contains("font-size: 1.75rem;", Source("Finance", "MonthlyBudgetCard.razor.css"));
        Assert.Equal(1, Count(card, "@Money(budget.FreeToSpend)"));
    }

    [Fact]
    public void HomeFinance_KeepsSetEditRemove_AndTheBudgetApi()
    {
        var card = Source("Finance", "MonthlyBudgetCard.razor");

        Assert.Contains("BudgetsApi.GetAsync(month, Currency)", card);
        Assert.Contains("BudgetsApi.SetAsync(month, Currency!, amount)", card);
        Assert.Contains("BudgetsApi.DeleteAsync(month, Currency!)", card);
        Assert.Contains("<span class=\"lo-muted\">No @LocalMonth.Label(month) budget.</span>", card);
        Assert.Contains("<button class=\"btn btn-sm btn-outline-primary\" @onclick=\"Edit\">Set budget</button>", card);
        Assert.Contains("\"Enter a budget amount greater than zero.\"", card);
        Assert.Contains("<p>Remove this month's budget?</p>", card);
        Assert.Contains("@onclick=\"RemoveAsync\" disabled=\"@saving\">Remove budget</button>", card);
        Assert.Contains(">Keep budget</button>", card);
        Assert.Contains("Loading budget...", card);
        Assert.Contains("@onclick=\"LoadAsync\">Retry</button>", card);
    }

    [Fact]
    public void HomeTrainingAndNutrition_KeepTheirOwnSections()
    {
        Assert.Contains("<h2 class=\"lo-section-title\">Training</h2>", Source("Gym", "HomeTrainingCard.razor"));
        Assert.Contains("<h2 class=\"lo-section-title\">Nutrition</h2>", Source("Nutrition", "HomeNutritionCard.razor"));
        Assert.Matches(new Regex(@"<section class=""lo-section"">\s*<HomeTrainingCard />\s*</section>"), Source("Pages", "Home.razor"));
        Assert.Matches(new Regex(@"<section class=""lo-section"">\s*<HomeNutritionCard />\s*</section>"), Source("Pages", "Home.razor"));
    }

    // ---- More stays the module directory ----

    [Fact]
    public void More_StillListsFinanceGymAndTheNutritionHub()
    {
        var more = Source("Pages", "More.razor");

        Assert.Contains("new(\"Finance\", \"Transactions, accounts and categories\", \"finance\", \"finance\")", more);
        Assert.Contains("new(\"Gym\", \"Train and manage workout programs\", \"gym\", \"gym\")", more);
        Assert.Contains("new(\"Nutrition\", \"Food diary and targets\", \"nutrition\", \"nutrition/hub\")", more);
    }

    private static DockItem Item(string label) => DockNavigation.Items.Single(item => item.Label == label);

    private static int Count(string source, string value) =>
        (source.Length - source.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;

    private static string Source(string folder, string fileName, [CallerFilePath] string testFile = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!, "..", "..", "..", "..", "src", "dotnet", "LifeOS.App", "Components", folder, fileName)));
}
