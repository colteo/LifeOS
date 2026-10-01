using System.Globalization;
using LifeOS.Contracts.Finance.Accounts;
using LifeOS.Contracts.Finance.Categories;
using LifeOS.Contracts.Finance.Transactions;

namespace LifeOS.App.Services.Finance;

// Display rules for transactions shared by the Transactions page and Home. Plain .NET, no MAUI
// dependency. CategoryLabel and AccountName return null when unresolved; Title and Details use the
// fallback wording below, so a missing name never breaks a row.
public static class TransactionDisplay
{
	public const string Expense = "Expense";
	public const string Income = "Income";
	public const string Transfer = "Transfer";

	public const string UnknownCategory = "Unknown category";
	public const string UnknownAccount = "Unknown account";
	public const string TransferTitle = "⇄ Transfer";

	// Primary line of a transaction row: the note when present; otherwise the category
	// ("Parent · Child"), or "⇄ Transfer" for a transfer.
	public static string Title(TransactionResponse transaction, IReadOnlyList<CategoryResponse> categories) =>
		Note(transaction)
		?? (transaction.Type == Transfer
			? TransferTitle
			: CategoryLabel(categories, transaction.CategoryId) ?? UnknownCategory);

	// Secondary line: where the money moved. A transfer shows "Source → Destination". Income and
	// Expense show the account, preceded by the category when the note took the title.
	public static string Details(
		TransactionResponse transaction,
		IReadOnlyList<AccountResponse> accounts,
		IReadOnlyList<CategoryResponse> categories)
	{
		if (transaction.Type == Transfer)
		{
			return $"{AccountName(accounts, transaction.SourceAccountId) ?? UnknownAccount} → "
				+ $"{AccountName(accounts, transaction.DestinationAccountId) ?? UnknownAccount}";
		}

		var account = AccountName(accounts, transaction.AccountId) ?? UnknownAccount;

		return Note(transaction) is null
			? account
			: $"{CategoryLabel(categories, transaction.CategoryId) ?? UnknownCategory} · {account}";
	}

	private static string? Note(TransactionResponse transaction) =>
		string.IsNullOrWhiteSpace(transaction.Note) ? null : transaction.Note.Trim();

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
