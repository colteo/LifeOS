using LifeOS.Domain.Assistant;
using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Domain.Finance.Assistant;

// Who chose the draft's category: the user, or a suggestion (F3: the interpreter's guess, shown as a
// proposal the user can change; a user choice is never replaced by a later suggestion).
public enum FinanceDraftCategorySource
{
    User,
    Suggested
}

// Whether the local date was stated, or is today by default (AI-007 F2 B.1).
public enum FinanceDraftDateSource
{
    Explicit,
    Today
}

// Whether the local time was stated, or is a convention: the current time for today (B.2), 12:00 for any
// other date (B.3, P-6). Conventions are shown in the draft and can be replaced by an explicit time.
public enum FinanceDraftTimeSource
{
    Explicit,
    CurrentTime,
    Noon
}

// The field names the draft reports as missing or invalid (also the HTTP field names).
public static class FinanceDraftFields
{
    public const string Type = "type";
    public const string Amount = "amount";
    public const string Account = "accountId";
    public const string Category = "categoryId";
    public const string CategorySource = "categorySource";
    public const string Note = "note";
    public const string Currency = "currency";
    public const string OccurredOn = "occurredOn";
    public const string OccurredTime = "occurredTime";

    // The resolved instant: missing until a date can be resolved in the user's valid time zone.
    public const string OccurredAt = "occurredAt";
}

// When the transaction happened: the user's local date and time, how each was determined, the time zone
// they were read in, and the resulting UTC instant (computed by the Application layer, with the LifeOS DST
// rules). Fixed once resolved: the draft shows exactly the instant a confirmation will record.
public sealed record FinanceDraftOccurrence
{
    public const int MaxTimeZoneIdLength = 64;

    public static readonly TimeOnly Noon = new(12, 0);

    private FinanceDraftOccurrence() { }

    public DateOnly LocalDate { get; private init; }

    public TimeOnly LocalTime { get; private init; }

    public FinanceDraftDateSource DateSource { get; private init; }

    public FinanceDraftTimeSource TimeSource { get; private init; }

    public string TimeZoneId { get; private init; } = "";

    public DateTimeOffset OccurredAtUtc { get; private init; }

    public static FinanceDraftOccurrence Create(
        DateOnly localDate,
        TimeOnly localTime,
        FinanceDraftDateSource dateSource,
        FinanceDraftTimeSource timeSource,
        string timeZoneId,
        DateTimeOffset occurredAtUtc)
    {
        if (!Enum.IsDefined(dateSource) || !Enum.IsDefined(timeSource))
        {
            throw new ArgumentException("Unknown date or time source.", FinanceDraftFields.OccurredAt);
        }

        if (string.IsNullOrWhiteSpace(timeZoneId) || timeZoneId.Length > MaxTimeZoneIdLength)
        {
            throw new ArgumentException("A time zone id is required.", FinanceDraftFields.OccurredAt);
        }

        FinanceTransactionDraft.EnsureDateInRange(localDate, FinanceDraftFields.OccurredOn);
        FinanceTransactionDraft.EnsureWholeSeconds(localTime, FinanceDraftFields.OccurredTime);

        if (timeSource == FinanceDraftTimeSource.Noon && localTime != Noon)
        {
            throw new ArgumentException("The conventional time is 12:00.", FinanceDraftFields.OccurredTime);
        }

        return new FinanceDraftOccurrence
        {
            LocalDate = localDate,
            LocalTime = localTime,
            DateSource = dateSource,
            TimeSource = timeSource,
            TimeZoneId = timeZoneId,
            OccurredAtUtc = AssistantConversation.ToStoredPrecision(occurredAtUtc)
        };
    }
}

