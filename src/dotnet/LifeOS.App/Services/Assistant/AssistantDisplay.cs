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

	public static bool IsFinance(AssistantOperationResponse operation) => operation.Kind == AssistantOperationKindNames.FinanceTransactionCreate;

	// AI-008: the draft's heading, by kind.
	public static string TitleText(AssistantOperationResponse operation) => operation.Kind switch
	{
		AssistantOperationKindNames.HydrationIntakeCreate => "Water",
		AssistantOperationKindNames.NutritionMealCreate => "Meal",
		_ => TypeText(operation.Draft?.Type)
	};

	// The draft in one line (the pinned bar).
	public static string SummaryText(AssistantOperationResponse operation, CultureInfo culture) => operation.Kind switch
	{
		AssistantOperationKindNames.HydrationIntakeCreate => "Water · " + WaterText(operation.Hydration),
		AssistantOperationKindNames.NutritionMealCreate => "Meal · " + (operation.Meal?.Description ?? "—"),
		_ => $"{TypeText(operation.Draft?.Type)} · {(operation.Draft is { } draft ? AmountText(draft, culture) : "—")}"
	};

	public static string WaterText(HydrationIntakeDraftResponse? draft) => draft?.AmountMl is { } millilitres
		? millilitres.ToString(CultureInfo.InvariantCulture) + " ml"
		: "—";

	public static string MealTypeText(string? mealType) => mealType switch
	{
		null => "—",
		"Breakfast" => "Breakfast",
		"Lunch" => "Lunch",
		"Dinner" => "Dinner",
		"Snack" => "Snack",
		_ => "Other"
	};

	public static string RecordedNotice(string kind) => kind switch
	{
		AssistantOperationKindNames.HydrationIntakeCreate => "Water recorded.",
		AssistantOperationKindNames.NutritionMealCreate => "Meal recorded.",
		_ => AssistantChatFlow.RecordedNotice
	};

	public static string FieldText(string field) => field switch
	{
		"type" => "type",
		"amount" => "amount",
		"accountId" => "account",
		"categoryId" => "category",
		"occurredAt" => "date",
		"amountMl" => "amount",
		"description" => "what you ate",
		_ => field
	};

	public static string IssueText(string code) => code switch
	{
		AssistantErrorCodes.AccountNotFound => "The account no longer exists. Choose another one.",
		AssistantErrorCodes.CategoryNotFound => "The category no longer exists. Choose another one.",
		AssistantErrorCodes.CategoryIncompatible => "The category does not match the type (expense or income).",
		AssistantErrorCodes.CurrencyMismatch => "The account uses a different currency from the one you stated. Choose an account in that currency.",
		AssistantErrorCodes.TimeZoneRequired => "Set your time zone to date this transaction.",
		AssistantErrorCodes.FutureDate => "Water cannot be logged for a future day. Change the date.",
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

	public static string DateText(FinanceTransactionDraftResponse draft) => DateText(draft.Occurrence, draft.OccurredOn, draft.OccurredTime);

	public static string DateText(AssistantDraftOccurrenceResponse? occurrence, string? occurredOn, string? occurredTime) => occurrence is not null
		? $"{occurrence.LocalDate} {occurrence.LocalTime[..5]}"
		: occurredOn is { } date ? $"{date} {occurredTime ?? ""}".TrimEnd() : "Today";

	public static string ConversationTitle(AssistantConversationResponse conversation, TimeZoneInfo timeZone, CultureInfo culture) =>
		TimeZoneInfo.ConvertTime(conversation.UpdatedAtUtc, timeZone).ToString("ddd d MMM yyyy, HH:mm", culture);
}
