using LifeOS.App.Services.Finance;
using LifeOS.Contracts.Finance.Accounts;

namespace LifeOS.UnitTests.Finance;

public class PortfolioSummaryTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 30, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Balances_AreSummedPerCurrency_IncludingNegativeOnes()
    {
        var portfolio = PortfolioSummary.Build(
            [Balance("EUR", 1595.30m), Balance("EUR", -350m), Balance("EUR", 0m)],
            "EUR");

        var eur = Assert.Single(portfolio);
        Assert.Equal(1245.30m, eur.KnownTotal);
        Assert.Equal(PortfolioStatus.Complete, eur.Status);
    }

    [Fact]
    public void DifferentCurrencies_AreNeverAddedTogether()
    {
        var portfolio = PortfolioSummary.Build([Balance("EUR", 100m), Balance("USD", 320m)], "EUR");

        Assert.Equal(["EUR", "USD"], portfolio.Select(currency => currency.Currency));
        Assert.Equal(100m, portfolio[0].KnownTotal);
        Assert.Equal(320m, portfolio[1].KnownTotal);
    }

    [Fact]
    public void SomeUnavailableBalances_MakeThePortfolioPartial()
    {
        var eur = Assert.Single(PortfolioSummary.Build([Balance("EUR", 100m), Balance("EUR", null), Balance("EUR", null)], null));

        Assert.Equal(PortfolioStatus.Partial, eur.Status);
        Assert.Equal(100m, eur.KnownTotal);
        Assert.Equal(1, eur.AvailableCount);
        Assert.Equal(2, eur.UnavailableCount);
    }

    [Fact]
    public void OnlyUnavailableBalances_AreUnavailable()
    {
        var eur = Assert.Single(PortfolioSummary.Build([Balance("EUR", null)], null));

        Assert.Equal(PortfolioStatus.Unavailable, eur.Status);
    }

    [Fact]
    public void DefaultCurrencyFirst_ThenAlphabetical()
    {
        var portfolio = PortfolioSummary.Build(
            [Balance("CHF", 1m), Balance("USD", 1m), Balance("EUR", 1m), Balance("GBP", 1m)],
            "USD");

        Assert.Equal(["USD", "CHF", "EUR", "GBP"], portfolio.Select(currency => currency.Currency));
    }

    [Fact]
    public void WithoutDefaultCurrency_IsAlphabetical()
    {
        var portfolio = PortfolioSummary.Build([Balance("USD", 1m), Balance("EUR", 1m)], null);

        Assert.Equal(["EUR", "USD"], portfolio.Select(currency => currency.Currency));
    }

    [Fact]
    public void DefaultCurrencyWithoutAccounts_GetsNoRow()
    {
        var portfolio = PortfolioSummary.Build([Balance("USD", 1m)], "EUR");

        Assert.Equal(["USD"], portfolio.Select(currency => currency.Currency));
    }

    [Fact]
    public void AccountCount_IncludesAccountsWithAndWithoutABalance()
    {
        var portfolio = PortfolioSummary.Build(
            [Balance("EUR", 100m), Balance("EUR", null), Balance("USD", 320m)],
            "EUR");

        Assert.Equal(2, portfolio[0].AccountCount);
        Assert.Equal(1, portfolio[1].AccountCount);
    }

    [Fact]
    public void NoBalances_GiveAnEmptyPortfolio()
    {
        Assert.Empty(PortfolioSummary.Build([], "EUR"));
    }

    private static AccountBalanceResponse Balance(string currency, decimal? balance) =>
        new(Guid.CreateVersion7(), currency, balance, At, null);
}
