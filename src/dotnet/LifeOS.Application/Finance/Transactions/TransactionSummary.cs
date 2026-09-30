using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Application.Finance.Transactions;

// A transaction as returned by the read use cases (history and recent transactions).
public sealed record TransactionSummary(
    Guid Id,
    TransactionType TransactionType,
    decimal Amount,
    string Currency,
    Guid? AccountId,
    Guid? SourceAccountId,
    Guid? DestinationAccountId,
    Guid? CategoryId,
    string? Note,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset CreatedAtUtc)
{
    // Newest first: OccurredAtUtc, then CreatedAtUtc, then Id, all descending. Repositories order
    // the same way; applying it here keeps the rule guaranteed and unit-tested.
    // Guid comparison is unsigned per field, matching PostgreSQL uuid ordering.
    public static IReadOnlyList<TransactionSummary> NewestFirst(IEnumerable<Transaction> transactions) =>
        transactions
            .OrderByDescending(transaction => transaction.OccurredAtUtc)
            .ThenByDescending(transaction => transaction.CreatedAtUtc)
            .ThenByDescending(transaction => transaction.Id)
            .Select(From)
            .ToList();

    public static TransactionSummary From(Transaction transaction) =>
        new(
            transaction.Id,
            transaction.TransactionType,
            transaction.Amount,
            transaction.Currency,
            transaction.AccountId,
            transaction.SourceAccountId,
            transaction.DestinationAccountId,
            transaction.CategoryId,
            transaction.Note,
            transaction.OccurredAtUtc,
            transaction.CreatedAtUtc);
}
