namespace LifeOS.Application.Assistant;

// AI-007 F3 / AI-008: the interpretation protocol, version 2. The language model has exactly one job: read the
// user's message and return this structure. It never decides anything: .NET validates the structure
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
// Capability sections (Finance, Hydration, Meal) use primitive types only: the core knows the protocol, not the
// modules. A new capability adds its intents and its own section. Version 2 (AI-008) adds the Hydration and Meal
// sections and the generic draft intents (draft_update/confirm/cancel act on whatever draft is open), and moves
// the open draft to the top of the context; Finance's section is unchanged. The service keeps answering version
// 1 for older callers (docs/contracts/assistant-interpretation/v1 and v2).
public static class AssistantInterpretationProtocol
{
    public const int Version = 2;

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

    // A new water intake (AI-008).
    HydrationIntake,

    // A new meal of the meal journal (AI-008).
    NutritionMeal,

    // A change to the open draft, whatever its module (including the answer to a question: "Revolut", "300 ml").
    DraftUpdate,

    // "Confermo", "va bene, salva": never a confirmation (only POST …/confirm writes); the reply points to the
    // button.
    DraftConfirm,

    // "Annulla": detected, never executed from text (only POST …/cancel cancels); the reply points to the button.
    DraftCancel,

    // A question about the user's data or LifeOS (the assistant cannot read data to answer it, and says so).
    GeneralQuestion,

    // The model could not understand the message.
    Clarification,

    // A request the assistant cannot handle (UnsupportedReason says which kind).
    Unsupported
}

public enum AssistantUnsupportedReason
{
    Transfer,

    // Several records in one message (two expenses, a meal and a drink): one at a time.
    MultipleOperations,

    // Another LifeOS function (reminders, journal, habits, gym...): not available in the assistant.
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
// separator); currency a 3-letter code ("EUR"); account and category the mention as written; note free text;
// hydration unit "ml" | "cl" | "l"; meal description the user's words; meal type "breakfast" | "lunch" |
// "dinner" | "snack" | "other".
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

// The field names of the Finance section (AmbiguousFields, the open draft's filled/missing lists).
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

// AI-008: one water intake. Amount: the quantity as the user stated it, in Unit (a plain decimal: "300",
// "0.5"); the backend converts it to whole millilitres. A container ("un bicchiere", "una bottiglia") is not a
// quantity: both stay Absent and the quantity is asked.
public sealed record AssistantHydrationFields(
    AssistantTextField Amount,
    AssistantTextField Unit,
    AssistantDateField Date,
    AssistantTimeField Time)
{
    public static AssistantHydrationFields None { get; } =
        new(AssistantTextField.Absent, AssistantTextField.Absent, AssistantDateField.Absent, AssistantTimeField.Absent);
}

public static class AssistantHydrationFieldNames
{
    public const string Amount = "amount";
    public const string Unit = "unit";
    public const string Date = "date";
    public const string Time = "time";

    public static readonly IReadOnlyList<string> All = [Amount, Unit, Date, Time];

    public static readonly IReadOnlyList<string> Units = ["ml", "cl", "l"];
}

// AI-008: one meal. Description: what the user ate, in the user's own words (never ingredients, quantities or
// nutrition the user did not write). MealType: only when stated.
public sealed record AssistantMealFields(
    AssistantTextField Description,
    AssistantTextField MealType,
    AssistantDateField Date,
    AssistantTimeField Time)
{
    public static AssistantMealFields None { get; } =
        new(AssistantTextField.Absent, AssistantTextField.Absent, AssistantDateField.Absent, AssistantTimeField.Absent);
}

public static class AssistantMealFieldNames
{
    public const string Description = "description";
    public const string MealType = "meal_type";
    public const string Date = "date";
    public const string Time = "time";

    public static readonly IReadOnlyList<string> All = [Description, MealType, Date, Time];

    public static readonly IReadOnlyList<string> MealTypes = ["breakfast", "lunch", "dinner", "snack", "other"];
}

// Every field name of the protocol (AmbiguousFields, the open draft's filled/missing lists): the sections share
// "amount", "date" and "time"; a module reads only its own section.
public static class AssistantFieldNames
{
    public static readonly IReadOnlyList<string> All =
        AssistantFinanceFieldNames.All.Concat(AssistantHydrationFieldNames.All).Concat(AssistantMealFieldNames.All).Distinct().ToList();
}

// What the model returns. Every member is always present on the wire (the strict schema lists them all); every
// section too, with every field Absent when the message is not about it. UnsupportedReason exactly for
// Unsupported.
public sealed record AssistantInterpretation(
    int ProtocolVersion,
    AssistantIntent Intent,
    AssistantFinanceFields Finance,
    AssistantHydrationFields Hydration,
    AssistantMealFields Meal,
    IReadOnlyList<string> AmbiguousFields,
    AssistantUnsupportedReason? UnsupportedReason);

// ---- What the model receives (minimised; no identifiers) ----

// The open draft as the model needs it to read a short answer ("Revolut" after "Da quale conto?", "300 ml"
// after "Quanta acqua?"): its kind, which fields are filled and which are missing, and the field the last reply
// asked for. Never the values (amount, note, description, account or category names) and never ids. Type: the
// Finance draft's "expense" | "income", null for other kinds.
public sealed record AssistantOpenDraftContext(
    string Kind,
    string? Type,
    IReadOnlyList<string> FilledFields,
    IReadOnlyList<string> MissingFields,
    string? AwaitingField);

// The user's category names (bounded), so the model can propose one; accounts are never listed. Empty while a
// draft of another module is open (no transaction can start then).
public sealed record AssistantFinanceContext(
    IReadOnlyList<string> ExpenseCategories,
    IReadOnlyList<string> IncomeCategories)
{
    public static AssistantFinanceContext None { get; } = new([], []);
}

// Hydration and Meal add no context: no history, no target, no settings (AI-008, ADR-014 addendum).
public sealed record AssistantInterpretationContext(string Locale, AssistantOpenDraftContext? OpenDraft, AssistantFinanceContext Finance);

// One request: the current message and the minimal context. No conversation history: the persisted draft
// carries the operational state (AI-007 §8), so earlier messages are not needed to interpret a turn.
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
