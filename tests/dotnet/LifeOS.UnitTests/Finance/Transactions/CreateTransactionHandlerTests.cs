using LifeOS.Application.Finance.Transactions.CreateTransaction;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Transactions;

public class CreateTransactionHandlerTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 9, 29, 18, 5, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset OccurredAtUtc = new(2026, 9, 29, 18, 0, 0, TimeSpan.Zero);

    private readonly InMemoryAccountRepository _accounts = new();
    private readonly InMemoryCategoryRepository _categories = new();
    private readonly InMemoryTransactionRepository _transactions = new();
    private readonly CreateTransactionHandler _handler;

    private readonly Account _checking;
    private readonly Account _savings;
    private readonly Account _usdAccount;
    private readonly Category _eatingOut;
    private readonly Category _bar;
    private readonly Category _salary;
    private readonly Category _thirteenthSalary;

    public CreateTransactionHandlerTests()
    {
        _handler = new CreateTransactionHandler(_accounts, _categories, _transactions, new FixedTimeProvider(UtcNow));

        _checking = AddAccount("Checking", "EUR");
        _savings = AddAccount("Savings", "EUR");
        _usdAccount = AddAccount("USD wallet", "USD");

        _eatingOut = AddCategory("Mangiare fuori", CategoryType.Expense);
        _bar = AddCategory("Bar", CategoryType.Expense, _eatingOut);
        _salary = AddCategory("Stipendio", CategoryType.Income);
        _thirteenthSalary = AddCategory("Tredicesima", CategoryType.Income, _salary);
    }

    // Income and Expense: success

    [Fact]
    public async Task HandleAsync_IncomeWithTopLevelCategory_CreatesAndPersistsOnce()
    {
        var result = await _handler.HandleAsync(Income(_checking.Id, _salary.Id), CancellationToken.None);

        Assert.Equal(CreateTransactionStatus.Created, result.Status);
        var persisted = Assert.Single(_transactions.Transactions);
        Assert.Equal(persisted.Id, result.Transaction!.Id);
        Assert.Equal(TransactionType.Income, persisted.TransactionType);
        Assert.Equal(_checking.Id, persisted.AccountId);
        Assert.Equal(_salary.Id, persisted.CategoryId);
        Assert.Equal("EUR", persisted.Currency);
    }

    [Fact]
    public async Task HandleAsync_IncomeWithSubcategory_IsAccepted()
    {
        var result = await _handler.HandleAsync(Income(_checking.Id, _thirteenthSalary.Id), CancellationToken.None);

        Assert.Equal(CreateTransactionStatus.Created, result.Status);
        Assert.Equal(_thirteenthSalary.Id, Assert.Single(_transactions.Transactions).CategoryId);
    }

    [Fact]
    public async Task HandleAsync_ExpenseWithTopLevelCategory_CreatesAndPersistsOnce()
    {
        var result = await _handler.HandleAsync(Expense(_checking.Id, _eatingOut.Id), CancellationToken.None);

        Assert.Equal(CreateTransactionStatus.Created, result.Status);
        var persisted = Assert.Single(_transactions.Transactions);
        Assert.Equal(TransactionType.Expense, persisted.TransactionType);
        Assert.Equal(_eatingOut.Id, persisted.CategoryId);
    }

    [Fact]
    public async Task HandleAsync_ExpenseWithSubcategory_IsAccepted()
    {
        var result = await _handler.HandleAsync(Expense(_checking.Id, _bar.Id), CancellationToken.None);

        Assert.Equal(CreateTransactionStatus.Created, result.Status);
        Assert.Equal(_bar.Id, Assert.Single(_transactions.Transactions).CategoryId);
    }

    [Fact]
    public async Task HandleAsync_DerivesCurrencyFromAccount()
    {
        var result = await _handler.HandleAsync(Expense(_usdAccount.Id, _eatingOut.Id), CancellationToken.None);

        Assert.Equal("USD", result.Transaction!.Currency);
        Assert.Equal("USD", Assert.Single(_transactions.Transactions).Currency);
    }

    [Fact]
    public async Task HandleAsync_UsesCreatedAtFromTimeProvider()
    {
        var result = await _handler.HandleAsync(Expense(_checking.Id, _eatingOut.Id), CancellationToken.None);

        Assert.Equal(UtcNow, result.Transaction!.CreatedAtUtc);
        Assert.Equal(OccurredAtUtc, result.Transaction.OccurredAtUtc);
    }

    // Transfer: success

    [Fact]
    public async Task HandleAsync_Transfer_PersistsExactlyOneTransaction()
    {
        var result = await _handler.HandleAsync(Transfer(_checking.Id, _savings.Id), CancellationToken.None);

        Assert.Equal(CreateTransactionStatus.Created, result.Status);
        var persisted = Assert.Single(_transactions.Transactions);
        Assert.Equal(TransactionType.Transfer, persisted.TransactionType);
        Assert.Equal(_checking.Id, persisted.SourceAccountId);
        Assert.Equal(_savings.Id, persisted.DestinationAccountId);
        Assert.Null(persisted.AccountId);
        Assert.Null(persisted.CategoryId);
        Assert.Equal("EUR", persisted.Currency);
    }

    // Field combinations

    [Theory]
    [InlineData("income-with-source", "sourceAccountId")]
    [InlineData("expense-with-destination", "destinationAccountId")]
    [InlineData("income-without-account", "accountId")]
    [InlineData("expense-without-category", "categoryId")]
    [InlineData("transfer-with-account", "accountId")]
    [InlineData("transfer-with-category", "categoryId")]
    [InlineData("transfer-without-source", "sourceAccountId")]
    [InlineData("transfer-without-destination", "destinationAccountId")]
    public async Task HandleAsync_WithInvalidFieldCombination_ReturnsInvalidAndDoesNotPersist(
        string scenario,
        string expectedField)
    {
        var command = scenario switch
        {
            "income-with-source" => Income(_checking.Id, _salary.Id) with { SourceAccountId = _savings.Id },
            "expense-with-destination" => Expense(_checking.Id, _bar.Id) with { DestinationAccountId = _savings.Id },
            "income-without-account" => Income(_checking.Id, _salary.Id) with { AccountId = null },
            "expense-without-category" => Expense(_checking.Id, _bar.Id) with { CategoryId = null },
            "transfer-with-account" => Transfer(_checking.Id, _savings.Id) with { AccountId = _checking.Id },
            "transfer-with-category" => Transfer(_checking.Id, _savings.Id) with { CategoryId = _bar.Id },
            "transfer-without-source" => Transfer(_checking.Id, _savings.Id) with { SourceAccountId = null },
            "transfer-without-destination" => Transfer(_checking.Id, _savings.Id) with { DestinationAccountId = null },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        var result = await _handler.HandleAsync(command, CancellationToken.None);

        Assert.Equal(CreateTransactionStatus.Invalid, result.Status);
        Assert.Equal(expectedField, result.Field);
        Assert.Empty(_transactions.Transactions);
    }

    // Not found

    [Fact]
    public async Task HandleAsync_WithMissingAccount_ReturnsNotFound()
    {
        var result = await _handler.HandleAsync(Expense(Guid.CreateVersion7(), _eatingOut.Id), CancellationToken.None);

        AssertNotFound(result, "accountId");
    }

    [Fact]
    public async Task HandleAsync_WithMissingCategory_ReturnsNotFound()
    {
        var result = await _handler.HandleAsync(Expense(_checking.Id, Guid.CreateVersion7()), CancellationToken.None);

        AssertNotFound(result, "categoryId");
    }

    [Fact]
    public async Task HandleAsync_WithMissingSource_ReturnsNotFound()
    {
        var result = await _handler.HandleAsync(Transfer(Guid.CreateVersion7(), _savings.Id), CancellationToken.None);

        AssertNotFound(result, "sourceAccountId");
    }

    [Fact]
    public async Task HandleAsync_WithMissingDestination_ReturnsNotFound()
    {
        var result = await _handler.HandleAsync(Transfer(_checking.Id, Guid.CreateVersion7()), CancellationToken.None);

        AssertNotFound(result, "destinationAccountId");
    }

    // Compatibility

    [Fact]
    public async Task HandleAsync_IncomeWithExpenseCategory_ReturnsInvalid()
    {
        var result = await _handler.HandleAsync(Income(_checking.Id, _bar.Id), CancellationToken.None);

        AssertInvalid(result, "categoryId");
    }

    [Fact]
    public async Task HandleAsync_ExpenseWithIncomeCategory_ReturnsInvalid()
    {
        var result = await _handler.HandleAsync(Expense(_checking.Id, _salary.Id), CancellationToken.None);

        AssertInvalid(result, "categoryId");
    }

    [Fact]
    public async Task HandleAsync_TransferBetweenDifferentCurrencies_ReturnsInvalid()
    {
        var result = await _handler.HandleAsync(Transfer(_checking.Id, _usdAccount.Id), CancellationToken.None);

        AssertInvalid(result, "destinationAccountId");
    }

    // Domain failures

    [Fact]
    public async Task HandleAsync_WithInvalidAmount_ThrowsAndDoesNotPersist()
    {
        var command = Expense(_checking.Id, _eatingOut.Id) with { Amount = 0m };

        await Assert.ThrowsAnyAsync<ArgumentException>(() => _handler.HandleAsync(command, CancellationToken.None));

        Assert.Empty(_transactions.Transactions);
    }

    [Fact]
    public async Task HandleAsync_TransferToSameAccount_ThrowsAndDoesNotPersist()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _handler.HandleAsync(Transfer(_checking.Id, _checking.Id), CancellationToken.None));

        Assert.Empty(_transactions.Transactions);
    }

    private static CreateTransactionCommand Income(Guid accountId, Guid categoryId) =>
        new(TransactionType.Income, 1500m, accountId, null, null, categoryId, OccurredAtUtc, "Settembre");

    private static CreateTransactionCommand Expense(Guid accountId, Guid categoryId) =>
        new(TransactionType.Expense, 18.50m, accountId, null, null, categoryId, OccurredAtUtc, "Aperitivo");

    private static CreateTransactionCommand Transfer(Guid sourceAccountId, Guid destinationAccountId) =>
        new(TransactionType.Transfer, 100m, null, sourceAccountId, destinationAccountId, null, OccurredAtUtc, "Move to savings");

    private void AssertNotFound(CreateTransactionResult result, string expectedField)
    {
        Assert.Equal(CreateTransactionStatus.NotFound, result.Status);
        Assert.Equal(expectedField, result.Field);
        Assert.Empty(_transactions.Transactions);
    }

    private void AssertInvalid(CreateTransactionResult result, string expectedField)
    {
        Assert.Equal(CreateTransactionStatus.Invalid, result.Status);
        Assert.Equal(expectedField, result.Field);
        Assert.Empty(_transactions.Transactions);
    }

    private Account AddAccount(string name, string currency)
    {
        var account = Account.Create(name, AccountType.BankAccount, currency, UtcNow);
        _accounts.Accounts.Add(account);

        return account;
    }

    private Category AddCategory(string name, CategoryType categoryType, Category? parent = null)
    {
        var category = Category.Create(name, categoryType, parent, UtcNow);
        _categories.Categories.Add(category);

        return category;
    }
}