// AI-007 F2: the typed draft of one Expense or Income (kind finance.transaction.create). Every field may be
// missing while the user is still completing it; what is present is always valid in shape:
//   - type: Expense or Income (transfers are not supported by the assistant in v1);
//   - amount: positive, at most Transaction.MaxAmount, at most Transaction.MaxDecimalPlaces decimals;
//   - currency: only the currency the user explicitly stated (ISO-style code), never a default — the
//     transaction always takes the account's currency, and an account in another currency is refused;
//   - account and category: ids of the user's own resources (ownership, existence and compatibility are
//     checked by the Application layer against Finance, and again by CreateTransactionHandler on confirm);
//     the account is only ever an explicit choice, never a default;
//   - note: free text (people's names stay here, e.g. "colazione con Pamela"), trimmed, ≤ MaxNoteLength;
//   - requested date/time: what the user explicitly stated; the occurrence: the resolved instant.
//
// Nothing else of Finance is copied here (no account or category names, no currency of the account): the
// draft references Finance, it does not duplicate it. Instances are immutable; equality is by value, which
// is how a revision that changes nothing is recognised.
public sealed record FinanceTransactionDraft : IAssistantDraft
{
    // AI-007 F2 decision: Finance stores notes as unbounded text; a draft note is capped at the assistant's
    // user message length, so a draft never holds more text than a message could carry.
    public const int MaxNoteLength = AssistantMessage.MaxUserContentLength;

    // The schema version of the stored draft document (AssistantOperation.PayloadVersion).
    public const int PayloadVersion = 1;

    public static readonly DateOnly MinOccurredOn = new(1900, 1, 1);
    public static readonly DateOnly MaxOccurredOn = new(2999, 12, 31);

    private const int CurrencyCodeLength = 3;

    private FinanceTransactionDraft() { }

    public TransactionType? Type { get; private init; }

    public decimal? Amount { get; private init; }

    // The explicitly stated currency, if any.
    public string? CurrencyMention { get; private init; }

    public Guid? AccountId { get; private init; }

    public Guid? CategoryId { get; private init; }

    // Set exactly when CategoryId is.
    public FinanceDraftCategorySource? CategorySource { get; private init; }

    public string? Note { get; private init; }

    // The explicitly stated local date and time; null when not stated.
    public DateOnly? RequestedDate { get; private init; }

    public TimeOnly? RequestedTime { get; private init; }

    // Null while no valid time zone was available to resolve it.
    public FinanceDraftOccurrence? Occurrence { get; private init; }

    public static FinanceTransactionDraft Empty { get; } = new();

    // The fields a confirmation still needs, in display order.
    public IReadOnlyList<string> MissingFields
    {
        get
        {
            var missing = new List<string>(5);

            if (Type is null) missing.Add(FinanceDraftFields.Type);
            if (Amount is null) missing.Add(FinanceDraftFields.Amount);
            if (AccountId is null) missing.Add(FinanceDraftFields.Account);
            if (CategoryId is null) missing.Add(FinanceDraftFields.Category);
            if (Occurrence is null) missing.Add(FinanceDraftFields.OccurredAt);

            return missing;
        }
    }

    public bool IsComplete => Type is not null && Amount is not null && AccountId is not null && CategoryId is not null && Occurrence is not null;

    // Validates and normalises every field (ArgumentException with the field name as ParamName).
    public static FinanceTransactionDraft Create(
        TransactionType? type,
        decimal? amount,
        string? currencyMention,
        Guid? accountId,
        Guid? categoryId,
        FinanceDraftCategorySource? categorySource,
        string? note,
        DateOnly? requestedDate,
        TimeOnly? requestedTime,
        FinanceDraftOccurrence? occurrence)
    {
        if (type is { } t && t is not (TransactionType.Expense or TransactionType.Income))
        {
            throw new ArgumentException("The assistant records only an Expense or an Income.", FinanceDraftFields.Type);
        }

        if (amount is { } value)
        {
            ValidateAmount(value);
        }

        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("A valid account id is required.", FinanceDraftFields.Account);
        }

        if (categoryId == Guid.Empty)
        {
            throw new ArgumentException("A valid category id is required.", FinanceDraftFields.Category);
        }

