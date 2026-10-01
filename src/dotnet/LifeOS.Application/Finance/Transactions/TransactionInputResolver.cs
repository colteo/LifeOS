using LifeOS.Application.Finance.Accounts;
using LifeOS.Application.Finance.Categories;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Application.Finance.Transactions;

// The request field names errors are reported on: flat for creation, nested for updates.
public sealed record TransactionInputFields(string Account, string Category, string SourceAccount, string DestinationAccount)
{
    public static readonly TransactionInputFields Flat =
        new("accountId", "categoryId", "sourceAccountId", "destinationAccountId");

    public static readonly TransactionInputFields Nested =
        new("accountTransaction.accountId", "accountTransaction.categoryId", "transfer.sourceAccountId", "transfer.destinationAccountId");
}

public enum TransactionInputStatus
{
    Resolved,
    Invalid,
    NotFound
}

// Resolved: the currency the transaction takes from its account(s). Otherwise the field and message.
public sealed record TransactionInput(TransactionInputStatus Status, string? Currency, string? Field, string? Message)
{
    public static TransactionInput Resolved(string currency) => new(TransactionInputStatus.Resolved, currency, null, null);

    public static TransactionInput Invalid(string field, string message) => new(TransactionInputStatus.Invalid, null, field, message);

    public static TransactionInput NotFound(string field, string message) => new(TransactionInputStatus.NotFound, null, field, message);
}

// The reference rules shared by creating and editing a transaction, so they exist once:
//   - every account and category is looked up scoped to userId, so another user's resource is
//     reported as not found and can never be referenced;
//   - Income/Expense need a category of the same type (top-level or subcategory);
//   - the currency is the account's; a transfer's two accounts must share it.
public sealed class TransactionInputResolver
{
    private readonly IAccountRepository _accountRepository;
    private readonly ICategoryRepository _categoryRepository;

    public TransactionInputResolver(IAccountRepository accountRepository, ICategoryRepository categoryRepository)
    {
        _accountRepository = accountRepository;
        _categoryRepository = categoryRepository;
    }

    public async Task<TransactionInput> ResolveAccountTransactionAsync(
        Guid userId,
        TransactionType type,
        Guid accountId,
        Guid categoryId,
        TransactionInputFields fields,
        CancellationToken cancellationToken)
    {
        var account = await _accountRepository.GetByIdAsync(userId, accountId, cancellationToken);

        if (account is null)
        {
            return TransactionInput.NotFound(fields.Account, $"Account '{accountId}' does not exist.");
        }

        var category = await _categoryRepository.GetByIdAsync(userId, categoryId, cancellationToken);

        if (category is null)
        {
            return TransactionInput.NotFound(fields.Category, $"Category '{categoryId}' does not exist.");
        }

        var requiredCategoryType = type == TransactionType.Income ? CategoryType.Income : CategoryType.Expense;

        if (category.CategoryType != requiredCategoryType)
        {
            return TransactionInput.Invalid(fields.Category, $"{type} transactions require a {requiredCategoryType} category.");
        }

        return TransactionInput.Resolved(account.Currency);
    }

    public async Task<TransactionInput> ResolveTransferAsync(
        Guid userId,
        Guid sourceAccountId,
        Guid destinationAccountId,
        TransactionInputFields fields,
        CancellationToken cancellationToken)
    {
        var sourceAccount = await _accountRepository.GetByIdAsync(userId, sourceAccountId, cancellationToken);

        if (sourceAccount is null)
        {
            return TransactionInput.NotFound(fields.SourceAccount, $"Account '{sourceAccountId}' does not exist.");
        }

        var destinationAccount = await _accountRepository.GetByIdAsync(userId, destinationAccountId, cancellationToken);

        if (destinationAccount is null)
        {
            return TransactionInput.NotFound(fields.DestinationAccount, $"Account '{destinationAccountId}' does not exist.");
        }

        // Cross-currency transfers are not supported.
        if (sourceAccount.Currency != destinationAccount.Currency)
        {
            return TransactionInput.Invalid(
                fields.DestinationAccount,
                "Transfers between accounts with different currencies are not supported.");
        }

        return TransactionInput.Resolved(sourceAccount.Currency);
    }

    // The write lost a race with the deletion of a referenced account or category (the lookups found
    // them, the database no longer did). Re-reads each reference once and reports the missing one.
    public async Task<TransactionInput> ReferenceGoneAsync(Guid userId, Transaction transaction, TransactionInputFields fields, CancellationToken cancellationToken)
    {
        (string Field, Guid? Id)[] accounts =
        [
            (fields.Account, transaction.AccountId),
            (fields.SourceAccount, transaction.SourceAccountId),
            (fields.DestinationAccount, transaction.DestinationAccountId)
        ];

        foreach (var (field, id) in accounts)
        {
            if (id is { } accountId && await _accountRepository.GetByIdAsync(userId, accountId, cancellationToken) is null)
            {
                return TransactionInput.NotFound(field, $"Account '{accountId}' does not exist.");
            }
        }

        if (transaction.CategoryId is { } categoryId
            && await _categoryRepository.GetByIdAsync(userId, categoryId, cancellationToken) is null)
        {
            return TransactionInput.NotFound(fields.Category, $"Category '{categoryId}' does not exist.");
        }

        // Deleted rows never reappear, so one of the checks above normally reports it.
        return TransactionInput.NotFound("request", "A referenced account or category no longer exists.");
    }
}
