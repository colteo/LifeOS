using System.Globalization;
using LifeOS.Contracts.Finance.Accounts;
using LifeOS.Contracts.Finance.Categories;
using LifeOS.Contracts.Finance.Transactions;

namespace LifeOS.App.Services.Finance;

// Display rules for transactions shared by the Transactions page and Home. Plain .NET, no MAUI
// dependency. Unresolved names return null, so each page chooses its own fallback wording.
public static class TransactionDisplay
{
	public const string Expense = "Expense";
	public const string Income = "Income";
	public const string Transfer = "Transfer";

	// Subcategories are shown as "Parent · Child".
	public static string? CategoryLabel(IReadOnlyList<CategoryResponse> categories, Guid? categoryId)
	{
		var category = categories.FirstOrDefault(candidate => candidate.Id == categoryId);

		if (category is null)
		{
			return null;
		}

		var parent = category.ParentCategoryId is { } parentId
			? categories.FirstOrDefault(candidate => candidate.Id == parentId)
			: null;

		return parent is null ? category.Name : $"{parent.Name} · {category.Name}";
	}

	public static string? AccountName(IReadOnlyList<AccountResponse> accounts, Guid? accountId) =>
		accounts.FirstOrDefault(account => account.Id == accountId)?.Name;

	// The stored amount is always positive; the type gives the sign shown (a transfer has none).
	public static string AmountText(TransactionResponse transaction, CultureInfo? culture = null)
	{
		var amount = $"{transaction.Amount.ToString("#,##0.00##", culture ?? CultureInfo.CurrentCulture)} {transaction.Currency}";

		return transaction.Type switch
		{
			Expense => $"−{amount}",
			Income => $"+{amount}",
			_ => amount
		};
	}

	public static string AmountCssClass(TransactionResponse transaction) => transaction.Type switch
	{
		Expense => "text-danger",
		Income => "text-success",
		_ => "text-secondary"
	};

	// Compact local date and time: "Oggi 18:05", "Ieri 09:30", "27 settembre · 14:10",
	// "27 settembre 2025 · 14:10". "Oggi"/"Ieri" are Italian UI labels; the rest follows the culture.
	public static string CompactDateTime(DateTimeOffset occurredAtUtc, DateTime today, TimeZoneInfo timeZone, CultureInfo culture)
	{
		var local = TimeZoneInfo.ConvertTime(occurredAtUtc, timeZone);
		var time = local.ToString(culture.DateTimeFormat.ShortTimePattern, culture);
		var date = local.Date;

		if (date == today.Date)
		{
			return $"Oggi {time}";
		}

		if (date == today.Date.AddDays(-1))
		{
			return $"Ieri {time}";
		}

		var monthDay = culture.DateTimeFormat.MonthDayPattern;
		var pattern = date.Year == today.Year ? monthDay : $"{monthDay} yyyy";

		return $"{local.ToString(pattern, culture)} · {time}";
	}
}
