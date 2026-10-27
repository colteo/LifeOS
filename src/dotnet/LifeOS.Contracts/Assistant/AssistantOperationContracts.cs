namespace LifeOS.Contracts.Assistant;

// AI-007 F2: operations the assistant prepares in a conversation: "finance.transaction.create" (one Expense or
// Income) and, since AI-008, "hydration.intake.create" (one water intake) and "nutrition.meal.create" (one meal of
// the meal journal). Each is prepared as a draft (nothing is written to its module), completed and changed by the
// user, and recorded only by the explicit confirm request. There is never a user id: the owner is the
// authenticated user. Writes carry the draft version the client last saw (expectedVersion); a different stored
// version is a 409 version_conflict. Which draft section a response carries, and which fields a change may set,
// follows the operation's kind.

// Kind (required): "finance.transaction.create". Every other field is optional — a draft may start
// incomplete. Type: "Expense" or "Income". Amount: > 0, at most 4 decimals. Currency: the currency the user
// stated (3 letters); the transaction always takes the account's currency, and an account in another
// currency is refused. AccountId/CategoryId: the user's own account/category (the account is never chosen by
// default). Note: up to 1000 characters (people's names belong here). OccurredOn: "yyyy-MM-dd", the user's
// local date (default: today). OccurredTime: "HH:mm" or "HH:mm:ss", local (default: now for today, 12:00
// for another date).
public sealed record CreateAssistantOperationRequest(
    string? Kind,
    string? Type,
    decimal? Amount,
    string? Currency,
    Guid? AccountId,
    Guid? CategoryId,
    string? Note,
    string? OccurredOn,
    string? OccurredTime);

// ExpectedVersion (required): the draftVersion the client is changing. A non-null field replaces the draft's
// value; Clear lists fields to remove; other fields are kept. A request that changes nothing returns the
// operation unchanged (same draftVersion and expiry). Fields by kind (a field of another kind is a 400):
//   finance.transaction.create: type, amount, currency, accountId, categoryId, note, occurredOn, occurredTime
//     (all clearable);
//   hydration.intake.create: amountMl (1–4000 whole ml), occurredOn, occurredTime (all clearable);
//   nutrition.meal.create: description, mealType ("Breakfast", "Lunch", "Dinner", "Snack", "Other"), occurredOn,
//     occurredTime (all clearable).
public sealed record UpdateAssistantOperationRequest(
    int? ExpectedVersion,
    string? Type,
    decimal? Amount,
    string? Currency,
    Guid? AccountId,
    Guid? CategoryId,
    string? Note,
    string? OccurredOn,
    string? OccurredTime,
    IReadOnlyList<string>? Clear,
    int? AmountMl = null,
    string? Description = null,
    string? MealType = null);

// Confirm and cancel. ExpectedVersion (required): the draftVersion the user saw.
public sealed record AssistantOperationVersionRequest(int? ExpectedVersion);

// Status: AssistantOperationStatuses. MissingFields: what a confirmation still needs (Finance: "type", "amount",
// "accountId", "categoryId", "occurredAt"; water: "amountMl", "occurredAt"; meal: "description", "occurredAt").
// Issues (open operations only): problems that block a confirmation now (AssistantErrorCodes: account_not_found,
// category_not_found, category_incompatible, currency_mismatch, time_zone_required, future_date). CanConfirm:
// AwaitingConfirmation with no issue. ExpiresAtUtc: 24 h after the last substantive change; reads never extend it.
// Exactly one draft section is set, the one of Kind: Draft (Finance; kept under this name for AI-007 clients),
// Hydration or Meal. ResultId: Executed only, the record the confirmation created (the transaction, the intake,
// the meal); TransactionId: the same id, Finance only.
public sealed record AssistantOperationResponse(
    Guid Id,
    Guid ConversationId,
    string Kind,
    string Status,
    int DraftVersion,
    int PayloadVersion,
    FinanceTransactionDraftResponse? Draft,
    IReadOnlyList<string> MissingFields,
    IReadOnlyList<string> Issues,
    bool CanConfirm,
    DateTimeOffset ExpiresAtUtc,
    Guid? TransactionId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ExecutedAtUtc,
    HydrationIntakeDraftResponse? Hydration = null,
    MealDraftResponse? Meal = null,
    Guid? ResultId = null);

