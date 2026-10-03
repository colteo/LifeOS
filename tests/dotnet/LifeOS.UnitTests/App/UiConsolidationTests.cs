using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace LifeOS.UnitTests.App;

// The consolidated mobile layout (UI-001): the shared title row (title left, < Back or one action
// right), the pages that use it, Portfolio's inline account management, and the removed helper copy.
// The net10.0 test project cannot render Razor, so these checks read the component sources.
public class UiConsolidationTests
{
    // ---- Shared header ----

    [Fact]
    public void PageHeader_IsTitleLeft_ThenBackOrOneActionRight()
    {
        var header = Source("Shared", "PageHeader.razor");

        var title = header.IndexOf("<h1 class=\"lo-page-title\">@Title</h1>", StringComparison.Ordinal);
        var back = header.IndexOf("<a href=\"@BackHref\" class=\"lo-header-action\">", StringComparison.Ordinal);
        var action = header.IndexOf("@Action", StringComparison.Ordinal);

        // The back link no longer renders above the title, as a breadcrumb.
        Assert.True(title >= 0 && title < back && back < action);
        Assert.Matches(new Regex(@"<AppIcon Name=""chevron-left""[^>]*/>\s*Back\s*</a>"), header);
        Assert.Contains("public RenderFragment? Action { get; set; }", header);
        Assert.DoesNotContain("BackLabel", header);
        Assert.DoesNotContain("lo-back", header);
    }

