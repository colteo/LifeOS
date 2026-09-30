namespace LifeOS.Application.Finance.Transactions.GetTransactions;

// Half-open range of UTC instants: FromUtc inclusive, ToUtc exclusive.
// The caller converts local calendar periods to UTC; the backend knows no time zone.
public sealed record GetTransactionsQuery(DateTimeOffset FromUtc, DateTimeOffset ToUtc);
