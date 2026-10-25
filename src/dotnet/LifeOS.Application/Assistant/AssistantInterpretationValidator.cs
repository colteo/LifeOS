using System.Globalization;
using System.Text;

namespace LifeOS.Application.Assistant;

// AI-007 F3: the backend's check of an interpretation before anything uses it. The model's output is
// untrusted: whatever breaks a protocol rule makes the whole interpretation invalid (the turn fails with
// assistant_invalid_output); nothing is repaired by guessing (ADR-014 §6). The Python service validates the
// same rules; they are checked again here because .NET never trusts what crosses the boundary.
//
// Rules (see AssistantInterpretation): the protocol version; a known intent; the Finance section for the
// intents that change a draft; UnsupportedReason exactly for Unsupported; ambiguous fields among the known
// names; per field, a known state with exactly the parts that state allows; Inferred only for the category
// and the note (the model may propose those, never an amount, an account, a date or a time); Clear only for
// fields that can be removed; a Stated value always with its verbatim Span, found in the message; a Stated
// account or category is a mention found in its Span (a name the user did not write is refused); a Stated
// note is the user's own words, case included (found in the message as written: the model can neither
// reword it nor change the case of a name in it); value formats (type, amount, currency) and date/time
// ranges. Lengths count code points. Ranges that belong to Finance (the maximum amount) are the capability's.
//
// The canonical contract (schema, limits, golden cases) is docs/contracts/assistant-interpretation/v1: the
// .NET and the Python test suites both check themselves against it.
public static class AssistantInterpretationValidator
{
    public const int MaxDaysAgo = 366;

    // Null when valid; otherwise a short diagnostic code (never message content) for tests and logs.
    public static string? Validate(AssistantInterpretation? interpretation, string message)
    {
        if (interpretation is null)
        {
            return "missing";
        }

        if (interpretation.ProtocolVersion != AssistantInterpretationProtocol.Version)
        {
            return "protocol_version";
        }

        if (!Enum.IsDefined(interpretation.Intent))
        {
            return "intent";
        }

        if (interpretation.AmbiguousFields is null
            || interpretation.AmbiguousFields.Any(field => field is null || !AssistantFinanceFieldNames.All.Contains(field))
            || interpretation.AmbiguousFields.Distinct().Count() != interpretation.AmbiguousFields.Count)
        {
            return "ambiguous_fields";
        }

        var unsupported = interpretation.Intent == AssistantIntent.Unsupported;

        if (unsupported != interpretation.UnsupportedReason.HasValue
            || (interpretation.UnsupportedReason is { } reason && !Enum.IsDefined(reason)))
        {
            return "unsupported_reason";
        }

        // Always present on the wire; null only if a caller bypassed the strict deserialisation.
        if (interpretation.Finance is not { } finance)
        {
            return "finance_missing";
        }

        var normalizedMessage = NormalizeForMatch(message);

        return ValidateType(finance.Type, interpretation.Intent, normalizedMessage)
            ?? ValidateText(finance.Amount, AssistantFinanceFieldNames.Amount, normalizedMessage, canInfer: false, canClear: false, IsAmount)
            ?? ValidateText(finance.Currency, AssistantFinanceFieldNames.Currency, normalizedMessage, canInfer: false, canClear: true, IsCurrency)
            ?? ValidateMention(finance.Account, AssistantFinanceFieldNames.Account, normalizedMessage, canInfer: false)
            ?? ValidateMention(finance.Category, AssistantFinanceFieldNames.Category, normalizedMessage, canInfer: true)
            ?? ValidateText(finance.Note, AssistantFinanceFieldNames.Note, normalizedMessage, canInfer: true, canClear: true,
                maxLength: AssistantInterpretationProtocol.MaxNoteLength)
            ?? ValidateVerbatim(finance.Note, AssistantFinanceFieldNames.Note, message)
            ?? ValidateDate(finance.Date, normalizedMessage)
            ?? ValidateTime(finance.Time, normalizedMessage);
    }

