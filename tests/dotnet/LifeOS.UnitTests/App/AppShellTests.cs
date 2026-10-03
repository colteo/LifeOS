using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LifeOS.App.Services.Gym;
using LifeOS.App.Services.Navigation;
using LifeOS.Contracts.Gym.History;

namespace LifeOS.UnitTests.App;

// APP-001: the cross-module shell. The dock's active rules are plain .NET (DockNavigation); the dock,
// module hubs and Home are Razor, which the net10.0 test project cannot render, so those checks read
// the component sources.
public class AppShellTests
{
    // ---- Dock ----

    [Theory]
    [InlineData("", DockSection.Home)]
    [InlineData("finance", DockSection.Finance)]
    [InlineData("finance/transactions", DockSection.Finance)]
    [InlineData("finance/transactions/0193a000-0000-7000-8000-000000000001", DockSection.Finance)]
    [InlineData("finance/transactions/0193a000-0000-7000-8000-000000000001/edit", DockSection.Finance)]
    [InlineData("finance/accounts", DockSection.Finance)]
    [InlineData("finance/categories", DockSection.Finance)]
    [InlineData("finance/planned-expenses", DockSection.Finance)]
    [InlineData("finance/recurring", DockSection.Finance)]
    [InlineData("finance/analytics", DockSection.Finance)]
    [InlineData("portfolio", DockSection.Finance)]
    [InlineData("gym", DockSection.Gym)]
    [InlineData("gym/train", DockSection.Gym)]
    [InlineData("gym/history", DockSection.Gym)]
    [InlineData("gym/history/0193a000-0000-7000-8000-000000000001", DockSection.Gym)]
    [InlineData("gym/programs", DockSection.Gym)]
    [InlineData("gym/sessions/0193a000-0000-7000-8000-000000000001", DockSection.Gym)]
    [InlineData("more", DockSection.More)]
    [InlineData("settings", DockSection.None)]
    [InlineData("not-a-page", DockSection.None)]
    public void ActiveSection_FollowsModules_NotScreens(string path, DockSection expected) =>
        Assert.Equal(expected, DockNavigation.ActiveSection(path));

    [Fact]
    public void NewTransaction_IsAnAction_AndSelectsNoSection() =>
        Assert.Equal(DockSection.None, DockNavigation.ActiveSection(DockNavigation.NewTransactionHref));

    [Fact]
    public void ActiveSection_IgnoresPrefixLookalikes()
    {
        Assert.Equal(DockSection.None, DockNavigation.ActiveSection("financeplus"));
        Assert.Equal(DockSection.None, DockNavigation.ActiveSection("gymnastics"));
        Assert.Equal(DockSection.None, DockNavigation.ActiveSection("portfolios"));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("Finance/Accounts/", "finance/accounts")]
    [InlineData("finance/transactions?month=2026-10#top", "finance/transactions")]
    [InlineData("gym#history", "gym")]
    [InlineData("?q=1", "")]
    public void Normalize_DropsQueryFragmentSlashesAndCase(string relative, string expected) =>
        Assert.Equal(expected, DockNavigation.Normalize(relative));

    [Fact]
    public void Dock_LinksTheModules()
    {
        Assert.Equal("", DockNavigation.HomeHref);
        Assert.Equal("finance", DockNavigation.FinanceHref);
        Assert.Equal("finance/transactions/new", DockNavigation.NewTransactionHref);
        Assert.Equal("gym", DockNavigation.GymHref);
        Assert.Equal("more", DockNavigation.MoreHref);
    }

