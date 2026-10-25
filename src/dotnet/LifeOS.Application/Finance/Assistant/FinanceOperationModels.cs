using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Assistant;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;
using FinanceOperation = LifeOS.Domain.Assistant.AssistantOperation<LifeOS.Domain.Finance.Assistant.FinanceTransactionDraft>;

namespace LifeOS.Application.Finance.Assistant;

// AI-007 F2: the Finance capability of LifeOS Assistant (kind finance.transaction.create). It prepares one
// Expense or Income as a draft (no Finance write), lets the user complete and change it, and writes it only
// on the user's explicit confirmation, through CreateTransactionHandler. Structured input only: interpreting
// language is F3, which will build these same inputs and go through the same validation.

// The fields of a new draft; null = not stated. OccurredOn/OccurredTime are the user's explicit local date
// and time. CategorySource: User for a category the user chose; Suggested only for a proposal made on the
// user's behalf (F3), never exposed over HTTP.
public sealed record FinanceDraftInput(
    TransactionType? Type = null,
    decimal? Amount = null,
    string? Currency = null,
    Guid? AccountId = null,
    Guid? CategoryId = null,
    string? Note = null,
    DateOnly? OccurredOn = null,
    TimeOnly? OccurredTime = null,
    FinanceDraftCategorySource CategorySource = FinanceDraftCategorySource.User);

// A change to a draft: a non-null value replaces the field, a name in Clear removes it (FinanceDraftFields:
// type, amount, currency, accountId, categoryId, note, occurredOn, occurredTime), anything else is kept. A
// field cannot be both set and cleared.
public sealed record FinanceDraftPatch(
    TransactionType? Type = null,
    decimal? Amount = null,
    string? Currency = null,
    Guid? AccountId = null,
    Guid? CategoryId = null,
    string? Note = null,
    DateOnly? OccurredOn = null,
    TimeOnly? OccurredTime = null,
    IReadOnlyCollection<string>? Clear = null,
    FinanceDraftCategorySource CategorySource = FinanceDraftCategorySource.User)
{
    public static readonly IReadOnlyList<string> ClearableFields =
    [
        FinanceDraftFields.Type, FinanceDraftFields.Amount, FinanceDraftFields.Currency, FinanceDraftFields.Account,
        FinanceDraftFields.Category, FinanceDraftFields.Note, FinanceDraftFields.OccurredOn, FinanceDraftFields.OccurredTime
    ];
}

// The draft's account as Finance knows it now. Name and Currency are null when it no longer exists.
public sealed record FinanceDraftAccountView(Guid Id, string? Name, string? Currency, AccountType? Type);

// The draft's category as Finance knows it now. Name and Type are null when it no longer exists.
public sealed record FinanceDraftCategoryView(Guid Id, string? Name, CategoryType? Type, Guid? ParentCategoryId, FinanceDraftCategorySource Source);

// An operation with what the user needs to review it: the referenced Finance resources, the problems that
// block a confirmation now (Issues, FinanceOperationErrors codes, open operations only), whether the
// occurrence lies in the future (shown explicitly before confirming) and whether a confirmation would be
// attempted (AwaitingConfirmation, no issue, not expired).
public sealed record FinanceOperationView(
    FinanceOperation Operation,
    FinanceDraftAccountView? Account,
    FinanceDraftCategoryView? Category,
    IReadOnlyList<string> Issues,
    bool OccursInFuture,
    bool CanConfirm);

// The outcome of a write. Changed: something was stored by this request (false for a PATCH equal to the
// draft, a repeated confirmation of an Executed operation, a repeated cancellation).
public sealed record FinanceOperationOutcome(FinanceOperationView View, bool Changed);

// The stable codes of draft problems the user can correct (AssistantResultStatus.Rejected, and Issues).
public static class FinanceOperationErrors
{
    // The account does not exist (or is another user's, or was deleted).
    public const string AccountNotFound = "account_not_found";

    public const string CategoryNotFound = "category_not_found";

    // The category's type differs from the draft's (an Expense needs an Expense category).
    public const string CategoryIncompatible = "category_incompatible";

    // The currency the user stated differs from the account's: a compatible account is needed.
    public const string CurrencyMismatch = "currency_mismatch";

    // The user has no valid time zone, so the date cannot be resolved (never assumed, AI-007 F2 B.6).
    public const string TimeZoneRequired = "time_zone_required";

    // CreateTransactionHandler refused the draft for another reason.
    public const string TransactionRejected = "transaction_rejected";
}
