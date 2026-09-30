using LifeOS.Application.Finance.Transactions.CreateTransaction;
using LifeOS.Application.Finance.Transactions.GetTransactions;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Transactions;

// User A owns the "own" resources; user B owns the "foreign" ones. A must never reach B's data.
public class TransactionOwnershipTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset OccurredAtUtc = new(2026, 9, 29, 18, 0, 0, TimeSpan.Zero);

    private readonly InMemoryAccountRepository _accounts = new();
    private readonly InMemoryCategoryRepository _categories = new();
    private readonly InMemoryTransactionRepository _transactions = new();
    private readonly CreateTransactionHandler _handler;

    private readonly Account _ownAccount;
    private readonly Account _ownSecondAccount;
    private readonly Category _ownExpenseCategory;
    private readonly Account _foreignAccount;
    private readonly Category _foreignExpenseCategory;

    public TransactionOwnershipTests()
    {
        _handler = new CreateTransactionHandler(_accounts, _categories, _transactions, new FixedTimeProvider(UtcNow));

        _ownAccount = AddAccount(TestUsers.A, "A checking");
        _ownSecondAccount = AddAccount(TestUsers.A, "A savings");
        _ownExpenseCategory = AddCategory(TestUsers.A, "A groceries");
        _foreignAccount = AddAccount(TestUsers.B, "B checking");
        _foreignExpenseCategory = AddCategory(TestUsers.B, "B groceries");
    }

    [Fact]
    public async Task Expense_WithOwnAccountAndCategory_IsCreatedForTheCaller()
    {
        var result = await CreateAsync(Expense(_ownAccount.Id, _ownExpenseCategory.Id));

        Assert.Equal(CreateTransactionStatus.Created, result.Status);
        Assert.Equal(TestUsers.A, Assert.Single(_transactions.Transactions).UserId);
    }

    [Fact]
    public async Task Transfer_BetweenOwnAccounts_IsCreatedForTheCaller()
    {
        var result = await CreateAsync(Transfer(_ownAccount.Id, _ownSecondAccount.Id));

        Assert.Equal(CreateTransactionStatus.Created, result.Status);
        Assert.Equal(TestUsers.A, Assert.Single(_transactions.Transactions).UserId);
    }

    [Fact]
    public async Task Expense_WithForeignAccount_ReturnsNotFound()
    {
        var result = await CreateAsync(Expense(_foreignAccount.Id, _ownExpenseCategory.Id));

        AssertNotFound(result, "accountId");
    }

    [Fact]
    public async Task Expense_WithForeignCategory_ReturnsNotFound()
    {
        var result = await CreateAsync(Expense(_ownAccount.Id, _foreignExpenseCategory.Id));

        AssertNotFound(result, "categoryId");
    }

    [Fact]
    public async Task Transfer_WithForeignSourceAccount_ReturnsNotFound()
    {
        var result = await CreateAsync(Transfer(_foreignAccount.Id, _ownAccount.Id));

        AssertNotFound(result, "sourceAccountId");
    }

    [Fact]
    public async Task Transfer_WithForeignDestinationAccount_ReturnsNotFound()
    {
        var result = await CreateAsync(Transfer(_ownAccount.Id, _foreignAccount.Id));

        AssertNotFound(result, "destinationAccountId");
    }

    [Fact]
    public async Task ForeignResource_IsReportedExactlyLikeAMissingOne()
    {
        var foreign = await CreateAsync(Expense(_foreignAccount.Id, _ownExpenseCategory.Id));
        var missingId = Guid.CreateVersion7();
        var missing = await CreateAsync(Expense(missingId, _ownExpenseCategory.Id));

        Assert.Equal(missing.Status, foreign.Status);
        Assert.Equal(missing.Field, foreign.Field);
        Assert.Equal(
            missing.Message!.Replace(missingId.ToString(), "{id}"),
            foreign.Message!.Replace(_foreignAccount.Id.ToString(), "{id}"));
    }

    [Fact]
    public async Task GetTransactions_ReturnsOnlyTheCallersTransactions()
    {
        var own = Transaction.CreateExpense(
            TestUsers.A, _ownAccount.Id, _ownExpenseCategory.Id, 10m, "EUR", OccurredAtUtc, null, UtcNow);
        var foreign = Transaction.CreateExpense(
            TestUsers.B, _foreignAccount.Id, _foreignExpenseCategory.Id, 20m, "EUR", OccurredAtUtc, null, UtcNow);
        _transactions.Transactions.AddRange([own, foreign]);

        var result = await new GetTransactionsHandler(_transactions).HandleAsync(
            TestUsers.A,
            new GetTransactionsQuery(OccurredAtUtc.AddDays(-1), OccurredAtUtc.AddDays(1)),
            CancellationToken.None);

        Assert.Equal(GetTransactionsStatus.Ok, result.Status);
        Assert.Equal(own.Id, Assert.Single(result.Transactions).Id);
    }

    private Task<CreateTransactionResult> CreateAsync(CreateTransactionCommand command) =>
        _handler.HandleAsync(TestUsers.A, command, CancellationToken.None);

    private void AssertNotFound(CreateTransactionResult result, string field)
    {
        Assert.Equal(CreateTransactionStatus.NotFound, result.Status);
        Assert.Equal(field, result.Field);
        Assert.Empty(_transactions.Transactions);
    }

    private static CreateTransactionCommand Expense(Guid accountId, Guid categoryId) =>
        new(TransactionType.Expense, 12.50m, accountId, null, null, categoryId, OccurredAtUtc, null);

    private static CreateTransactionCommand Transfer(Guid sourceAccountId, Guid destinationAccountId) =>
        new(TransactionType.Transfer, 100m, null, sourceAccountId, destinationAccountId, null, OccurredAtUtc, null);

    private Account AddAccount(Guid userId, string name)
    {
        var account = Account.Create(userId, name, AccountType.BankAccount, "EUR", UtcNow);
        _accounts.Accounts.Add(account);

        return account;
    }

    private Category AddCategory(Guid userId, string name)
    {
        var category = Category.Create(userId, name, CategoryType.Expense, parent: null, UtcNow);
        _categories.Categories.Add(category);

        return category;
    }
}