    [Fact]
    public void Dock_IsHomeFinancePlusGymMore_WithAnUnlabelledAccessiblePlus()
    {
        var dock = Source("Layout", "BottomDock.razor");

        var positions = new[]
        {
            "new(\"Home\", \"home\", DockNavigation.HomeHref, DockSection.Home)",
            "new(\"Finance\", \"finance\", DockNavigation.FinanceHref, DockSection.Finance)",
            "new(\"New transaction\", \"plus\", DockNavigation.NewTransactionHref, DockSection.None, IsPrimary: true)",
            "new(\"Gym\", \"gym\", DockNavigation.GymHref, DockSection.Gym)",
            "new(\"More\", \"grid\", DockNavigation.MoreHref, DockSection.More)"
        }.Select(item => dock.IndexOf(item, StringComparison.Ordinal)).ToList();

        Assert.All(positions, position => Assert.True(position >= 0));
        Assert.Equal(positions.Order(), positions);
        Assert.DoesNotContain("Portfolio", dock);
        Assert.DoesNotContain("\"Transactions\"", dock);

        // + carries its name as aria-label and no visible label; only sections get aria-current.
        Assert.Contains("class=\"lo-dock__primary\" aria-label=\"@item.Label\"", dock);
        Assert.Contains("aria-current=\"@(active ? \"page\" : null)\"", dock);
        Assert.Contains("<span class=\"lo-dock__label\">@item.Label</span>", dock);
    }

    // ---- Module hubs ----

    [Fact]
    public void FinanceHub_IsAModuleRoot_WithoutBackToMore_AndListsItsScreens()
    {
        var hub = Source("Pages", "Finance", "FinanceHub.razor");

        Assert.Contains("<PageHeader Title=\"Finance\" />", hub);
        Assert.DoesNotContain("BackHref", hub);
        foreach (var href in new[] { "\"portfolio\"", "\"finance/transactions\"", "\"finance/planned-expenses\"", "\"finance/recurring\"", "\"finance/accounts\"", "\"finance/categories\"", "\"finance/analytics\"" })
        {
            Assert.Contains(href, hub);
        }
    }

    [Fact]
    public void GymHub_IsAModuleRoot_WithoutBackToMore()
    {
        var hub = Source("Pages", "Gym", "GymHub.razor");

        Assert.Contains("<PageHeader Title=\"Gym\" />", hub);
        Assert.DoesNotContain("BackHref", hub);
    }

    [Theory]
    [InlineData("Finance", "Accounts.razor")]
    [InlineData("Finance", "Categories.razor")]
    [InlineData("Finance", "PlannedExpenses.razor")]
    [InlineData("Finance", "Recurring.razor")]
    [InlineData("Finance", "Analytics.razor")]
    [InlineData("Finance", "Transactions.razor")]
    [InlineData("", "Portfolio.razor")]
    public void FinanceScreens_LinkBackToFinance(string folder, string page) =>
        Assert.Contains("BackHref=\"finance\" BackLabel=\"Finance\"", Source("Pages", folder, page));

    [Theory]
    [InlineData("Train.razor")]
    [InlineData("History.razor")]
    [InlineData("Programs.razor")]
    public void GymScreens_LinkBackToGym(string page) =>
        Assert.Contains("BackHref=\"gym\" BackLabel=\"Gym\"", Source("Pages", "Gym", page));

