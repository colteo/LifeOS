namespace LifeOS.Application.Assistant;

// AI-007 F3: the interpretation protocol, version 1. The language model has exactly one job: read the user's
// message and return this structure. It never decides anything: .NET validates the structure
// (AssistantInterpretationValidator), resolves every mention against the user's own data, computes dates,
// applies the result to the draft through the capability's existing use cases and writes the reply.
//
// Every field distinguishes four states, which is the core of the protocol:
//   - Absent:   the message says nothing about the field (the draft keeps what it has);
//   - Stated:   the user said it explicitly; Span is the verbatim text it comes from, which must occur in
//               the message, so a value the model made up is refused;
//   - Inferred: a proposal of the model (only where a proposal is allowed: the category, the note); it never
//               replaces something the user chose;
//   - Clear:    the user explicitly asked to remove the field.
// There is no confidence score: nothing the model says authorises a write. A field the model finds
// ambiguous is listed in AmbiguousFields and asked again instead of applied.
//
// The protocol carries no LifeOS identifier in either direction (no user, conversation, operation, account
// or category id), no account list, no balances and no dates computed by the model: a date is a structured
// expression ("yesterday", "last Monday", "21/10") that the backend resolves in the user's time zone.
//
// Capability sections (Finance in v1) use primitive types only: the core knows the protocol, not the
// modules. A new capability adds its intents and its own section.
public static class AssistantInterpretationProtocol
{
    public const int Version = 1;

    public const string Locale = "it-IT";

    // Bounds of what the model may return (validated, never truncated). Lengths count Unicode code points, as
    // JSON Schema and Python do. The canonical values are in docs/contracts/assistant-interpretation/v1.
    public const int MaxValueLength = 200;
    public const int MaxNoteLength = 1000;
    public const int MaxSpanLength = 1000;

    // Bounds of the request: a category name longer than this is not offered to the model (the service
    // would refuse the whole request).
    public const int MaxCategoryNames = 100;
    public const int MaxCategoryNameLength = 200;
}

public enum AssistantIntent
{
    // A new expense or income (one transaction).
    FinanceExpense,
    FinanceIncome,

    // A change to the open draft (including the answer to a question: "Revolut").
    FinanceDraftUpdate,

    // "Confermo", "va bene, salva": never a confirmation (only POST …/confirm writes); the reply points to the
    // button.
    FinanceDraftConfirm,

    // "Annulla": detected, never executed from text (only POST …/cancel cancels); the reply points to the button.
    FinanceDraftCancel,

    // A question about the user's data or LifeOS (v1 cannot read data to answer it, and says so).
    GeneralQuestion,

    // The model could not understand the message.
    Clarification,

    // A request the assistant cannot handle (UnsupportedReason says which kind).
    Unsupported
}

public enum AssistantUnsupportedReason
{
    Transfer,
    MultipleTransactions,
    OtherCapability,
    Other
}

public enum AssistantFieldState
{
    Absent,
    Stated,
    Inferred,
    Clear
}

// A text-valued field. Value formats: type "expense" | "income"; amount a plain decimal ("5", "12.50", dot
// separator); currency a 3-letter code ("EUR"); account and category the mention as written; note free text.
public sealed record AssistantTextField(AssistantFieldState State, string? Value = null, string? Span = null)
{
    public static AssistantTextField Absent { get; } = new(AssistantFieldState.Absent);
}

public enum AssistantDateKind
{
    Today,
    Yesterday,
    DayBeforeYesterday,

    // DaysAgo days before today (1–366).
    DaysAgo,

    // The most recent Weekday strictly before today ("lunedì scorso"): 1–7 days ago.
    Weekday,

    // An explicit calendar date: Day and Month, Year when stated (otherwise the current year).
    Calendar
}

public sealed record AssistantDateField(
    AssistantFieldState State,
    AssistantDateKind? Kind = null,
    int? DaysAgo = null,
    DayOfWeek? Weekday = null,
    int? Day = null,
    int? Month = null,
    int? Year = null,
    string? Span = null)
{
    public static AssistantDateField Absent { get; } = new(AssistantFieldState.Absent);
}

