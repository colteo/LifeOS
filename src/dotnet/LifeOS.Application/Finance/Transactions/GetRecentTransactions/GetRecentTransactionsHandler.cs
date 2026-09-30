namespace LifeOS.Application.Finance.Transactions.GetRecentTransactions;

// The caller's most recent transactions, newest first, for overviews such as Home.
// The monthly history uses GetTransactions with an explicit range instead.
public sealed class GetRecentTransactionsHandler
{
    public const int DefaultLimit = 5;
    public const int MaxLimit = 20;

    private readonly ITransactionRepository _transactionRepository;

    public GetRecentTransactionsHandler(ITransactionRepository transactionRepository)
    {
        _transactionRepository = transactionRepository;
    }

    public async Task<GetRecentTransactionsResult> HandleAsync(
        Guid userId,
        GetRecentTransactionsQuery query,
        CancellationToken cancellationToken)
    {
        if (query.Limit is < 1 or > MaxLimit)
        {
            return GetRecentTransactionsResult.Invalid("limit", $"limit must be between 1 and {MaxLimit}.");
        }

        var transactions = await _transactionRepository.GetRecentAsync(userId, query.Limit, cancellationToken);

        return GetRecentTransactionsResult.Ok(TransactionSummary.NewestFirst(transactions));
    }
}
