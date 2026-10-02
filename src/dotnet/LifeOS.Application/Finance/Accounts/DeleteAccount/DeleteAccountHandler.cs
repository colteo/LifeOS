using LifeOS.Application.Finance.Transactions;

namespace LifeOS.Application.Finance.Accounts.DeleteAccount;

// Deletes an account that no transaction references, together with its opening balance, if any
// (an opening balance has no meaning without its account). Accounts with transactions are kept:
// history is never modified. Deleting the last account is allowed.
public sealed class DeleteAccountHandler
{
    private readonly IAccountRepository _accountRepository;
    private readonly ITransactionRepository _transactionRepository;

    public DeleteAccountHandler(IAccountRepository accountRepository, ITransactionRepository transactionRepository)
    {
        _accountRepository = accountRepository;
        _transactionRepository = transactionRepository;
    }

    public async Task<DeleteAccountResult> HandleAsync(Guid userId, Guid accountId, CancellationToken cancellationToken)
    {
        // Scoped: another user's account is reported exactly like a missing one.
        var account = await _accountRepository.GetByIdAsync(userId, accountId, cancellationToken);

        if (account is null)
        {
            return DeleteAccountResult.NotFound;
        }

        if (await _transactionRepository.AnyReferencingAccountAsync(userId, accountId, cancellationToken))
        {
            return DeleteAccountResult.HasTransactions;
        }

        // The database is the final backstop: a transaction or opening balance created after the
        // check above makes the delete fail as a whole, and the outcome says why.
        return await _accountRepository.DeleteAsync(userId, accountId, cancellationToken) switch
        {
            AccountDeleteOutcome.Deleted => DeleteAccountResult.Deleted,
            AccountDeleteOutcome.HasReconciliations => DeleteAccountResult.HasReconciliations,
            AccountDeleteOutcome.HasTransactions => DeleteAccountResult.HasTransactions,
            AccountDeleteOutcome.Changed => DeleteAccountResult.Changed,
            _ => DeleteAccountResult.NotFound
        };
    }
}
