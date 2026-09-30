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

        return GetTransactionsResult.Ok(TransactionSummary.NewestFirst(transactions));
    }
}
