namespace LifeOS.Application.Finance.Transactions.GetTransaction;

public sealed class GetTransactionHandler
{
    private readonly ITransactionRepository _transactionRepository;

    public GetTransactionHandler(ITransactionRepository transactionRepository)
    {
        _transactionRepository = transactionRepository;
    }

    // Null for a missing transaction, and equally for another user's.
    public async Task<TransactionSummary?> HandleAsync(Guid userId, Guid transactionId, CancellationToken cancellationToken)
    {
        var transaction = await _transactionRepository.GetByIdAsync(userId, transactionId, cancellationToken);

        return transaction is null ? null : TransactionSummary.From(transaction);
    }
}
