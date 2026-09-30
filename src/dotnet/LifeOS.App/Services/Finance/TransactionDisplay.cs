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

	// The UI is English-only, so textual dates (month names) use en-GB, which keeps the natural
	// day-month order, whatever the device culture. Times and numbers still follow the device culture.
	public static readonly CultureInfo DateTextCulture = CultureInfo.GetCultureInfo("en-GB");

	// Local day label: "Today", "Yesterday", "27 September", "27 September 2025".
	public static string DayLabel(DateTime localDate, DateTime today)
	{
		if (localDate.Date == today.Date)
		{
			return "Today";
		}

		if (localDate.Date == today.Date.AddDays(-1))
		{
			return "Yesterday";
		}

		var monthDay = DateTextCulture.DateTimeFormat.MonthDayPattern;
		var pattern = localDate.Year == today.Year ? monthDay : $"{monthDay} yyyy";

		return localDate.ToString(pattern, DateTextCulture);
	}

	// Compact local date and time: "Today 18:05", "Yesterday 09:30", "27 September · 14:10",
	// "27 September 2025 · 14:10". The time follows the given (device) culture.
	public static string CompactDateTime(DateTimeOffset occurredAtUtc, DateTime today, TimeZoneInfo timeZone, CultureInfo culture)
	{
		var local = TimeZoneInfo.ConvertTime(occurredAtUtc, timeZone);
		var time = local.ToString(culture.DateTimeFormat.ShortTimePattern, culture);
		var date = local.Date;
		var day = DayLabel(date, today);

		return date == today.Date || date == today.Date.AddDays(-1) ? $"{day} {time}" : $"{day} · {time}";
	}
}
