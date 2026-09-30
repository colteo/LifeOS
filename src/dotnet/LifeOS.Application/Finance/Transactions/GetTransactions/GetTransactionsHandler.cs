namespace LifeOS.Application.Finance.Transactions.GetTransactions;

public sealed class GetTransactionsHandler
{
    private readonly ITransactionRepository _transactionRepository;

    public GetTransactionsHandler(ITransactionRepository transactionRepository)
    {
        _transactionRepository = transactionRepository;
    }

    public async Task<GetTransactionsResult> HandleAsync(
        Guid userId,
        GetTransactionsQuery query,
        CancellationToken cancellationToken)
    {
        if (query.FromUtc.Offset != TimeSpan.Zero)
        {
            return GetTransactionsResult.Invalid("fromUtc", "fromUtc must be a UTC value.");
        }

        if (query.ToUtc.Offset != TimeSpan.Zero)
        {
            return GetTransactionsResult.Invalid("toUtc", "toUtc must be a UTC value.");
        }

        if (query.FromUtc >= query.ToUtc)
        {
            return GetTransactionsResult.Invalid("toUtc", "toUtc must be later than fromUtc.");
        }

        var transactions = await _transactionRepository.GetByOccurredRangeAsync(
            userId,
            query.FromUtc,
            query.ToUtc,
            cancellationToken);

        // The repository orders the same way; sorting here keeps the rule guaranteed and unit-tested.
        // Guid comparison is unsigned per field, matching PostgreSQL uuid ordering.
        return GetTransactionsResult.Ok(transactions
            .OrderByDescending(transaction => transaction.OccurredAtUtc)
            .ThenByDescending(transaction => transaction.CreatedAtUtc)
            .ThenByDescending(transaction => transaction.Id)
            .Select(transaction => new TransactionSummary(
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
                transaction.CreatedAtUtc))
            .ToList());
    }
}