        if (categoryId.HasValue != categorySource.HasValue || (categorySource is { } source && !Enum.IsDefined(source)))
        {
            throw new ArgumentException("A category carries its source (User or Suggested), and only a category does.", FinanceDraftFields.CategorySource);
        }

        if (requestedDate is { } date)
        {
            EnsureDateInRange(date, FinanceDraftFields.OccurredOn);
        }

        if (requestedTime is { } time)
        {
            EnsureWholeSeconds(time, FinanceDraftFields.OccurredTime);
        }

        if (occurrence is not null)
        {
            EnsureConsistent(occurrence, requestedDate, requestedTime);
        }

        return new FinanceTransactionDraft
        {
            Type = type,
            Amount = amount,
            CurrencyMention = NormalizeCurrency(currencyMention),
            AccountId = accountId,
            CategoryId = categoryId,
            CategorySource = categorySource,
            Note = NormalizeNote(note),
            RequestedDate = requestedDate,
            RequestedTime = requestedTime,
            Occurrence = occurrence
        };
    }

    internal static void EnsureDateInRange(DateOnly date, string field)
    {
        if (date < MinOccurredOn || date > MaxOccurredOn)
        {
            throw new ArgumentOutOfRangeException(field, date, $"The date must be between {MinOccurredOn:yyyy-MM-dd} and {MaxOccurredOn:yyyy-MM-dd}.");
        }
    }

    internal static void EnsureWholeSeconds(TimeOnly time, string field)
    {
        if (time.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentException("The time must be in whole seconds.", field);
        }
    }

    // The same limits as the Transaction factories, checked here so a draft never holds an amount a
    // confirmation would reject.
    private static void ValidateAmount(decimal amount)
    {
        if (amount <= 0 || amount > Transaction.MaxAmount)
        {
            throw new ArgumentOutOfRangeException(FinanceDraftFields.Amount, amount, $"Amount must be greater than 0 and at most {Transaction.MaxAmount}.");
        }

        if (decimal.Round(amount, Transaction.MaxDecimalPlaces) != amount)
        {
            throw new ArgumentException($"Amount must have at most {Transaction.MaxDecimalPlaces} decimal places.", FinanceDraftFields.Amount);
        }
    }

    // An explicit date/time is the occurrence's; a missing one is a convention (today; current time or noon).
    private static void EnsureConsistent(FinanceDraftOccurrence occurrence, DateOnly? requestedDate, TimeOnly? requestedTime)
    {
        var dateMatches = requestedDate is { } date
            ? occurrence.DateSource == FinanceDraftDateSource.Explicit && occurrence.LocalDate == date
            : occurrence.DateSource == FinanceDraftDateSource.Today;

        var timeMatches = requestedTime is { } time
            ? occurrence.TimeSource == FinanceDraftTimeSource.Explicit && occurrence.LocalTime == time
            : occurrence.TimeSource != FinanceDraftTimeSource.Explicit;

        if (!dateMatches || !timeMatches)
        {
            throw new ArgumentException("The resolved date and time do not match the requested ones.", FinanceDraftFields.OccurredAt);
        }
    }

    private static string? NormalizeCurrency(string? currency)
    {
        if (currency is null)
        {
            return null;
        }

        var normalized = currency.Trim().ToUpperInvariant();

        if (normalized.Length != CurrencyCodeLength || !normalized.All(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException("Currency must be a 3-letter ISO-style code.", FinanceDraftFields.Currency);
        }

        return normalized;
    }

    // Blank → no note. NUL is rejected (PostgreSQL text and jsonb cannot store it).
    private static string? NormalizeNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return null;
        }

        var text = note.Trim();

        if (text.Length > MaxNoteLength)
        {
            throw new ArgumentException($"A note has at most {MaxNoteLength} characters.", FinanceDraftFields.Note);
        }

        if (text.Contains('\0'))
        {
            throw new ArgumentException("A note cannot contain NUL characters.", FinanceDraftFields.Note);
        }

        return text;
    }
}
