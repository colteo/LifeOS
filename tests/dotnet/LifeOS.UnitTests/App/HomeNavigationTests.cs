using System.Runtime.CompilerServices;
using LifeOS.App.Services.Navigation;

namespace LifeOS.UnitTests.App;

// The minimal Home and the global navigation (APP-001): the dock's items and active rules (plain
// .NET), and the Home and header composition, which the net10.0 test project cannot render. Those
// checks read the component sources.
public class HomeNavigationTests
{
    [Fact]
    public void Dock_IsTransactions_New_More_InThatOrder()
    {
        Assert.Equal(["Transactions", "New transaction", "More"], DockNavigation.Items.Select(item => item.Label));
        Assert.Equal(["finance/transactions", "finance/transactions/new", "more"], DockNavigation.Items.Select(item => item.Href));
        Assert.Equal([false, true, false], DockNavigation.Items.Select(item => item.IsPrimary));
    }

    [Fact]
    public void Dock_NoLongerHasHomeOrPortfolio()
    {
        Assert.DoesNotContain(DockNavigation.Items, item => item.Label is "Home" or "Portfolio");
        Assert.DoesNotContain(DockNavigation.Items, item => item.Href is "" or "portfolio");
    }

    [Theory]
    [InlineData("")]
    [InlineData("finance/transactions")]
    [InlineData("finance/transactions/new")]
    [InlineData("more")]
    [InlineData("portfolio")]
    public void NewTransaction_IsNeverSelected(string path)
    {
        Assert.False(Item("New transaction").IsActive(path));
    }

    [Theory]
    [InlineData("finance/transactions", true)]
    [InlineData("finance/transactions/0198c0de-0000-7000-8000-000000000001", true)]
    [InlineData("finance/transactions/0198c0de-0000-7000-8000-000000000001/edit", true)]
    [InlineData("finance/transactions/new", false)]
    [InlineData("", false)]
    [InlineData("finance", false)]
    [InlineData("finance/transactionsx", false)]
    public void Transactions_IsActiveInTheTransactionsArea(string path, bool active)
    {
        Assert.Equal(active, Item("Transactions").IsActive(path));
    }

    [Theory]
    [InlineData("more", true)]
    [InlineData("finance", true)]
    [InlineData("finance/accounts", true)]
    [InlineData("finance/categories", true)]
    [InlineData("", false)]
    [InlineData("settings", false)]
    [InlineData("portfolio", false)]
    public void More_IsActiveOnTheModuleDirectoryAndFinanceManagement(string path, bool active)
    {
        Assert.Equal(active, Item("More").IsActive(path));
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

    [Fact]
    public void DockMarkup_LabelsThePrimaryAction_AndUsesThreeColumns()
    {
        var dock = Source("Layout", "BottomDock.razor");

        Assert.Contains("DockNavigation.Items", dock);
        Assert.Contains("class=\"lo-dock__primary\" aria-label=\"@item.Label\"", dock);
        Assert.Contains("repeat(3, 1fr)", Source("Layout", "BottomDock.razor.css"));
    }

    [Fact]
    public void HeaderWordmark_LinksHome_AndSettingsIsUnchanged()
    {
        var header = Source("Layout", "AppHeader.razor");

        Assert.Contains("<a href=\"\" class=\"lo-wordmark\" aria-label=\"LifeOS home\">LifeOS</a>", header);
        Assert.Contains("<a href=\"settings\" class=\"lo-icon-btn\" aria-label=\"Settings\">", header);
    }

    [Fact]
    public void Home_HasOnlyTheBudgetAndTrainingCards()
    {
        var home = Source("Pages", "Home.razor");

        Assert.Contains("<MonthlyBudgetCard", home);
        Assert.Contains("<HomeTrainingCard />", home);
        Assert.DoesNotContain("PortfolioCard", home);
        Assert.DoesNotContain("Recent transactions", home);
        Assert.DoesNotContain("TransactionRow", home);
        Assert.DoesNotContain("AccountsApi", home);
        Assert.DoesNotContain("TransactionsApi", home);
        Assert.Equal(2, Count(home, "<section class=\"lo-section\">"));
    }

    [Fact]
    public void HomeBudgetCard_LeadsToPortfolioAndAnalytics()
    {
        var home = Source("Pages", "Home.razor");
        var card = Source("Finance", "MonthlyBudgetCard.razor");

        Assert.Contains("href=\"portfolio\"", home);
        Assert.Contains("href=\"finance/analytics\"", home);
        Assert.Contains("<Actions>", home);
        Assert.Contains("@Actions", card);
        Assert.Contains("BudgetsApi.GetAsync(month, Currency)", card);
    }

    private static DockItem Item(string label) => DockNavigation.Items.Single(item => item.Label == label);

    private static int Count(string source, string value) =>
        (source.Length - source.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;

    private static string Source(string folder, string fileName, [CallerFilePath] string testFile = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!, "..", "..", "..", "..", "src", "dotnet", "LifeOS.App", "Components", folder, fileName)));
}