    // The amount format of the protocol: digits, optionally a dot and 1–4 decimals.
    public static bool TryParseAmount(string? value, out decimal amount)
    {
        amount = 0;

        if (value is null || value.Length > 20 || !IsAmount(value))
        {
            return false;
        }

        return decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);
    }

    // Lowercase, typographic apostrophes as ', whitespace runs as one space: how a span is found in the
    // message regardless of case and spacing.
    public static string NormalizeForMatch(string? text) => NormalizeSpacing(text).ToLowerInvariant();

    // Typographic apostrophes as ', whitespace runs as one space, case kept: how the user's own words are
    // found in the message regardless of spacing.
    public static string NormalizeSpacing(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var builder = new StringBuilder(text.Length);
        var space = false;

        foreach (var character in text.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                space = true;
                continue;
            }

            if (space && builder.Length > 0)
            {
                builder.Append(' ');
            }

            space = false;
            builder.Append(character is '’' or '‘' or '`' ? '\'' : character);
        }

        return builder.ToString();
    }

    private static string? ValidateType(AssistantTextField? field, AssistantIntent intent, string message)
    {
        if (ValidateText(field, AssistantFinanceFieldNames.Type, message, canInfer: false, canClear: false,
                value => value is "expense" or "income") is { } problem)
        {
            return problem;
        }

        // A new expense is not "of type income".
        return (intent, field!.Value) switch
        {
            (AssistantIntent.FinanceExpense, "income") or (AssistantIntent.FinanceIncome, "expense") => "type_contradicts_intent",
            _ => null
        };
    }

    private static string? ValidateText(
        AssistantTextField? field,
        string name,
        string message,
        bool canInfer,
        bool canClear,
        Func<string, bool>? format = null,
        int maxLength = AssistantInterpretationProtocol.MaxValueLength)
    {
        if (field is null || !Enum.IsDefined(field.State))
        {
            return $"{name}_state";
        }

        switch (field.State)
        {
            case AssistantFieldState.Absent:
            case AssistantFieldState.Clear:
                if (field.State == AssistantFieldState.Clear && !canClear)
                {
                    return $"{name}_clear";
                }

                return field.Value is null && field.Span is null ? null : $"{name}_unexpected_value";

            case AssistantFieldState.Inferred when !canInfer:
                return $"{name}_inferred";
        }

        if (string.IsNullOrWhiteSpace(field.Value) || CodePoints(field.Value) > maxLength || field.Value.Contains('\0'))
        {
            return $"{name}_value";
        }

        if (format is not null && !format(field.Value))
        {
            return $"{name}_format";
        }

        return field.State == AssistantFieldState.Stated ? ValidateSpan(field.Span, name, message) : null;
    }

    // An account or a category: a stated mention is part of what its span quotes, so the model cannot name a
    // resource the user did not write.
    private static string? ValidateMention(AssistantTextField? field, string name, string message, bool canInfer) =>
        ValidateText(field, name, message, canInfer, canClear: true)
        ?? (field!.State == AssistantFieldState.Stated
            && !NormalizeForMatch(field.Span).Contains(NormalizeForMatch(field.Value), StringComparison.Ordinal)
                ? $"{name}_value_not_in_span"
                : null);

    // A stated note is the user's own words: found in the message as written, case included.
    private static string? ValidateVerbatim(AssistantTextField field, string name, string message) =>
        field.State == AssistantFieldState.Stated
        && !NormalizeSpacing(message).Contains(NormalizeSpacing(field.Value), StringComparison.Ordinal)
            ? $"{name}_not_verbatim"
            : null;

    private static string? ValidateDate(AssistantDateField? field, string message)
    {
        const string name = AssistantFinanceFieldNames.Date;

        if (field is null || !Enum.IsDefined(field.State))
        {
            return $"{name}_state";
        }

        var noParts = field is { Kind: null, DaysAgo: null, Weekday: null, Day: null, Month: null, Year: null, Span: null };

        switch (field.State)
        {
            case AssistantFieldState.Absent:
            case AssistantFieldState.Clear:
                return noParts ? null : $"{name}_unexpected_value";

            case AssistantFieldState.Inferred:
                return $"{name}_inferred";
        }

        if (field.Kind is not { } kind || !Enum.IsDefined(kind))
        {
            return $"{name}_kind";
        }

        var consistent = kind switch
        {
            AssistantDateKind.DaysAgo => field is { DaysAgo: >= 1 and <= MaxDaysAgo, Weekday: null, Day: null, Month: null, Year: null },
            AssistantDateKind.Weekday => field is { DaysAgo: null, Weekday: { } weekday, Day: null, Month: null, Year: null } && Enum.IsDefined(weekday),
            AssistantDateKind.Calendar => field is { DaysAgo: null, Weekday: null, Day: >= 1 and <= 31, Month: >= 1 and <= 12 }
                && field.Year is null or (>= 1900 and <= 2999),
            _ => field is { DaysAgo: null, Weekday: null, Day: null, Month: null, Year: null }
        };

        return consistent ? ValidateSpan(field.Span, name, message) : $"{name}_parts";
    }

    private static string? ValidateTime(AssistantTimeField? field, string message)
    {
        const string name = AssistantFinanceFieldNames.Time;

        if (field is null || !Enum.IsDefined(field.State))
        {
            return $"{name}_state";
        }

        switch (field.State)
        {
            case AssistantFieldState.Absent:
            case AssistantFieldState.Clear:
                return field is { Hour: null, Minute: null, Span: null } ? null : $"{name}_unexpected_value";

            case AssistantFieldState.Inferred:
                return $"{name}_inferred";
        }

        return field is { Hour: >= 0 and <= 23, Minute: >= 0 and <= 59 } ? ValidateSpan(field.Span, name, message) : $"{name}_parts";
    }

    // A stated value must come from the message itself.
    private static string? ValidateSpan(string? span, string name, string message)
    {
        if (string.IsNullOrWhiteSpace(span) || CodePoints(span) > AssistantInterpretationProtocol.MaxSpanLength)
        {
            return $"{name}_span";
        }

        return message.Contains(NormalizeForMatch(span), StringComparison.Ordinal) ? null : $"{name}_span_not_in_message";
    }

    private static bool IsAmount(string value)
    {
        var dot = value.IndexOf('.');
        var integer = dot < 0 ? value : value[..dot];
        var fraction = dot < 0 ? "" : value[(dot + 1)..];

        return integer.Length is >= 1 and <= 15 && integer.All(char.IsAsciiDigit)
            && (dot < 0 || (fraction.Length is >= 1 and <= 4 && fraction.All(char.IsAsciiDigit)));
    }

    private static bool IsCurrency(string value) => value.Length == 3 && value.All(char.IsAsciiLetterUpper);

    // JSON Schema and Python count code points, not UTF-16 units.
    private static int CodePoints(string text) => text.EnumerateRunes().Count();
}