public sealed record AssistantTimeField(AssistantFieldState State, int? Hour = null, int? Minute = null, string? Span = null)
{
    public static AssistantTimeField Absent { get; } = new(AssistantFieldState.Absent);
}

public sealed record AssistantFinanceFields(
    AssistantTextField Type,
    AssistantTextField Amount,
    AssistantTextField Currency,
    AssistantTextField Account,
    AssistantTextField Category,
    AssistantTextField Note,
    AssistantDateField Date,
    AssistantTimeField Time)
{
    public static AssistantFinanceFields None { get; } = new(AssistantTextField.Absent, AssistantTextField.Absent, AssistantTextField.Absent,
        AssistantTextField.Absent, AssistantTextField.Absent, AssistantTextField.Absent, AssistantDateField.Absent, AssistantTimeField.Absent);
}

// The field names used by AmbiguousFields and by the context's missing/filled lists.
public static class AssistantFinanceFieldNames
{
    public const string Type = "type";
    public const string Amount = "amount";
    public const string Currency = "currency";
    public const string Account = "account";
    public const string Category = "category";
    public const string Note = "note";
    public const string Date = "date";
    public const string Time = "time";

    public static readonly IReadOnlyList<string> All = [Type, Amount, Currency, Account, Category, Note, Date, Time];
}

// What the model returns. Every member is always present on the wire (the strict schema lists them all);
// Finance too, with every field Absent when the intent changes no draft. UnsupportedReason exactly for
// Unsupported.
public sealed record AssistantInterpretation(
    int ProtocolVersion,
    AssistantIntent Intent,
    AssistantFinanceFields Finance,
    IReadOnlyList<string> AmbiguousFields,
    AssistantUnsupportedReason? UnsupportedReason);

// ---- What the model receives (minimised; no identifiers) ----

// The open draft as the model needs it to read a short answer ("Revolut" after "Da quale conto?"): which
// fields are filled and which are missing, and the field the last reply asked for. Never the values
// (amount, note, account or category names) and never ids.
public sealed record AssistantOpenDraftContext(
    string Kind,
    string? Type,
    IReadOnlyList<string> FilledFields,
    IReadOnlyList<string> MissingFields,
    string? AwaitingField);

// The user's category names (bounded), so the model can propose one; accounts are never listed.
public sealed record AssistantFinanceContext(
    AssistantOpenDraftContext? OpenDraft,
    IReadOnlyList<string> ExpenseCategories,
    IReadOnlyList<string> IncomeCategories);

public sealed record AssistantInterpretationContext(string Locale, AssistantFinanceContext Finance);

// One request: the current message and the minimal context. No conversation history in v1: the persisted
// draft carries the operational state (AI-007 §8), so earlier messages are not needed to interpret a turn.
public sealed record AssistantInterpretationRequest(int ProtocolVersion, string Message, AssistantInterpretationContext Context);

public enum AssistantInterpretationFailure
{
    // Not configured (the default), unreachable, rate limited, or the service failed.
    Unavailable,

    // No answer within the time the turn can wait.
    Timeout,

    // An answer that is not a valid interpretation (malformed, wrong version, broken protocol rules).
    InvalidOutput,

    // The provider refused to interpret the message.
    Refused
}

public sealed record AssistantInterpretationResult(AssistantInterpretation? Interpretation, AssistantInterpretationFailure? Failure)
{
    public static AssistantInterpretationResult Success(AssistantInterpretation interpretation) => new(interpretation, null);

    public static AssistantInterpretationResult Failed(AssistantInterpretationFailure failure) => new(null, failure);
}

// The port to the language model (implemented in Infrastructure; disabled by default: ADR-014 is not
// accepted). Every failure is a result, never an exception, except the caller's own cancellation.
public interface IAssistantInterpreter
{
    Task<AssistantInterpretationResult> InterpretAsync(AssistantInterpretationRequest request, CancellationToken cancellationToken);
}