// AI-008: a water intake draft. AmountMl: whole millilitres. OccurredOn/OccurredTime: the explicitly stated local
// date/time (null = default); Occurrence: the resolved instant, null while the user has no valid time zone.
public sealed record HydrationIntakeDraftResponse(
    int? AmountMl,
    string? OccurredOn,
    string? OccurredTime,
    AssistantDraftOccurrenceResponse? Occurrence);

// AI-008: a meal draft. Description: the user's own words. MealType: "Breakfast", "Lunch", "Dinner", "Snack",
// "Other" or null. No nutrition: estimating it stays an action of the meal journal.
public sealed record MealDraftResponse(
    string? Description,
    string? MealType,
    string? OccurredOn,
    string? OccurredTime,
    AssistantDraftOccurrenceResponse? Occurrence);

// Currency: the currency the transaction will be recorded in (the account's), null while there is no
// existing account. StatedCurrency: the currency the user stated, if any. OccurredOn/OccurredTime: the
// explicitly stated local date/time (null = default). Occurrence: the resolved instant, null while the user
// has no valid time zone.
public sealed record FinanceTransactionDraftResponse(
    string? Type,
    decimal? Amount,
    string? Currency,
    string? StatedCurrency,
    AssistantDraftAccountResponse? Account,
    AssistantDraftCategoryResponse? Category,
    string? Note,
    string? OccurredOn,
    string? OccurredTime,
    AssistantDraftOccurrenceResponse? Occurrence);

// Name, Currency and Type are null when the account no longer exists.
public sealed record AssistantDraftAccountResponse(Guid Id, string? Name, string? Currency, string? Type);

// Name and Type are null when the category no longer exists. Source: "User" or "Suggested".
public sealed record AssistantDraftCategoryResponse(Guid Id, string? Name, string? Type, Guid? ParentCategoryId, string Source);

// LocalDate "yyyy-MM-dd" and LocalTime "HH:mm:ss" in TimeZoneId. DateSource: "Explicit" or "Today".
// TimeSource: "Explicit", "CurrentTime" or "Noon" (the 12:00 convention for a date without a time).
// IsFuture: the instant lies in the future (only possible from an explicit date or time).
public sealed record AssistantDraftOccurrenceResponse(
    string LocalDate,
    string LocalTime,
    string DateSource,
    string TimeSource,
    string TimeZoneId,
    DateTimeOffset OccurredAtUtc,
    bool IsFuture);

// GET /conversations/{id}: the conversation and its open operation, if any.
public sealed record AssistantConversationDetailResponse(AssistantConversationResponse Conversation, AssistantOperationResponse? ActiveOperation);

// The outcome of a confirmation. Replayed: the operation had already been executed (by an earlier or a
// concurrent request); ResultId is the same record, never a second one. TransactionId: Finance only.
public sealed record AssistantOperationConfirmationResponse(Guid? TransactionId, bool Replayed, AssistantOperationResponse Operation,
    Guid? ResultId = null);

public static class AssistantOperationKindNames
{
    public const string FinanceTransactionCreate = "finance.transaction.create";
    public const string HydrationIntakeCreate = "hydration.intake.create";
    public const string NutritionMealCreate = "nutrition.meal.create";
}

public static class AssistantOperationStatuses
{
    public const string Collecting = "Collecting";
    public const string AwaitingConfirmation = "AwaitingConfirmation";
    public const string Executed = "Executed";
    public const string Cancelled = "Cancelled";
    public const string Expired = "Expired";
}
