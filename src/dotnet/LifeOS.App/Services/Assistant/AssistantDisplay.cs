using System.Globalization;
using LifeOS.Contracts.Assistant;

namespace LifeOS.App.Services.Assistant;

// AI-007 F4: readable texts for LifeOS Assistant states and codes. Only the API's stable codes are mapped; the
// assistant's own replies are shown as the API stored them. Plain .NET, no MAUI.
public static class AssistantDisplay
{
	public static string FailureText(string? failureCode) => failureCode switch
	{
		"assistant_unavailable" => "The assistant is unavailable right now.",
		"assistant_timeout" => "The assistant took too long to answer.",
		"assistant_invalid_output" or "assistant_refused" => "The assistant could not process this message.",
		AssistantFailureCodes.Interrupted => "The reply was interrupted.",
		_ => "The message could not be processed."
	};

	public static string StatusText(string status) => status switch
	{
		AssistantOperationStatuses.Collecting => "Draft · details missing",
		AssistantOperationStatuses.AwaitingConfirmation => "Draft · ready to confirm",
		AssistantOperationStatuses.Executed => "Recorded",
		AssistantOperationStatuses.Cancelled => "Cancelled",
		AssistantOperationStatuses.Expired => "Expired",
		_ => status
	};

	public static string TypeText(string? type) => type switch
	{
		AssistantDraftEditor.ExpenseType => "Expense",
		AssistantDraftEditor.IncomeType => "Income",
		_ => "Expense or income?"
	};

	public static string FieldText(string field) => field switch
	{
		"type" => "type",
		"amount" => "amount",
		"accountId" => "account",
		"categoryId" => "category",
		"occurredAt" => "date",
		_ => field
	};

	public static string IssueText(string code) => code switch
	{
		AssistantErrorCodes.AccountNotFound => "The account no longer exists. Choose another one.",
		AssistantErrorCodes.CategoryNotFound => "The category no longer exists. Choose another one.",
		AssistantErrorCodes.CategoryIncompatible => "The category does not match the type (expense or income).",
		AssistantErrorCodes.CurrencyMismatch => "The account uses a different currency from the one you stated. Choose an account in that currency.",
		AssistantErrorCodes.TimeZoneRequired => "Set your time zone to date this transaction.",
		_ => "This draft cannot be confirmed yet."
	};

	public static string MissingText(IReadOnlyList<string> missing) =>
		missing.Count == 0 ? "" : "Missing: " + string.Join(", ", missing.Select(FieldText)) + ".";

	public static string AmountText(FinanceTransactionDraftResponse draft, CultureInfo culture) =>
		draft.Amount is { } amount
			? $"{amount.ToString("N2", culture)} {draft.Currency ?? draft.StatedCurrency}".TrimEnd()
			: "—";

	public static string CategoryText(AssistantDraftCategoryResponse? category) => category switch
	{
		null => "—",
		{ Name: null } => "(deleted category)",
		{ Name: { } name, Source: "Suggested" } => $"{name} (suggested)",
		{ Name: { } name } => name
	};

	public static string AccountText(AssistantDraftAccountResponse? account) => account switch
	{
		null => "—",
		{ Name: null } => "(deleted account)",
		{ Name: { } name } => name
	};

	public static string DateText(FinanceTransactionDraftResponse draft) => draft.Occurrence is { } occurrence
		? $"{occurrence.LocalDate} {occurrence.LocalTime[..5]}"
		: draft.OccurredOn is { } date ? $"{date} {draft.OccurredTime ?? ""}".TrimEnd() : "Today";

	public static string ConversationTitle(AssistantConversationResponse conversation, TimeZoneInfo timeZone, CultureInfo culture) =>
		TimeZoneInfo.ConvertTime(conversation.UpdatedAtUtc, timeZone).ToString("ddd d MMM yyyy, HH:mm", culture);
}
