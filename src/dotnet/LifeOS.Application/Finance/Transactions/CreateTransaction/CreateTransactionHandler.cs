using LifeOS.Application.Finance.Accounts;
using LifeOS.Application.Finance.Categories;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Application.Finance.Transactions.CreateTransaction;

public sealed class CreateTransactionHandler
{
    private readonly IAccountRepository _accountRepository;
    private readonly ICategoryRepository _categoryRepository;
    private readonly ITransactionRepository _transactionRepository;
    private readonly TimeProvider _timeProvider;

    public CreateTransactionHandler(
        IAccountRepository accountRepository,
        ICategoryRepository categoryRepository,
        ITransactionRepository transactionRepository,
        TimeProvider timeProvider)
    {
        _accountRepository = accountRepository;
        _categoryRepository = categoryRepository;
        _transactionRepository = transactionRepository;
        _timeProvider = timeProvider;
    }

    // Every referenced account and category is looked up scoped to userId, so another user's
    // resource is reported as NotFound and can never be referenced.
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

        // 2-3. Referenced entities.
        var account = await _accountRepository.GetByIdAsync(userId, accountId, cancellationToken);

        if (account is null)
        {
            return CreateTransactionResult.NotFound("accountId", $"Account '{accountId}' does not exist.");
        }

        var category = await _categoryRepository.GetByIdAsync(userId, categoryId, cancellationToken);

        if (category is null)
        {
            return CreateTransactionResult.NotFound("categoryId", $"Category '{categoryId}' does not exist.");
        }

        // 4. Compatibility. Top-level categories and subcategories are both valid.
        var requiredCategoryType = type == TransactionType.Income ? CategoryType.Income : CategoryType.Expense;

        if (category.CategoryType != requiredCategoryType)
        {
            return CreateTransactionResult.Invalid("categoryId", $"{type} transactions require a {requiredCategoryType} category.");
        }

        // 5. Domain factory; the currency is taken from the account.
        var transaction = type == TransactionType.Income
            ? Transaction.CreateIncome(
                userId, accountId, categoryId, command.Amount, account.Currency, command.OccurredAtUtc, command.Note, _timeProvider.GetUtcNow())
            : Transaction.CreateExpense(
                userId, accountId, categoryId, command.Amount, account.Currency, command.OccurredAtUtc, command.Note, _timeProvider.GetUtcNow());

        // 6. Persist exactly one transaction.
        await _transactionRepository.AddAsync(transaction, cancellationToken);

        return CreateTransactionResult.Created(ToCreatedTransaction(transaction));
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

        // 2. Referenced accounts.
        var sourceAccount = await _accountRepository.GetByIdAsync(userId, sourceAccountId, cancellationToken);

        if (sourceAccount is null)
        {
            return CreateTransactionResult.NotFound("sourceAccountId", $"Account '{sourceAccountId}' does not exist.");
        }

        var destinationAccount = await _accountRepository.GetByIdAsync(userId, destinationAccountId, cancellationToken);

        if (destinationAccount is null)
        {
            return CreateTransactionResult.NotFound("destinationAccountId", $"Account '{destinationAccountId}' does not exist.");
        }

        // 4. Compatibility: cross-currency transfers are not supported.
        if (sourceAccount.Currency != destinationAccount.Currency)
        {
            return CreateTransactionResult.Invalid(
                "destinationAccountId",
                "Transfers between accounts with different currencies are not supported.");
        }

        // 5. Domain factory (also rejects source == destination); currency comes from the accounts.
        var transaction = Transaction.CreateTransfer(
            userId,
            sourceAccountId,
            destinationAccountId,
            command.Amount,
            sourceAccount.Currency,
            command.OccurredAtUtc,
            command.Note,
            _timeProvider.GetUtcNow());

        // 6. One row for the whole transfer.
        await _transactionRepository.AddAsync(transaction, cancellationToken);

        return CreateTransactionResult.Created(ToCreatedTransaction(transaction));
    }

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