    [Fact]
    public void NoPage_LinksBackToMore()
    {
        var pages = Path.Combine(AppRoot(), "Components", "Pages");

        foreach (var file in Directory.EnumerateFiles(pages, "*.razor", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("BackHref=\"more\"", File.ReadAllText(file));
        }
    }

    [Fact]
    public void More_StaysTheModuleDirectory_AndSettingsStaysInTheHeader()
    {
        var more = Source("Pages", "More.razor");

        Assert.Contains("@page \"/more\"", more);
        Assert.Contains("new(\"Finance\"", more);
        Assert.Contains("new(\"Gym\"", more);
        Assert.DoesNotContain("href=\"settings\"", more);
        Assert.DoesNotContain("\"settings\")", more);
        Assert.Contains("href=\"settings\"", Source("Layout", "AppHeader.razor"));
    }

    // ---- Home ----

    [Fact]
    public void Home_ComposesIndependentModuleSections_TodayFinanceRecent()
    {
        var home = Source("Pages", "Home.razor");

        var gym = home.IndexOf("<GymHomeSummary />", StringComparison.Ordinal);
        var finance = home.IndexOf("<FinanceHomeSummary />", StringComparison.Ordinal);
        var recent = home.IndexOf("<RecentTransactionsSummary />", StringComparison.Ordinal);
        Assert.True(gym >= 0 && gym < finance && finance < recent);

        // Home itself loads nothing: no shared, all-or-nothing request state.
        Assert.DoesNotContain("ApiClient", home);
        Assert.DoesNotContain("WhenAll", home);
        Assert.Contains("Title=\"Today\"", home);
        Assert.Contains("Title=\"Finance\" Href=\"finance\"", home);
        Assert.Contains("Href=\"finance/transactions\" LinkLabel=\"View all\"", home);
    }

    [Fact]
    public void GymOnHome_ResumesAWorkoutInProgress_OrOffersTrain()
    {
        var gym = Source("Home", "GymHomeSummary.razor");

        Assert.Contains("SessionsApi.GetCurrentAsync()", gym);
        Assert.Contains("<WorkoutInProgressCard Session=\"current\" />", gym);
        Assert.Contains(">Resume<", Source("Gym", "WorkoutInProgressCard.razor"));
        Assert.Contains("href=\"gym/train\"", gym);
        Assert.Contains(">Train<", gym);

        // History is read once, only when nothing is in progress; no schedule semantics (GYM-004).
        Assert.Contains("if (result.IsSuccess && current is null)", gym);
        Assert.Single(Regex.Matches(gym, "GetHistoryAsync"));
        foreach (var invented in new[] { "Next workout", "Scheduled", "streak", "adherence" })
        {
            Assert.DoesNotContain(invented, gym, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void GymOnHome_FailsOnItsOwn_WithRetry()
    {
        var gym = Source("Home", "GymHomeSummary.razor");

        Assert.Contains("Unable to load your workout.", gym);
        Assert.Contains("@onclick=\"LoadAsync\"", gym);
        Assert.DoesNotContain("AccountsApi", gym);
        Assert.DoesNotContain("TransactionsApi", gym);
    }

    [Fact]
    public void FinanceOnHome_KeepsPortfolioBudgetAndRecent_AndFailsOnItsOwn()
    {
        var finance = Source("Home", "FinanceHomeSummary.razor");
        var recent = Source("Home", "RecentTransactionsSummary.razor");

        Assert.Contains("PortfolioSummary.Build(balances.Value!, Auth.CurrentUser?.DefaultCurrency)", finance);
        Assert.Contains("Href=\"portfolio\"", finance);
        Assert.Contains("<MonthlyBudgetCard Currency=\"@(Auth.CurrentUser?.DefaultCurrency)\" />", finance);
        Assert.Contains("TransactionsApi.GetRecentTransactionsAsync(RecentCount)", recent);
        Assert.Contains("Unable to load transactions.", recent);

        foreach (var source in new[] { finance, recent })
        {
            Assert.DoesNotContain("SessionsApi", source);
        }
    }

    [Fact]
    public void Home_CallsEachEndpointOnce()
    {
        var all = string.Concat(
            Source("Pages", "Home.razor"),
            Source("Home", "GymHomeSummary.razor"),
            Source("Home", "FinanceHomeSummary.razor"),
            Source("Home", "RecentTransactionsSummary.razor"));

        foreach (var call in new[] { "GetBalancesAsync", "GetAccountsAsync", "GetCategoriesAsync", "GetRecentTransactionsAsync", "GetCurrentAsync", "GetHistoryAsync" })
        {
            Assert.Single(Regex.Matches(all, call));
        }
    }

    [Fact]
    public void LastWorkout_NamesTheWorkoutAndWhen()
    {
        var item = new WorkoutHistoryItemResponse(
            Guid.NewGuid(), "Strength", "Push", new DateTimeOffset(2026, 10, 2, 17, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 2, 18, 5, 0, TimeSpan.Zero), 15, 15, 5);

        Assert.Equal(
            "Last workout · Push · Yesterday 18:05",
            HistoryDisplay.LastWorkout(item, new DateTime(2026, 10, 3), TimeZoneInfo.Utc, CultureInfo.GetCultureInfo("en-GB")));
    }

    private static string Source(params string[] path) =>
        File.ReadAllText(Path.Combine([AppRoot(), "Components", .. path]));

    private static string AppRoot([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", "..", "src", "dotnet", "LifeOS.App"));
}