    [Fact]
    public void NoPage_NamesItsParentInTheBackLink()
    {
        foreach (var page in Directory.EnumerateFiles(ComponentsRoot(), "*.razor", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("BackLabel", File.ReadAllText(page));
        }
    }

    [Fact]
    public void TitleRowAction_IsACompactSharedClass_AtLeast40px()
    {
        var css = AppCss();

        Assert.Contains(".lo-header-action {", css);
        Assert.Contains("min-height: 2.5rem;", Rule(css, ".lo-header-action"));
        Assert.Contains("justify-content: space-between;", Rule(css, ".lo-page-header"));
        Assert.Contains("justify-content: space-between;", Rule(css, ".lo-section-header"));
    }

    [Fact]
    public void SettingsGear_IsACog_NotASun()
    {
        var icon = Source("Shared", "AppIcon.razor");
        var gear = icon[icon.IndexOf("case \"gear\":", StringComparison.Ordinal)..icon.IndexOf("case \"chevron-left\":", StringComparison.Ordinal)];

        // The old drawing: two circles and eight separate rays.
        Assert.DoesNotContain("M12 2.6v2.8", gear);
        Assert.Contains("<circle cx=\"12\" cy=\"12\" r=\"3.2\" />", gear);
        Assert.Matches(new Regex(@"<path d=""M[\d. L]+Z"" />"), gear);
        Assert.Contains("<a href=\"settings\" class=\"lo-icon-btn\" aria-label=\"Settings\">", Source("Layout", "AppHeader.razor"));
    }

    // ---- Home ----

    [Fact]
    public void HomeTraining_ProgramAndCycleShareARow_AsDoWorkoutAndSummary()
    {
        var card = Source("Gym", "HomeTrainingCard.razor");

        Assert.Matches(new Regex(@"<div class=""lo-home-training__line"">\s*<span class=""lo-home-training__program"">@active.ProgramName</span>\s*<span class=""lo-muted small"">@TrainingDisplay.Cycle\(active\)</span>"), card);
        Assert.Matches(new Regex(@"<div class=""lo-home-training__line"">\s*<span class=""lo-home-training__workout"">@next.Name</span>\s*<span class=""lo-muted small"">@TrainingDisplay.Summary\(next\)</span>"), card);
        Assert.Contains("Next workout", card);
        Assert.Contains("@(starting ? \"Starting...\" : \"Start workout\")", card);
        Assert.Contains("\"Other workouts\"", card);
        Assert.Contains("flex-wrap: wrap;", Source("Gym", "HomeTrainingCard.razor.css"));
    }

    [Fact]
    public void MonthlyBudget_EditIsTheSectionsTitleRowAction()
    {
        var card = Source("Finance", "MonthlyBudgetCard.razor");

        Assert.Matches(new Regex(@"<h2 class=""lo-section-title"">Monthly budget</h2>\s*@if \(budget is not null && !editing\)\s*\{\s*<button type=""button"" class=""lo-header-action"""), card);
    }

    // ---- Transactions ----

    [Fact]
    public void Transactions_IsTitleWithBack_WithoutRedundantHelperCopy()
    {
        var page = Source("Pages/Finance", "Transactions.razor");

        Assert.Contains("<PageHeader Title=\"Transactions\" BackHref=\"finance\" />", page);
        foreach (var copy in new[]
        {
            "Real transactions only",
            "Recorded transactions for",
            "Expected movements for",
            "Repeats monthly.",
            "Happens once.",
            "lo-plan-hint"
        })
        {
            Assert.DoesNotContain(copy, page);
        }
    }

    [Fact]
    public void Transactions_KeepsFin006_MonthTabsDueCountAndPlannedActions()
    {
        var page = Source("Pages/Finance", "Transactions.razor");

        Assert.Contains("aria-label=\"Previous month\"", page);
        Assert.Contains("aria-label=\"Next month\"", page);
        Assert.Contains("@PlannedMonthView.PlannedTabLabel(isHistoryLoading ? 0 : planned.DueCount)", page);
        Assert.Contains("<RecurringOccurrenceActions Occurrence=\"occurrence\"", page);
        Assert.Contains("<PlannedExpenseActions Occurrence=\"item\"", page);
        Assert.Contains("id=\"planned-recurring-title\">Recurring</h3>", page);
        Assert.Contains("id=\"planned-oneoff-title\">One-off</h3>", page);
        Assert.Contains("TransactionMonthLoader.LoadAsync(month, TimeZoneInfo.Local, TransactionsApi, RecurringApi)", page);
    }

    // ---- Portfolio ----

    [Fact]
    public void Portfolio_AnalyticsIsTheTitleRowAction_AndTheBottomCardIsGone()
    {
        var page = Source("Pages", "Portfolio.razor");

        Assert.Matches(new Regex(@"<PageHeader Title=""Portfolio"">\s*<Action>\s*<a href=""finance/analytics"" class=""lo-header-action"">"), page);
        Assert.Single(Regex.Matches(page, "finance/analytics"));
        Assert.DoesNotContain("Monthly spending and income", page);
        Assert.DoesNotContain("lo-row__chevron", page);
    }

    [Fact]
    public void Portfolio_ManagesAccountsDirectly_WithoutAManageAccountsLink()
    {
        var page = Source("Pages", "Portfolio.razor");

        Assert.DoesNotContain("Manage accounts", page);
        Assert.DoesNotContain("href=\"finance/accounts\"", page);
        Assert.Contains("<AccountsManager @ref=\"accounts\" HideAmounts=\"Privacy.IsHidden\" ReportBalanceErrors=\"false\" BalancesLoaded=\"OnBalancesLoaded\" />", page);
        // The totals come from the same balances request as the rows.
        Assert.DoesNotContain("AccountsApi", page);
        Assert.Contains("PortfolioSummary.Build(balances, Auth.CurrentUser?.DefaultCurrency)", page);
    }

    [Fact]
    public void AccountsManager_AddAccountIsTheSectionAction_AndOpensInline()
    {
        var manager = Source("Finance", "AccountsManager.razor");

        Assert.Matches(new Regex(@"<h2 class=""lo-section-title"">@Heading</h2>\s*<button type=""button"" class=""lo-header-action"" aria-expanded="), manager);
        Assert.Contains("aria-controls=\"add-account-form\"", manager);
        Assert.Contains("<section id=\"add-account-form\"", manager);
        Assert.Contains("<CurrentBalanceFields Input=\"currentBalance\" AccountType=\"@form.Type\" IdPrefix=\"account\" />", manager);
        Assert.Contains("AccountsApi.CreateAccountAsync(", manager);
    }

    [Fact]
    public void AccountsManager_EveryAccountActionOpensInlineUnderItsRow_OneAtATime()
    {
        var manager = Source("Finance", "AccountsManager.razor");

        Assert.Contains("aria-label=\"Actions for @account.Name\"", manager);
        Assert.Contains("aria-expanded=\"@(openAccountId == account.Id ? \"true\" : \"false\")\"", manager);
        Assert.Contains("@ActionPanel(account, balanceEntry)", manager);
        Assert.Contains("openAccountId = openAccountId == accountId ? null : accountId;", manager);
        foreach (var call in new[]
        {
            "AccountsApi.UpdateAccountAsync(",
            "AccountsApi.SetOpeningBalanceAsync(",
            "AccountsApi.ReconcileAsync(",
            "AccountsApi.DeleteAccountAsync("
        })
        {
            Assert.Contains(call, manager);
        }

        // "Add current balance" only when the account is known to have no opening balance.
        Assert.Contains("balanceEntry is { OpeningBalance: null }", manager);
        Assert.Contains(">Edit</button>", manager);
        Assert.Contains(">Add current balance</button>", manager);
        Assert.Contains(">Reconcile balance</button>", manager);
        Assert.Contains(">Delete</button>", manager);
        Assert.DoesNotContain("modal", manager, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AccountRow_PutsTheMenuOnTheFarRight_OfTheSameRow()
    {
        var row = Source("Finance", "AccountRow.razor");

        var balance = row.IndexOf("<div class=\"lo-row__end\">", StringComparison.Ordinal);
        var actions = row.IndexOf("@Actions", StringComparison.Ordinal);
        var rowEnd = row.IndexOf("@ChildContent", StringComparison.Ordinal);
        Assert.True(balance >= 0 && balance < actions && actions < rowEnd);
    }

    [Fact]
    public void AccountsRoute_StillWorks_WithTheSameManagement()
    {
        var page = Source("Pages/Finance", "Accounts.razor");

        Assert.Contains("@page \"/finance/accounts\"", page);
        Assert.Contains("<PageHeader Title=\"Accounts\" BackHref=\"finance\" />", page);
        Assert.Contains("<AccountsManager", page);
    }

    // ---- Categories ----

    [Fact]
    public void Categories_IsTitleWithBack_TypeSwitch_ThenSectionTitleWithNewCategory()
    {
        var page = Source("Pages/Finance", "Categories.razor");

        Assert.Contains("<PageHeader Title=\"Categories\" BackHref=\"finance\" />", page);
        var segmented = page.IndexOf("aria-label=\"Category type\"", StringComparison.Ordinal);
        var section = page.IndexOf("<h2 class=\"lo-section-title\">@SectionTitle</h2>", StringComparison.Ordinal);
        Assert.True(segmented >= 0 && segmented < section);
        Assert.Contains("$\"{selectedType} categories\"", page);
        Assert.Matches(new Regex(@"<h2 class=""lo-section-title"">@SectionTitle</h2>\s*<button type=""button"" class=""lo-header-action"""), page);
        Assert.Contains("aria-controls=\"new-category-form\"", page);
        // The old full-width button under the tree is gone.
        Assert.DoesNotContain("btn btn-outline-secondary w-100 mt-3", page);
    }

    [Fact]
    public void Categories_KeepsCreateSubcategoryRenameDelete_Inline()
    {
        var page = Source("Pages/Finance", "Categories.razor");

        Assert.Contains("aria-label=\"Add subcategory to @parent.Name\"", page);
        Assert.Contains("aria-label=\"Actions for @category.Name\"", page);
        Assert.Contains("aria-expanded=\"@(IsActionOpen(category.Id) ? \"true\" : \"false\")\"", page);
        Assert.Contains("CategoriesApi.CreateCategoryAsync(new CreateCategoryRequest(name, form.Type, form.Parent?.Id))", page);
        Assert.Contains("CategoriesApi.RenameCategoryAsync(category.Id, name)", page);
        Assert.Contains("CategoriesApi.DeleteCategoryAsync(category.Id)", page);
        Assert.Contains("@CreateForm($\"New subcategory of {parent.Name}\")", page);
        Assert.Contains("@CreateForm(\"New category\")", page);
        Assert.DoesNotContain("modal", page, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Helpers ----

    // The body of the first CSS rule with exactly this selector.
    private static string Rule(string css, string selector)
    {
        var start = css.IndexOf(selector + " {", StringComparison.Ordinal);
        Assert.True(start >= 0, selector);

        return css[start..css.IndexOf('}', start)];
    }

    private static string AppCss([CallerFilePath] string testFile = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!, "..", "..", "..", "..", "src", "dotnet", "LifeOS.App", "wwwroot", "app.css")));

    private static string ComponentsRoot([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", "..", "src", "dotnet", "LifeOS.App", "Components"));

    private static string Source(string folder, string fileName) =>
        File.ReadAllText(Path.Combine(ComponentsRoot(), folder, fileName));
}
