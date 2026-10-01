using LifeOS.Application.Finance.Accounts;
using LifeOS.Application.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Application.Finance.Transactions.UpdateTransaction;

// Edits an existing transaction: amount, account(s), category, occurred time and note. The type is
// immutable (to change it, delete the transaction and create another one). The reference rules are
// the creation rules (TransactionInputResolver). Balances and analytics are derived and simply
// reflect the stored change. Concurrent edits: the last write wins.
public sealed class UpdateTransactionHandler
{
    private readonly ITransactionRepository _transactionRepository;
    private readonly TransactionInputResolver _inputResolver;

    public UpdateTransactionHandler(
        IAccountRepository accountRepository,
        ICategoryRepository categoryRepository,
        ITransactionRepository transactionRepository)
    {
        _transactionRepository = transactionRepository;
        _inputResolver = new TransactionInputResolver(accountRepository, categoryRepository);
    }

    // Throws ArgumentException for an invalid amount or identical transfer accounts (Domain rules).
    public async Task<UpdateTransactionResult> HandleAsync(
        Guid userId,
        UpdateTransactionCommand command,
        CancellationToken cancellationToken)
    {
        // 1. Exactly one branch: which one is required depends on the stored type.
        if (command.AccountTransaction is not null && command.Transfer is not null)
        {
            return UpdateTransactionResult.Invalid("transfer", "Send either accountTransaction or transfer, not both.");
        }

        // 2. Scoped: another user's transaction is reported exactly like a missing one.
        var transaction = await _transactionRepository.GetByIdAsync(userId, command.TransactionId, cancellationToken);

        if (transaction is null)
        {
            return UpdateTransactionResult.TransactionNotFound();
        }

        // 3-4. The branch for the stored type, its references, then the Domain update.
        var result = transaction.TransactionType == TransactionType.Transfer
            ? await UpdateTransferAsync(userId, transaction, command, cancellationToken)
            : await UpdateAccountTransactionAsync(userId, transaction, command, cancellationToken);

        if (result is not null)
        {
            return result;
        }

        // 5. Conditional write on id and owner.
        return await _transactionRepository.TryUpdateAsync(transaction, cancellationToken) switch
        {
            TransactionUpdateOutcome.Updated => UpdateTransactionResult.Updated(TransactionSummary.From(transaction)),
            TransactionUpdateOutcome.NotFound => UpdateTransactionResult.TransactionNotFound(),
            _ => ToResult(await _inputResolver.ReferenceGoneAsync(userId, transaction, TransactionInputFields.Nested, cancellationToken))
        };
    }

    // Null when the transaction was updated in memory and can be stored.
    private async Task<UpdateTransactionResult?> UpdateAccountTransactionAsync(
        Guid userId,
        Transaction transaction,
        UpdateTransactionCommand command,
        CancellationToken cancellationToken)
    {
        var type = transaction.TransactionType;

        if (command.Transfer is not null)
        {
            return UpdateTransactionResult.Invalid(
                "transfer",
                $"{type} transactions are edited with accountTransaction; the type cannot be changed.");
        }

        if (command.AccountTransaction is not { } input)
        {
            return UpdateTransactionResult.Invalid("accountTransaction", $"accountTransaction is required for {type} transactions.");
        }

        if (input.AccountId is not { } accountId)
        {
            return UpdateTransactionResult.Invalid(TransactionInputFields.Nested.Account, $"{type} transactions require an account.");
        }

        if (input.CategoryId is not { } categoryId)
        {
            return UpdateTransactionResult.Invalid(TransactionInputFields.Nested.Category, $"{type} transactions require a category.");
        }

        var resolved = await _inputResolver.ResolveAccountTransactionAsync(
            userId, type, accountId, categoryId, TransactionInputFields.Nested, cancellationToken);

        if (resolved.Status != TransactionInputStatus.Resolved)
        {
            return ToResult(resolved);
        }

        transaction.UpdateAccountTransaction(accountId, categoryId, command.Amount, resolved.Currency!, command.OccurredAtUtc, command.Note);

        return null;
    }

    private async Task<UpdateTransactionResult?> UpdateTransferAsync(
        Guid userId,
        Transaction transaction,
        UpdateTransactionCommand command,
        CancellationToken cancellationToken)
    {
        if (command.AccountTransaction is not null)
        {
            return UpdateTransactionResult.Invalid(
                "accountTransaction",
                "Transfers are edited with transfer; the type cannot be changed.");
        }

        if (command.Transfer is not { } input)
        {
            return UpdateTransactionResult.Invalid("transfer", "transfer is required for Transfer transactions.");
        }

        if (input.SourceAccountId is not { } sourceAccountId)
        {
            return UpdateTransactionResult.Invalid(TransactionInputFields.Nested.SourceAccount, "Transfers require a source account.");
        }

        if (input.DestinationAccountId is not { } destinationAccountId)
        {
            return UpdateTransactionResult.Invalid(TransactionInputFields.Nested.DestinationAccount, "Transfers require a destination account.");
        }

        var resolved = await _inputResolver.ResolveTransferAsync(
            userId, sourceAccountId, destinationAccountId, TransactionInputFields.Nested, cancellationToken);

        if (resolved.Status != TransactionInputStatus.Resolved)
        {
            return ToResult(resolved);
        }

        transaction.UpdateTransfer(sourceAccountId, destinationAccountId, command.Amount, resolved.Currency!, command.OccurredAtUtc, command.Note);

        return null;
    }

    private static UpdateTransactionResult ToResult(TransactionInput input) =>
        input.Status == TransactionInputStatus.NotFound
            ? UpdateTransactionResult.NotFound(input.Field!, input.Message!)
            : UpdateTransactionResult.Invalid(input.Field!, input.Message!);
}
