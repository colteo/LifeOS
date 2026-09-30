using LifeOS.Contracts.Finance.Accounts;

namespace LifeOS.App.Services.Finance;

public enum PortfolioStatus
{
	// Every account balance in the currency is available.
	Complete,

	// Some balances are unavailable: the total covers only the known ones.
	Partial,

	// No balance in the currency is available.
	Unavailable
}

// The net amount held in one currency: the sum of the known (signed) account balances.
public sealed record CurrencyPortfolio(string Currency, decimal KnownTotal, int AvailableCount, int UnavailableCount)
{
	public PortfolioStatus Status =>
		UnavailableCount == 0 ? PortfolioStatus.Complete
		: AvailableCount == 0 ? PortfolioStatus.Unavailable
		: PortfolioStatus.Partial;
}

// Groups current account balances by currency. Different currencies are never added together (no
// exchange rates), and an unavailable balance is never treated as zero. Plain .NET, no MAUI.
public static class PortfolioSummary
{
	// One row per currency that actually has accounts; the default currency first, then the others
	// alphabetically.
	public static IReadOnlyList<CurrencyPortfolio> Build(IEnumerable<AccountBalanceResponse> balances, string? defaultCurrency) =>
		balances
			.GroupBy(balance => balance.Currency, StringComparer.OrdinalIgnoreCase)
			.Select(group => new CurrencyPortfolio(
				group.Key,
				group.Sum(balance => balance.Balance ?? 0m),
				group.Count(balance => balance.Balance is not null),
				group.Count(balance => balance.Balance is null)))
			.OrderBy(portfolio => string.Equals(portfolio.Currency, defaultCurrency, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
			.ThenBy(portfolio => portfolio.Currency, StringComparer.Ordinal)
			.ToList();
}
