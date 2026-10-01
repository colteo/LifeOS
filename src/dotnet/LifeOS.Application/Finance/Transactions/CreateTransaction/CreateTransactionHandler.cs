using LifeOS.Application.Finance.Accounts;
using LifeOS.Application.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Application.Finance.Transactions.CreateTransaction;

public sealed class CreateTransactionHandler
{
    private readonly ITransactionRepository _transactionRepository;
    private readonly TransactionInputResolver _inputResolver;
    private readonly TimeProvider _timeProvider;

    public CreateTransactionHandler(
        IAccountRepository accountRepository,
        ICategoryRepository categoryRepository,
        ITransactionRepository transactionRepository,
        TimeProvider timeProvider)
    {
        _transactionRepository = transactionRepository;
        _inputResolver = new TransactionInputResolver(accountRepository, categoryRepository);
        _timeProvider = timeProvider;
    }

    // Every referenced account and category is looked up scoped to userId (TransactionInputResolver),
    // so another user's resource is reported as NotFound and can never be referenced.
    public Task<CreateTransactionResult> HandleAsync(
        Guid userId,
        CreateTransactionCommand command,
        CancellationToken cancellationToken) => command.TransactionType switch
        {
            TransactionType.Income or TransactionType.Expense =>
                CreateAccountTransactionAsync(userId, command, cancellationToken),
            TransactionType.Transfer =>
                CreateTransferAsync(userId, command, cancellationToken),
            _ => Task.FromResult(CreateTransactionResult.Invalid("type", "Transaction type is not supported."))
        };

    private async Task<CreateTransactionResult> CreateAccountTransactionAsync(
        Guid userId,
        CreateTransactionCommand command,
        CancellationToken cancellationToken)
    {
        var type = command.TransactionType;

        // 1. Field combination: the explicit Domain factories cannot represent other shapes.
        if (command.SourceAccountId is not null)
        {
            return CreateTransactionResult.Invalid("sourceAccountId", $"{type} transactions must not have a source account.");
        }

        if (command.DestinationAccountId is not null)
        {
            return CreateTransactionResult.Invalid("destinationAccountId", $"{type} transactions must not have a destination account.");
        }

        if (command.AccountId is not { } accountId)
        {
            return CreateTransactionResult.Invalid("accountId", $"{type} transactions require an account.");
        }

        if (command.CategoryId is not { } categoryId)
        {
            return CreateTransactionResult.Invalid("categoryId", $"{type} transactions require a category.");
        }

        // 2-4. Referenced entities and compatibility; the currency is taken from the account.
        var input = await _inputResolver.ResolveAccountTransactionAsync(
            userId, type, accountId, categoryId, TransactionInputFields.Flat, cancellationToken);

        if (input.Status != TransactionInputStatus.Resolved)
        {
            return ToResult(input);
        }

        // 5. Domain factory.
        var transaction = type == TransactionType.Income
            ? Transaction.CreateIncome(
                userId, accountId, categoryId, command.Amount, input.Currency!, command.OccurredAtUtc, command.Note, _timeProvider.GetUtcNow())
            : Transaction.CreateExpense(
                userId, accountId, categoryId, command.Amount, input.Currency!, command.OccurredAtUtc, command.Note, _timeProvider.GetUtcNow());

        // 6. Persist exactly one transaction.
        return await PersistAsync(userId, transaction, cancellationToken);
    }

    private async Task<CreateTransactionResult> CreateTransferAsync(
        Guid userId,
        CreateTransactionCommand command,
        CancellationToken cancellationToken)
    {
        // 1. Field combination.
        if (command.AccountId is not null)
        {
            return CreateTransactionResult.Invalid("accountId", "Transfers use sourceAccountId and destinationAccountId, not accountId.");
        }

        if (command.CategoryId is not null)
        {
            return CreateTransactionResult.Invalid("categoryId", "Transfers do not have a category.");
        }

        if (command.SourceAccountId is not { } sourceAccountId)
        {
            return CreateTransactionResult.Invalid("sourceAccountId", "Transfers require a source account.");
        }

        if (command.DestinationAccountId is not { } destinationAccountId)
        {
            return CreateTransactionResult.Invalid("destinationAccountId", "Transfers require a destination account.");
        }

        // 2-4. Referenced accounts; cross-currency transfers are not supported.
        var input = await _inputResolver.ResolveTransferAsync(
            userId, sourceAccountId, destinationAccountId, TransactionInputFields.Flat, cancellationToken);

        if (input.Status != TransactionInputStatus.Resolved)
        {
            return ToResult(input);
        }

        // 5. Domain factory (also rejects source == destination); currency comes from the accounts.
        var transaction = Transaction.CreateTransfer(
            userId,
            sourceAccountId,
            destinationAccountId,
            command.Amount,
            input.Currency!,
            command.OccurredAtUtc,
            command.Note,
            _timeProvider.GetUtcNow());

        // 6. One row for the whole transfer.
        return await PersistAsync(userId, transaction, cancellationToken);
    }

    private async Task<CreateTransactionResult> PersistAsync(Guid userId, Transaction transaction, CancellationToken cancellationToken)
    {
        if (!await _transactionRepository.TryAddAsync(transaction, cancellationToken))
        {
            return ToResult(await _inputResolver.ReferenceGoneAsync(userId, transaction, TransactionInputFields.Flat, cancellationToken));
        }

        return CreateTransactionResult.Created(ToCreatedTransaction(transaction));
    }

    private static CreateTransactionResult ToResult(TransactionInput input) =>
        input.Status == TransactionInputStatus.NotFound
            ? CreateTransactionResult.NotFound(input.Field!, input.Message!)
            : CreateTransactionResult.Invalid(input.Field!, input.Message!);

    private static CreatedTransaction ToCreatedTransaction(Transaction transaction) =>
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
