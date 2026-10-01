using LifeOS.Application.Finance.Transactions;
using LifeOS.Application.Finance.Transactions.DeleteTransaction;
using LifeOS.Application.Finance.Transactions.GetTransaction;
using LifeOS.Application.Finance.Transactions.UpdateTransaction;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Transactions;

public class TransactionManagementHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset OccurredAt = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryAccountRepository _accounts = new();
    private readonly InMemoryCategoryRepository _categories = new();
    private readonly InMemoryTransactionRepository _transactions = new();

    private readonly Account _checking;
    private readonly Account _savings;
    private readonly Account _dollars;
    private readonly Account _accountOfB;
    private readonly Category _groceries;
    private readonly Category _eatingOut;
    private readonly Category _salary;
    private readonly Category _categoryOfB;

    public TransactionManagementHandlersTests()
    {
        _checking = AddAccount(TestUsers.A, "Checking", "EUR");
        _savings = AddAccount(TestUsers.A, "Savings", "EUR");
        _dollars = AddAccount(TestUsers.A, "Dollars", "USD");
        _accountOfB = AddAccount(TestUsers.B, "B", "EUR");
        var food = AddCategory(TestUsers.A, "Food & Drink", CategoryType.Expense);
        _groceries = AddCategory(TestUsers.A, "Groceries", CategoryType.Expense, food);
        _eatingOut = AddCategory(TestUsers.A, "Eating out", CategoryType.Expense, food);
        _salary = AddCategory(TestUsers.A, "Salary", CategoryType.Income);
        _categoryOfB = AddCategory(TestUsers.B, "B", CategoryType.Expense);
    }

    // ---- Get ----

    [Fact]
    public async Task Get_OwnTransaction_ReturnsIt()
    {
        var expense = AddExpense(_checking, _groceries, 18.5m, "Lunch");

        var summary = await GetAsync(TestUsers.A, expense.Id);

        Assert.NotNull(summary);
        Assert.Equal((expense.Id, TransactionType.Expense, 18.5m, "Lunch"), (summary.Id, summary.TransactionType, summary.Amount, summary.Note));
    }

    [Fact]
    public async Task Get_MissingOrAnotherUsersTransaction_IsNull()
    {
        var expense = AddExpense(_checking, _groceries, 18.5m);

        Assert.Null(await GetAsync(TestUsers.B, expense.Id));
        Assert.Null(await GetAsync(TestUsers.A, Guid.CreateVersion7()));
    }

    // ---- Update: success ----

    [Fact]
    public async Task Update_Expense_ChangesAmountAccountCategoryDateAndNote_TakingTheNewAccountsCurrency()
    {
        var expense = AddExpense(_checking, _groceries, 50m, "Lunch");
        var newTime = OccurredAt.AddDays(-20);

        var result = await UpdateAsync(TestUsers.A, Command(expense.Id, 20m, newTime, "  Dinner ", account: (_dollars.Id, _eatingOut.Id)));

        Assert.Equal(UpdateTransactionStatus.Updated, result.Status);
        var stored = _transactions.Stored(expense.Id);
        Assert.Equal((TransactionType.Expense, 20m, "USD"), (stored.TransactionType, stored.Amount, stored.Currency));
        Assert.Equal((_dollars.Id, _eatingOut.Id), (stored.AccountId!.Value, stored.CategoryId!.Value));
        Assert.Equal((newTime, "Dinner"), (stored.OccurredAtUtc, stored.Note));
        Assert.Equal(expense.CreatedAtUtc, stored.CreatedAtUtc);
        Assert.Equal(20m, result.Transaction!.Amount);
    }

    [Fact]
    public async Task Update_Income_UsesAnIncomeCategory()
    {
        var income = AddIncome(_checking, 1500m);

        var result = await UpdateAsync(TestUsers.A, Command(income.Id, 1600m, OccurredAt, null, account: (_savings.Id, _salary.Id)));

        Assert.Equal(UpdateTransactionStatus.Updated, result.Status);
        Assert.Equal((TransactionType.Income, 1600m, _savings.Id), (_transactions.Stored(income.Id).TransactionType, _transactions.Stored(income.Id).Amount, _transactions.Stored(income.Id).AccountId!.Value));
    }

    [Fact]
    public async Task Update_Transfer_ChangesAmountAndEndpoints()
    {
        var transfer = AddTransfer(_checking, _savings, 100m);
        var cash = AddAccount(TestUsers.A, "Cash", "EUR");

        var result = await UpdateAsync(TestUsers.A, Command(transfer.Id, 75m, OccurredAt, "Move", transfer: (_savings.Id, cash.Id)));

        Assert.Equal(UpdateTransactionStatus.Updated, result.Status);
        var stored = _transactions.Stored(transfer.Id);
        Assert.Equal((TransactionType.Transfer, 75m, _savings.Id, cash.Id), (stored.TransactionType, stored.Amount, stored.SourceAccountId!.Value, stored.DestinationAccountId!.Value));
        Assert.Null(stored.CategoryId);
    }

    // ---- Update: request shape ----

    [Fact]
    public async Task Update_WithBothBranches_IsInvalid()
    {
        var expense = AddExpense(_checking, _groceries, 50m);

        var result = await UpdateAsync(TestUsers.A, Command(expense.Id, 5m, OccurredAt, null, account: (_checking.Id, _groceries.Id), transfer: (_checking.Id, _savings.Id)));

        AssertInvalid(result, "transfer", expense);
    }

    [Theory]
    [InlineData("transfer-branch", "transfer")]
    [InlineData("no-branch", "accountTransaction")]
    [InlineData("no-account", "accountTransaction.accountId")]
    [InlineData("no-category", "accountTransaction.categoryId")]
    [InlineData("income-category", "accountTransaction.categoryId")]
    public async Task Update_Expense_WithAWrongShape_IsInvalidForTheField(string variant, string field)
    {
        var expense = AddExpense(_checking, _groceries, 50m);
        var command = variant switch
        {
            "transfer-branch" => Command(expense.Id, 5m, OccurredAt, null, transfer: (_checking.Id, _savings.Id)),
            "no-branch" => Command(expense.Id, 5m, OccurredAt, null),
            "no-account" => Command(expense.Id, 5m, OccurredAt, null) with { AccountTransaction = new(null, _groceries.Id) },
            "no-category" => Command(expense.Id, 5m, OccurredAt, null) with { AccountTransaction = new(_checking.Id, null) },
            _ => Command(expense.Id, 5m, OccurredAt, null, account: (_checking.Id, _salary.Id))
        };

        AssertInvalid(await UpdateAsync(TestUsers.A, command), field, expense);
    }

    [Theory]
    [InlineData("account-branch", "accountTransaction")]
    [InlineData("no-branch", "transfer")]
    [InlineData("no-source", "transfer.sourceAccountId")]
    [InlineData("no-destination", "transfer.destinationAccountId")]
    [InlineData("currency-mismatch", "transfer.destinationAccountId")]
    public async Task Update_Transfer_WithAWrongShape_IsInvalidForTheField(string variant, string field)
    {
        var transfer = AddTransfer(_checking, _savings, 100m);
        var command = variant switch
        {
            "account-branch" => Command(transfer.Id, 5m, OccurredAt, null, account: (_checking.Id, _groceries.Id)),
            "no-branch" => Command(transfer.Id, 5m, OccurredAt, null),
            "no-source" => Command(transfer.Id, 5m, OccurredAt, null) with { Transfer = new(null, _savings.Id) },
            "no-destination" => Command(transfer.Id, 5m, OccurredAt, null) with { Transfer = new(_checking.Id, null) },
            _ => Command(transfer.Id, 5m, OccurredAt, null, transfer: (_checking.Id, _dollars.Id))
        };

        AssertInvalid(await UpdateAsync(TestUsers.A, command), field, transfer);
    }

    [Fact]
    public async Task Update_WithAnInvalidAmountOrTheSameTransferAccount_ThrowsAndStoresNothing()
    {
        var expense = AddExpense(_checking, _groceries, 50m);
        var transfer = AddTransfer(_checking, _savings, 100m);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            UpdateAsync(TestUsers.A, Command(expense.Id, 0m, OccurredAt, null, account: (_checking.Id, _groceries.Id))));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            UpdateAsync(TestUsers.A, Command(transfer.Id, 5m, OccurredAt, null, transfer: (_checking.Id, _checking.Id))));

        Assert.Equal(50m, _transactions.Stored(expense.Id).Amount);
        Assert.Equal(_savings.Id, _transactions.Stored(transfer.Id).DestinationAccountId);
    }

    // ---- Update: not found ----

    [Fact]
    public async Task Update_MissingOrAnotherUsersTransaction_IsNotFoundForTheTransaction()
    {
        var expense = AddExpense(_checking, _groceries, 50m);

        var foreign = await UpdateAsync(TestUsers.B, Command(expense.Id, 5m, OccurredAt, null, account: (_accountOfB.Id, _categoryOfB.Id)));
        var missing = await UpdateAsync(TestUsers.A, Command(Guid.CreateVersion7(), 5m, OccurredAt, null, account: (_checking.Id, _groceries.Id)));

        foreach (var result in new[] { foreign, missing })
        {
            Assert.Equal(UpdateTransactionStatus.NotFound, result.Status);
            Assert.Null(result.Field);
            Assert.Equal("This transaction no longer exists.", result.Message);
        }

        Assert.Equal(50m, _transactions.Stored(expense.Id).Amount);
    }

    [Theory]
    [InlineData("foreign-account", "accountTransaction.accountId")]
    [InlineData("foreign-category", "accountTransaction.categoryId")]
    [InlineData("missing-account", "accountTransaction.accountId")]
    public async Task Update_WithAnotherUsersOrAMissingReference_IsNotFoundForTheField(string variant, string field)
    {
        var expense = AddExpense(_checking, _groceries, 50m);
        var reference = variant switch
        {
            "foreign-account" => (_accountOfB.Id, _groceries.Id),
            "foreign-category" => (_checking.Id, _categoryOfB.Id),
            _ => (Guid.CreateVersion7(), _groceries.Id)
        };

        var result = await UpdateAsync(TestUsers.A, Command(expense.Id, 5m, OccurredAt, null, account: reference));

        Assert.Equal(UpdateTransactionStatus.NotFound, result.Status);
        Assert.Equal(field, result.Field);
        Assert.Equal(50m, _transactions.Stored(expense.Id).Amount);
    }

    // ---- Update: races ----

    [Fact]
    public async Task Update_TransactionDeletedConcurrently_IsNotFound()
    {
        var expense = AddExpense(_checking, _groceries, 50m);
        _transactions.BeforeWrite = () => _transactions.Transactions.Clear();

        var result = await UpdateAsync(TestUsers.A, Command(expense.Id, 5m, OccurredAt, null, account: (_checking.Id, _groceries.Id)));

        Assert.Equal(UpdateTransactionStatus.NotFound, result.Status);
        Assert.Null(result.Field);
    }

    [Fact]
    public async Task Update_ReferencedAccountDeletedConcurrently_IsNotFoundForThatField_AfterOneReRead()
    {
        var expense = AddExpense(_checking, _groceries, 50m);
        SimulateConcurrentDelete(() => _accounts.Accounts.RemoveAll(account => account.Id == _savings.Id));

        var result = await UpdateAsync(TestUsers.A, Command(expense.Id, 5m, OccurredAt, null, account: (_savings.Id, _groceries.Id)));

        Assert.Equal(UpdateTransactionStatus.NotFound, result.Status);
        Assert.Equal("accountTransaction.accountId", result.Field);
        Assert.Equal(_checking.Id, _transactions.Stored(expense.Id).AccountId);
    }

    [Fact]
    public async Task Update_TransferDestinationDeletedConcurrently_IsNotFoundForTheDestination()
    {
        var transfer = AddTransfer(_checking, _savings, 100m);
        var cash = AddAccount(TestUsers.A, "Cash", "EUR");
        SimulateConcurrentDelete(() => _accounts.Accounts.RemoveAll(account => account.Id == cash.Id));

        var result = await UpdateAsync(TestUsers.A, Command(transfer.Id, 5m, OccurredAt, null, transfer: (_checking.Id, cash.Id)));

        Assert.Equal(UpdateTransactionStatus.NotFound, result.Status);
        Assert.Equal("transfer.destinationAccountId", result.Field);
    }

    // ---- Delete ----

    [Fact]
    public async Task Delete_OwnTransaction_RemovesOnlyIt()
    {
        var expense = AddExpense(_checking, _groceries, 50m);
        var other = AddExpense(_checking, _groceries, 5m);

        Assert.Equal(DeleteTransactionResult.Deleted, await DeleteAsync(TestUsers.A, expense.Id));
        Assert.Equal(other.Id, Assert.Single(_transactions.Transactions).Id);
    }

    [Fact]
    public async Task Delete_MissingAnotherUsersOrAlreadyDeleted_IsNotFound()
    {
        var expense = AddExpense(_checking, _groceries, 50m);

        Assert.Equal(DeleteTransactionResult.NotFound, await DeleteAsync(TestUsers.B, expense.Id));
        Assert.Equal(DeleteTransactionResult.NotFound, await DeleteAsync(TestUsers.A, Guid.CreateVersion7()));
        Assert.Single(_transactions.Transactions);

        Assert.Equal(DeleteTransactionResult.Deleted, await DeleteAsync(TestUsers.A, expense.Id));
        Assert.Equal(DeleteTransactionResult.NotFound, await DeleteAsync(TestUsers.A, expense.Id));
    }

    private void AssertInvalid(UpdateTransactionResult result, string field, Transaction original)
    {
        Assert.Equal(UpdateTransactionStatus.Invalid, result.Status);
        Assert.Equal(field, result.Field);
        var stored = _transactions.Stored(original.Id);
        Assert.Equal((original.Amount, original.AccountId, original.SourceAccountId, original.DestinationAccountId), (stored.Amount, stored.AccountId, stored.SourceAccountId, stored.DestinationAccountId));
    }

    // Deletes just before the write, which then checks its references like the foreign keys.
    private void SimulateConcurrentDelete(Action delete)
    {
        _transactions.BeforeWrite = delete;
        _transactions.ReferencesExist = transaction =>
            new[] { transaction.AccountId, transaction.SourceAccountId, transaction.DestinationAccountId }
                .All(id => id is null || _accounts.Accounts.Any(account => account.Id == id))
            && (transaction.CategoryId is null || _categories.Categories.Any(category => category.Id == transaction.CategoryId));
    }

    private static UpdateTransactionCommand Command(
        Guid transactionId,
        decimal amount,
        DateTimeOffset occurredAtUtc,
        string? note,
        (Guid Account, Guid Category)? account = null,
        (Guid Source, Guid Destination)? transfer = null) =>
        new(
            transactionId,
            amount,
            occurredAtUtc,
            note,
            account is { } a ? new AccountTransactionInput(a.Account, a.Category) : null,
            transfer is { } t ? new TransferInput(t.Source, t.Destination) : null);

    private Task<TransactionSummary?> GetAsync(Guid userId, Guid id) =>
        new GetTransactionHandler(_transactions).HandleAsync(userId, id, CancellationToken.None);

    private Task<UpdateTransactionResult> UpdateAsync(Guid userId, UpdateTransactionCommand command) =>
        new UpdateTransactionHandler(_accounts, _categories, _transactions).HandleAsync(userId, command, CancellationToken.None);

    private Task<DeleteTransactionResult> DeleteAsync(Guid userId, Guid id) =>
        new DeleteTransactionHandler(_transactions).HandleAsync(userId, id, CancellationToken.None);

    private Account AddAccount(Guid userId, string name, string currency)
    {
        var account = Account.Create(userId, name, AccountType.BankAccount, currency, Now.AddDays(-30));
        _accounts.Accounts.Add(account);

        return account;
    }

    private Category AddCategory(Guid userId, string name, CategoryType type, Category? parent = null)
    {
        var category = Category.Create(userId, name, type, parent, Now.AddDays(-30));
        _categories.Categories.Add(category);

        return category;
    }

    private Transaction AddExpense(Account account, Category category, decimal amount, string? note = null) =>
        Add(Transaction.CreateExpense(TestUsers.A, account.Id, category.Id, amount, account.Currency, OccurredAt, note, Now));

    private Transaction AddIncome(Account account, decimal amount) =>
        Add(Transaction.CreateIncome(TestUsers.A, account.Id, _salary.Id, amount, account.Currency, OccurredAt, null, Now));

    private Transaction AddTransfer(Account source, Account destination, decimal amount) =>
        Add(Transaction.CreateTransfer(TestUsers.A, source.Id, destination.Id, amount, source.Currency, OccurredAt, null, Now));

    private Transaction Add(Transaction transaction)
    {
        _transactions.Transactions.Add(transaction);

        return transaction;
    }
}
