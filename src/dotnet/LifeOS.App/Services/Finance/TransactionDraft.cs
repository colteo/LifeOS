using System.Globalization;
using LifeOS.Contracts.Finance.Accounts;
using LifeOS.Contracts.Finance.Transactions;

namespace LifeOS.App.Services.Finance;

// The values the transaction form builds a request from.
public sealed record TransactionValues(
    decimal Amount,
    Guid? AccountId,
    Guid? CategoryId,
    Guid? SourceAccountId,
    Guid? DestinationAccountId,
    DateTimeOffset OccurredAtUtc,
    string? Note);

// The transaction being entered or edited, as the form shows it (text and selected ids), with the
// client-side checks that turn it into a request. Shared by quick entry (new) and edit. Lightweight
// checks for usability only: the API remains authoritative. Plain .NET, no MAUI.
public sealed class TransactionDraft
{
    public const string Expense = "Expense";
    public const string Income = "Income";
    public const string Transfer = "Transfer";

    // The HTML datetime-local value format: local wall-clock time without a time zone.
    private const string DateTimeLocalFormat = "yyyy-MM-ddTHH:mm";

    // Valid datetime-local values the WebView may return: minutes, seconds, or 1-7 fractional
    // second digits. No offset or "Z": the value is local wall-clock time.
    private static readonly string[] AcceptedDateTimeLocalFormats =
    [
        "yyyy-MM-ddTHH:mm",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFF"
    ];

    // Editing: the stored instant and the minute-precision text it is shown as. While the date/time
    // text is unchanged the stored instant is sent back exactly (seconds and microseconds included).
    private DateTimeOffset? _originalOccurredAtUtc;
    private string? _originalOccurredAtText;

    private TransactionDraft(string type, string occurredAtLocalText)
    {
        Type = type;
        OccurredAtLocalText = occurredAtLocalText;
    }

    // External transaction type name, as used by the API contract.
    public string Type { get; private set; }

    public string Amount { get; set; } = string.Empty;

    public string AccountId { get; set; } = string.Empty;

    public string SourceAccountId { get; set; } = string.Empty;

    public string DestinationAccountId { get; set; } = string.Empty;

    public string CategoryId { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;

    // The single source of truth for the date/time field, bound as the raw string so the picker's
    // value is never re-parsed or reverted on render.
    public string OccurredAtLocalText { get; private set; }

    public bool IsTransfer => Type == Transfer;

    public static TransactionDraft ForNew(string type, DateTime localNow) => new(type, FormatLocal(localNow));

    // A persisted transaction, ready to edit: the amount in the given (device) culture, the occurred
    // time as local wall-clock text, the stored instant kept for an unchanged date.
    public static TransactionDraft From(TransactionResponse transaction, TimeZoneInfo timeZone, CultureInfo culture)
    {
        var text = FormatLocal(TimeZoneInfo.ConvertTime(transaction.OccurredAtUtc, timeZone).DateTime);

        return new TransactionDraft(transaction.Type, text)
        {
            Amount = transaction.Amount.ToString("0.00##", culture),
            AccountId = transaction.AccountId?.ToString() ?? string.Empty,
            CategoryId = transaction.CategoryId?.ToString() ?? string.Empty,
            SourceAccountId = transaction.SourceAccountId?.ToString() ?? string.Empty,
            DestinationAccountId = transaction.DestinationAccountId?.ToString() ?? string.Empty,
            Note = transaction.Note ?? string.Empty,
            _originalOccurredAtUtc = transaction.OccurredAtUtc,
            _originalOccurredAtText = text
        };
    }

    // Quick entry only: categories never carry over (they are specific to Income or Expense), and
    // switching to or from Transfer clears the accounts of the other shape.
    public void ChangeType(string type)
    {
        if (type == Type)
        {
            return;
        }

        var wasTransfer = IsTransfer;
        Type = type;
        CategoryId = string.Empty;

        if (IsTransfer)
        {
            AccountId = string.Empty;
        }
        else if (wasTransfer)
        {
            SourceAccountId = string.Empty;
            DestinationAccountId = string.Empty;
        }
    }

    // The destination can never be the source.
    public void ClearDestinationIfSameAsSource()
    {
        if (DestinationAccountId == SourceAccountId)
        {
            DestinationAccountId = string.Empty;
        }
    }

    // The picker's value: stored as yyyy-MM-ddTHH:mm when parsable (the UI works at minute
    // precision), anything else as received (building the request then reports it).
    public void SetOccurredAtFromPicker(string? raw)
    {
        raw ??= string.Empty;

        OccurredAtLocalText = DateTime.TryParseExact(
                raw,
                AcceptedDateTimeLocalFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed)
            ? FormatLocal(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified))
            : raw;
    }

    // Quick entry only, after a save: type and account selection(s) are kept for fast repeated
    // entry; amount, note and category are cleared; the time resets to now.
    public void ClearForNextEntry(DateTime localNow)
    {
        Amount = string.Empty;
        Note = string.Empty;
        CategoryId = string.Empty;
        OccurredAtLocalText = FormatLocal(localNow);
        _originalOccurredAtUtc = null;
        _originalOccurredAtText = null;
    }

    public AccountResponse? FindAccount(IReadOnlyList<AccountResponse> accounts, string? id) =>
        Guid.TryParse(id, out var accountId) ? accounts.FirstOrDefault(account => account.Id == accountId) : null;

