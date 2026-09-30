using System.Globalization;
using LifeOS.Api.Finance;
using LifeOS.Application.Finance.Transactions.CreateTransaction;
using LifeOS.Application.Finance.Transactions.GetTransactions;
using LifeOS.Contracts.Finance.Transactions;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.UnitTests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.UnitTests.Finance.Transactions;

public class TransactionEndpointsTests
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

    public TransactionEndpointsTests()
    {
        _handler = new CreateTransactionHandler(_accounts, _categories, _transactions, new FixedTimeProvider(UtcNow));

        _checking = AddAccount("Checking", "EUR");
        _savings = AddAccount("Savings", "EUR");
        _usdAccount = AddAccount("USD wallet", "USD");

        _eatingOut = AddCategory("Mangiare fuori", CategoryType.Expense);
        _bar = AddCategory("Bar", CategoryType.Expense, _eatingOut);
        _salary = AddCategory("Stipendio", CategoryType.Income);
    }

    // 201 Created

    [Fact]
    public async Task CreateTransaction_ValidIncome_ReturnsCreated()
    {
        var response = await AssertCreated(IncomeRequest(_checking.Id, _salary.Id));

        Assert.Equal("Income", response.Type);
        Assert.Equal(_checking.Id, response.AccountId);
        Assert.Equal(_salary.Id, response.CategoryId);
        Assert.Equal("EUR", response.Currency);
    }

    [Fact]
    public async Task CreateTransaction_ValidExpenseWithTopLevelCategory_ReturnsCreated()
    {
        var response = await AssertCreated(ExpenseRequest(_checking.Id, _eatingOut.Id));

        Assert.Equal("Expense", response.Type);
        Assert.Equal(18.50m, response.Amount);
        Assert.Equal(_eatingOut.Id, response.CategoryId);
        Assert.Equal("Aperitivo", response.Note);
        Assert.Equal(OccurredAtUtc, response.OccurredAtUtc);
        Assert.Equal(UtcNow, response.CreatedAtUtc);
    }

    [Fact]
    public async Task CreateTransaction_ValidExpenseWithSubcategory_ReturnsCreated()
    {
        var response = await AssertCreated(ExpenseRequest(_checking.Id, _bar.Id));

        Assert.Equal(_bar.Id, response.CategoryId);
    }

    [Fact]
    public async Task CreateTransaction_ValidTransfer_ReturnsCreatedWithCurrencyFromAccounts()
    {
        var response = await AssertCreated(TransferRequest(_checking.Id, _savings.Id));

        Assert.Equal("Transfer", response.Type);
        Assert.Equal(_checking.Id, response.SourceAccountId);
        Assert.Equal(_savings.Id, response.DestinationAccountId);
        Assert.Null(response.AccountId);
        Assert.Null(response.CategoryId);
        Assert.Equal("EUR", response.Currency);
        Assert.Single(_transactions.Transactions);
    }

    // 400 Bad Request

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Refund")]
    [InlineData("1")]
    [InlineData("Income,Expense")]
    public async Task CreateTransaction_WithInvalidType_ReturnsValidationProblem(string? type)
    {
        var request = ExpenseRequest(_checking.Id, _eatingOut.Id) with { Type = type! };

        await AssertValidationProblem(request, "type");
    }

    [Fact]
    public async Task CreateTransaction_WithoutOccurredAt_ReturnsValidationProblem()
    {
        var request = ExpenseRequest(_checking.Id, _eatingOut.Id) with { OccurredAtUtc = null };

        await AssertValidationProblem(request, "occurredAtUtc");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-18.50")]
    [InlineData("18.12345")]
    [InlineData("1000000000000000")]
    public async Task CreateTransaction_WithUnsupportedAmount_ReturnsValidationProblem(string amount)
    {
        var request = ExpenseRequest(_checking.Id, _eatingOut.Id) with
        {
            Amount = decimal.Parse(amount, CultureInfo.InvariantCulture)
        };

        await AssertValidationProblem(request, "amount");
    }

    [Fact]
    public async Task CreateTransaction_TransferWithCategory_ReturnsValidationProblem()
    {
        var request = TransferRequest(_checking.Id, _savings.Id) with { CategoryId = _bar.Id };

        await AssertValidationProblem(request, "categoryId");
    }

    [Fact]
    public async Task CreateTransaction_ExpenseWithTransferFields_ReturnsValidationProblem()
    {
        var request = ExpenseRequest(_checking.Id, _eatingOut.Id) with { SourceAccountId = _savings.Id };

        await AssertValidationProblem(request, "sourceAccountId");
    }

    [Fact]
    public async Task CreateTransaction_TransferToSameAccount_ReturnsValidationProblem()
    {
        await AssertValidationProblem(TransferRequest(_checking.Id, _checking.Id), "destinationAccountId");
    }

    [Fact]
    public async Task CreateTransaction_WithIncompatibleCategory_ReturnsValidationProblem()
    {
        await AssertValidationProblem(ExpenseRequest(_checking.Id, _salary.Id), "categoryId");
    }

    [Fact]
    public async Task CreateTransaction_TransferBetweenDifferentCurrencies_ReturnsValidationProblem()
    {
        await AssertValidationProblem(TransferRequest(_checking.Id, _usdAccount.Id), "destinationAccountId");
    }

    // 404 Not Found

    [Fact]
    public async Task CreateTransaction_WithMissingAccount_ReturnsNotFound()
    {
        await AssertNotFound(ExpenseRequest(Guid.CreateVersion7(), _eatingOut.Id));
    }

    [Fact]
    public async Task CreateTransaction_WithMissingCategory_ReturnsNotFound()
    {
        await AssertNotFound(ExpenseRequest(_checking.Id, Guid.CreateVersion7()));
    }

    [Fact]
    public async Task CreateTransaction_WithMissingSource_ReturnsNotFound()
    {
        await AssertNotFound(TransferRequest(Guid.CreateVersion7(), _savings.Id));
    }

    [Fact]
    public async Task CreateTransaction_WithMissingDestination_ReturnsNotFound()
    {
        await AssertNotFound(TransferRequest(_checking.Id, Guid.CreateVersion7()));
    }

    // GET /api/transactions

    [Fact]
    public async Task GetTransactions_WithValidRange_ReturnsOkWithTransactions()
    {
        await AssertCreated(ExpenseRequest(_checking.Id, _bar.Id));

        var result = await GetTransactions("2026-08-31T22:00:00Z", "2026-09-30T22:00:00Z");

        var ok = Assert.IsType<Ok<IReadOnlyList<TransactionResponse>>>(result.Result);
        var response = Assert.Single(ok.Value!);
        Assert.Equal("Expense", response.Type);
        Assert.Equal(_bar.Id, response.CategoryId);
        Assert.Equal(_checking.Id, response.AccountId);
        Assert.Equal("EUR", response.Currency);
        Assert.Equal(OccurredAtUtc, response.OccurredAtUtc);
    }

    [Fact]
    public async Task GetTransactions_AcceptsExplicitZeroOffset()
    {
        await AssertCreated(ExpenseRequest(_checking.Id, _bar.Id));

        var result = await GetTransactions("2026-08-31T22:00:00+00:00", "2026-09-30T22:00:00+00:00");

        var ok = Assert.IsType<Ok<IReadOnlyList<TransactionResponse>>>(result.Result);
        Assert.Single(ok.Value!);
    }

    [Fact]
    public async Task GetTransactions_WithNoTransactions_ReturnsOkWithEmptyList()
    {
        var result = await GetTransactions("2026-08-31T22:00:00Z", "2026-09-30T22:00:00Z");

        var ok = Assert.IsType<Ok<IReadOnlyList<TransactionResponse>>>(result.Result);
        Assert.NotNull(ok.Value);
        Assert.Empty(ok.Value);
    }

    [Theory]
    [InlineData(null, "2026-09-30T22:00:00Z", "fromUtc")]
    [InlineData("2026-08-31T22:00:00Z", null, "toUtc")]
    [InlineData("", "2026-09-30T22:00:00Z", "fromUtc")]
    [InlineData("not-a-date", "2026-09-30T22:00:00Z", "fromUtc")]
    [InlineData("2026-08-31T22:00:00Z", "2026-13-01T00:00:00Z", "toUtc")]
    [InlineData("2026-08-31T22:00:00", "2026-09-30T22:00:00Z", "fromUtc")]
    [InlineData("2026-08-31T22:00:00Z", "2026-09-30", "toUtc")]
    [InlineData("2026-09-01T00:00:00+02:00", "2026-09-30T22:00:00Z", "fromUtc")]
    [InlineData("2026-08-31T22:00:00Z", "2026-10-01T00:00:00+02:00", "toUtc")]
    [InlineData("2026-09-30T22:00:00Z", "2026-08-31T22:00:00Z", "toUtc")]
    [InlineData("2026-08-31T22:00:00Z", "2026-08-31T22:00:00Z", "toUtc")]
    public async Task GetTransactions_WithInvalidRange_ReturnsValidationProblem(
        string? fromUtc,
        string? toUtc,
        string expectedField)
    {
        var result = await GetTransactions(fromUtc, toUtc);

        var problem = Assert.IsType<ValidationProblem>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Contains(expectedField, problem.ProblemDetails.Errors.Keys);
    }

    private Task<Results<Ok<IReadOnlyList<TransactionResponse>>, ValidationProblem>> GetTransactions(
        string? fromUtc,
        string? toUtc) =>
        TransactionEndpoints.GetTransactionsAsync(
            fromUtc,
            toUtc,
            TestUsers.AuthenticatedA,
            new GetTransactionsHandler(_transactions),
            CancellationToken.None);

    private static CreateTransactionRequest IncomeRequest(Guid accountId, Guid categoryId) =>
        new("Income", 1500m, accountId, null, null, categoryId, OccurredAtUtc, "Settembre");

    private static CreateTransactionRequest ExpenseRequest(Guid accountId, Guid categoryId) =>
        new("expense", 18.50m, accountId, null, null, categoryId, OccurredAtUtc, " Aperitivo ");

    private static CreateTransactionRequest TransferRequest(Guid sourceAccountId, Guid destinationAccountId) =>
        new("Transfer", 100m, null, sourceAccountId, destinationAccountId, null, OccurredAtUtc, "Move to savings");

    private async Task<TransactionResponse> AssertCreated(CreateTransactionRequest request)
    {
        var result = await TransactionEndpoints.CreateTransactionAsync(request, TestUsers.AuthenticatedA, _handler, CancellationToken.None);

        var created = Assert.IsType<Created<TransactionResponse>>(result.Result);
        var response = Assert.IsType<TransactionResponse>(created.Value);
        Assert.Equal($"/api/transactions/{response.Id}", created.Location);
        Assert.Equal(response.Id, Assert.Single(_transactions.Transactions).Id);

        return response;
    }

    private async Task AssertValidationProblem(CreateTransactionRequest request, string expectedField)
    {
        var result = await TransactionEndpoints.CreateTransactionAsync(request, TestUsers.AuthenticatedA, _handler, CancellationToken.None);

        var problem = Assert.IsType<ValidationProblem>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Contains(expectedField, problem.ProblemDetails.Errors.Keys);
        Assert.Empty(_transactions.Transactions);
    }

    private async Task AssertNotFound(CreateTransactionRequest request)
    {
        var result = await TransactionEndpoints.CreateTransactionAsync(request, TestUsers.AuthenticatedA, _handler, CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        Assert.Empty(_transactions.Transactions);
    }

    private Account AddAccount(string name, string currency)
    {
        var account = Account.Create(TestUsers.A, name, AccountType.BankAccount, currency, UtcNow);
        _accounts.Accounts.Add(account);

        return account;
    }

    private Category AddCategory(string name, CategoryType categoryType, Category? parent = null)
    {
        var category = Category.Create(TestUsers.A, name, categoryType, parent, UtcNow);
        _categories.Categories.Add(category);

        return category;
    }
}
