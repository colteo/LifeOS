namespace LifeOS.Application.Finance.Transactions.DeleteTransaction;

public enum DeleteTransactionResult
{
    Deleted,

    // Missing, already deleted, or another user's transaction.
    NotFound
}

// Deletes one transaction. Nothing references a transaction, so there is nothing to block it:
// balances and analytics are derived and simply stop including it.
public sealed class DeleteTransactionHandler
{
    private readonly ITransactionRepository _transactionRepository;

    public DeleteTransactionHandler(ITransactionRepository transactionRepository)
    {
        _transactionRepository = transactionRepository;
    }

    public async Task<DeleteTransactionResult> HandleAsync(Guid userId, Guid transactionId, CancellationToken cancellationToken) =>
        await _transactionRepository.DeleteAsync(userId, transactionId, cancellationToken)
            ? DeleteTransactionResult.Deleted
            : DeleteTransactionResult.NotFound;
}