    // A transfer between accounts of different currencies is not supported (the API rejects it too).
    public bool HasCurrencyMismatch(IReadOnlyList<AccountResponse> accounts) =>
        IsTransfer
        && FindAccount(accounts, SourceAccountId) is { } source
        && FindAccount(accounts, DestinationAccountId) is { } destination
        && source.Currency != destination.Currency;

    public bool TryBuild(TimeZoneInfo timeZone, out TransactionValues values, out IReadOnlyList<string> errors)
    {
        values = null!;
        var problems = new List<string>();

        // Accepts "12,50" and "12.50" (one decimal separator, no thousands separators, no sign);
        // transaction amounts must be greater than zero.
        if (!DecimalText.TryParse(Amount, allowNegative: false, out var amount) || amount <= 0)
        {
            problems.Add("Enter an amount greater than zero, e.g. 12,50.");
        }

        var occurredAtUtc = default(DateTimeOffset);

        if (_originalOccurredAtUtc is { } original && OccurredAtLocalText == _originalOccurredAtText)
        {
            occurredAtUtc = original;
        }
        else if (!TryParseForSave(OccurredAtLocalText, out var occurredAtLocal))
        {
            problems.Add("Choose a valid date and time.");
        }
        else if (!TryConvertToUtc(occurredAtLocal, timeZone, out occurredAtUtc))
        {
            problems.Add("This local time does not exist (daylight saving change). Choose another time.");
        }

        var note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim();

        if (IsTransfer)
        {
            var hasSource = Guid.TryParse(SourceAccountId, out var sourceAccountId);
            var hasDestination = Guid.TryParse(DestinationAccountId, out var destinationAccountId);

            if (!hasSource)
            {
                problems.Add("Select the account to transfer from.");
            }

            if (!hasDestination)
            {
                problems.Add("Select the account to transfer to.");
            }

            if (problems.Count == 0)
            {
                values = new TransactionValues(amount, null, null, sourceAccountId, destinationAccountId, occurredAtUtc, note);
            }
        }
        else
        {
            var hasAccount = Guid.TryParse(AccountId, out var accountId);
            var hasCategory = Guid.TryParse(CategoryId, out var categoryId);

            if (!hasAccount)
            {
                problems.Add("Select an account.");
            }

            if (!hasCategory)
            {
                problems.Add("Select a category.");
            }

            if (problems.Count == 0)
            {
                values = new TransactionValues(amount, accountId, categoryId, null, null, occurredAtUtc, note);
            }
        }

        errors = problems;
        return problems.Count == 0;
    }

    public CreateTransactionRequest ToCreateRequest(TransactionValues values) =>
        new(Type, values.Amount, values.AccountId, values.SourceAccountId, values.DestinationAccountId, values.CategoryId, values.OccurredAtUtc, values.Note);

    // Exactly one branch, matching the (immutable) type.
    public UpdateTransactionRequest ToUpdateRequest(TransactionValues values) =>
        IsTransfer
            ? new UpdateTransactionRequest(values.Amount, values.OccurredAtUtc, values.Note, null,
                new TransferUpdate(values.SourceAccountId, values.DestinationAccountId))
            : new UpdateTransactionRequest(values.Amount, values.OccurredAtUtc, values.Note,
                new AccountTransactionUpdate(values.AccountId, values.CategoryId), null);

    private static string FormatLocal(DateTime localWallClock) =>
        localWallClock.ToString(DateTimeLocalFormat, CultureInfo.InvariantCulture);

    // Save-time parsing of the stored datetime-local text. Tolerant of what a WebView may hand back
    // (surrounding whitespace, a space instead of "T", seconds, any number of fractional digits)
    // and always yields minute precision, matching the UI. Offsets and "Z" are still rejected.
    private static bool TryParseForSave(string? value, out DateTime localWallClock)
    {
        localWallClock = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();

        if (text.Length > 10 && text[10] == ' ')
        {
            text = string.Concat(text.AsSpan(0, 10), "T", text.AsSpan(11));
        }

        var fractionStart = text.IndexOf('.');

        if (fractionStart >= 0)
        {
            var fraction = text.AsSpan(fractionStart + 1);

            if (fraction.IsEmpty || fraction.IndexOfAnyExceptInRange('0', '9') >= 0)
            {
                return false;
            }

            text = text[..fractionStart];
        }

        if (!DateTime.TryParseExact(
                text,
                ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return false;
        }

        localWallClock = new DateTime(
            parsed.Year, parsed.Month, parsed.Day, parsed.Hour, parsed.Minute, 0, DateTimeKind.Unspecified);

        return true;
    }

    // datetime-local is a wall-clock time without a time zone: interpret it explicitly in the
    // device's time zone and convert it to UTC for the API.
    private static bool TryConvertToUtc(DateTime localWallClock, TimeZoneInfo timeZone, out DateTimeOffset occurredAtUtc)
    {
        occurredAtUtc = default;
        var unspecified = DateTime.SpecifyKind(localWallClock, DateTimeKind.Unspecified);

        if (timeZone.IsInvalidTime(unspecified))
        {
            return false;
        }

        occurredAtUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZone), TimeSpan.Zero);

        return true;
    }
}
