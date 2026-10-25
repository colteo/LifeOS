using System.Globalization;
using System.Text.Json;
using LifeOS.Domain.Finance.Assistant;
using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Infrastructure.Finance.Assistant;

// The jsonb document of a finance.transaction.create draft (AI-007 §9.4). Version 1:
//
//   { "type": "Expense"|"Income"|null, "amount": "5.00"|null, "currency": "EUR"|null,
//     "accountId": uuid|null, "categoryId": uuid|null, "categorySource": "User"|"Suggested"|null,
//     "note": string|null, "requestedDate": "2026-10-23"|null, "requestedTime": "08:30:00"|null,
//     "occurrence": { "localDate", "localTime", "dateSource", "timeSource", "timeZoneId", "occurredAtUtc" }|null }
//
// Amounts are invariant-culture decimal strings (never JSON numbers, so no float rounding), enums are their
// names, dates/times ISO. The stored payload_version selects the reader; an unknown version is an error,
// never a guess. Reading goes through FinanceTransactionDraft.Create, so a stored draft is validated again.
internal static class FinanceTransactionDraftJson
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string TimeFormat = "HH:mm:ss";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false
    };

    public static string Serialize(int payloadVersion, FinanceTransactionDraft draft) => payloadVersion switch
    {
        1 => JsonSerializer.Serialize(ToDocument(draft), Options),
        _ => throw new NotSupportedException($"Finance draft payload version {payloadVersion} cannot be written.")
    };

    public static FinanceTransactionDraft Deserialize(int payloadVersion, string json) => payloadVersion switch
    {
        1 => FromDocument(JsonSerializer.Deserialize<DraftV1>(json, Options) ?? throw new InvalidOperationException("The finance draft is empty.")),
        _ => throw new NotSupportedException($"Finance draft payload version {payloadVersion} is not supported.")
    };

    private static DraftV1 ToDocument(FinanceTransactionDraft draft) => new(
        draft.Type?.ToString(),
        draft.Amount?.ToString(CultureInfo.InvariantCulture),
        draft.CurrencyMention,
        draft.AccountId,
        draft.CategoryId,
        draft.CategorySource?.ToString(),
        draft.Note,
        draft.RequestedDate?.ToString(DateFormat, CultureInfo.InvariantCulture),
        draft.RequestedTime?.ToString(TimeFormat, CultureInfo.InvariantCulture),
        draft.Occurrence is { } occurrence
            ? new OccurrenceV1(
                occurrence.LocalDate.ToString(DateFormat, CultureInfo.InvariantCulture),
                occurrence.LocalTime.ToString(TimeFormat, CultureInfo.InvariantCulture),
                occurrence.DateSource.ToString(),
                occurrence.TimeSource.ToString(),
                occurrence.TimeZoneId,
                occurrence.OccurredAtUtc)
            : null);

    private static FinanceTransactionDraft FromDocument(DraftV1 document) => FinanceTransactionDraft.Create(
        document.Type is { } type ? Enum.Parse<TransactionType>(type) : null,
        document.Amount is { } amount ? decimal.Parse(amount, NumberStyles.Number, CultureInfo.InvariantCulture) : null,
        document.Currency,
        document.AccountId,
        document.CategoryId,
        document.CategorySource is { } source ? Enum.Parse<FinanceDraftCategorySource>(source) : null,
        document.Note,
        document.RequestedDate is { } date ? ParseDate(date) : null,
        document.RequestedTime is { } time ? ParseTime(time) : null,
        document.Occurrence is { } occurrence
            ? FinanceDraftOccurrence.Create(
                ParseDate(occurrence.LocalDate),
                ParseTime(occurrence.LocalTime),
                Enum.Parse<FinanceDraftDateSource>(occurrence.DateSource),
                Enum.Parse<FinanceDraftTimeSource>(occurrence.TimeSource),
                occurrence.TimeZoneId,
                occurrence.OccurredAtUtc)
            : null);

    private static DateOnly ParseDate(string value) => DateOnly.ParseExact(value, DateFormat, CultureInfo.InvariantCulture);

    private static TimeOnly ParseTime(string value) => TimeOnly.ParseExact(value, TimeFormat, CultureInfo.InvariantCulture);

    private sealed record DraftV1(
        string? Type,
        string? Amount,
        string? Currency,
        Guid? AccountId,
        Guid? CategoryId,
        string? CategorySource,
        string? Note,
        string? RequestedDate,
        string? RequestedTime,
        OccurrenceV1? Occurrence);

    private sealed record OccurrenceV1(
        string LocalDate,
        string LocalTime,
        string DateSource,
        string TimeSource,
        string TimeZoneId,
        DateTimeOffset OccurredAtUtc);
}
