using LifeOS.Api.Finance;
using LifeOS.Application.Finance.Accounts;
using LifeOS.Application.Finance.Accounts.CreateAccount;
using LifeOS.Application.Finance.Accounts.DeleteAccount;
using LifeOS.Application.Finance.Accounts.GetAccounts;
using LifeOS.Application.Finance.Accounts.UpdateAccount;
using LifeOS.Contracts.Finance.Accounts;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.UnitTests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.UnitTests.Finance.Accounts;

public class AccountEndpointsTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 9, 29, 10, 30, 0, TimeSpan.Zero);

    private readonly InMemoryAccountRepository _repository = new();
    private readonly CreateAccountHandler _handler;

    public AccountEndpointsTests()
    {
        _handler = new CreateAccountHandler(_repository, new FixedTimeProvider(UtcNow));
    }

    [Fact]
    public async Task CreateAccount_WithValidRequest_ReturnsCreated()
    {
        var request = new CreateAccountRequest("Main account", "BankAccount", "eur");

        var result = await AccountEndpoints.CreateAccountAsync(request, TestUsers.AuthenticatedA, _handler, CancellationToken.None);

        var created = Assert.IsType<Created<AccountResponse>>(result.Result);
        var response = Assert.IsType<AccountResponse>(created.Value);
        Assert.Equal($"/api/accounts/{response.Id}", created.Location);
        Assert.Equal("Main account", response.Name);
        Assert.Equal("BankAccount", response.Type);
        Assert.Equal("EUR", response.Currency);
        Assert.Equal(UtcNow, response.CreatedAtUtc);
        Assert.Single(_repository.Accounts);
    }

    [Theory]
    [InlineData("creditcard")]
    [InlineData("SAVINGS")]
    public async Task CreateAccount_AcceptsAccountTypeNameIgnoringCase(string type)
    {
        var request = new CreateAccountRequest("Main account", type, "EUR");

        var result = await AccountEndpoints.CreateAccountAsync(request, TestUsers.AuthenticatedA, _handler, CancellationToken.None);

        Assert.IsType<Created<AccountResponse>>(result.Result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Crypto")]
    [InlineData("1")]
    [InlineData("99")]
    [InlineData("Cash,BankAccount")]
    public async Task CreateAccount_WithInvalidAccountType_ReturnsValidationProblem(string? type)
    {
        var request = new CreateAccountRequest("Main account", type!, "EUR");

        var result = await AccountEndpoints.CreateAccountAsync(request, TestUsers.AuthenticatedA, _handler, CancellationToken.None);

        var problem = Assert.IsType<ValidationProblem>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Contains("type", problem.ProblemDetails.Errors.Keys);
        Assert.Empty(_repository.Accounts);
    }

    [Theory]
    [InlineData(" ", "EUR", "name")]
    [InlineData("Main account", "EURO", "currency")]
    public async Task CreateAccount_WithInvalidDomainInput_ReturnsValidationProblem(
        string name,
        string currency,
        string expectedField)
    {
        var request = new CreateAccountRequest(name, "BankAccount", currency);

        var result = await AccountEndpoints.CreateAccountAsync(request, TestUsers.AuthenticatedA, _handler, CancellationToken.None);

        var problem = Assert.IsType<ValidationProblem>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Contains(expectedField, problem.ProblemDetails.Errors.Keys);
        Assert.Empty(_repository.Accounts);
    }

    [Fact]
    public async Task GetAccounts_ReturnsOkWithAccountData()
    {
        var account = Account.Create(TestUsers.A, "Main account", AccountType.BankAccount, "EUR", UtcNow);
        _repository.Accounts.Add(account);

        var result = await AccountEndpoints.GetAccountsAsync(
            TestUsers.AuthenticatedA,
            new GetAccountsHandler(_repository),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        var response = Assert.Single(result.Value!);
        Assert.Equal(account.Id, response.Id);
        Assert.Equal("Main account", response.Name);
        Assert.Equal("BankAccount", response.Type);
        Assert.Equal("EUR", response.Currency);
        Assert.Equal(UtcNow, response.CreatedAtUtc);
    }

    [Fact]
    public async Task GetAccounts_WithNoAccounts_ReturnsOkWithEmptyArray()
    {
        var result = await AccountEndpoints.GetAccountsAsync(
            TestUsers.AuthenticatedA,
            new GetAccountsHandler(_repository),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.NotNull(result.Value);
        Assert.Empty(result.Value);
    }

    // ---- UpdateAccount ----

    [Fact]
    public async Task UpdateAccount_WithValidRequest_ReturnsOkWithTheUpdatedAccount()
    {
        var account = AddAccount(TestUsers.A);

        var result = await UpdateAsync(account.Id, new UpdateAccountRequest(" Everyday ", "savings"));

        var ok = Assert.IsType<Ok<AccountResponse>>(result.Result);
        Assert.Equal(account.Id, ok.Value!.Id);
        Assert.Equal("Everyday", ok.Value.Name);
        Assert.Equal("Savings", ok.Value.Type);
        Assert.Equal("EUR", ok.Value.Currency);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Crypto")]
    [InlineData("1")]
    public async Task UpdateAccount_WithInvalidType_ReturnsValidationProblemForType(string? type)
    {
        var account = AddAccount(TestUsers.A);

        var result = await UpdateAsync(account.Id, new UpdateAccountRequest("Main", type!));

        var problem = Assert.IsType<ValidationProblem>(result.Result);
        Assert.Contains("type", problem.ProblemDetails.Errors.Keys);
        Assert.Equal(AccountType.BankAccount, _repository.Stored(account.Id).AccountType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task UpdateAccount_WithBlankName_ReturnsValidationProblemForName(string name)
    {
        var account = AddAccount(TestUsers.A);

        var result = await UpdateAsync(account.Id, new UpdateAccountRequest(name, "Cash"));

        var problem = Assert.IsType<ValidationProblem>(result.Result);
        Assert.Contains("name", problem.ProblemDetails.Errors.Keys);
        Assert.Equal("Main account", _repository.Stored(account.Id).Name);
    }

    [Fact]
    public async Task UpdateAccount_AnotherUsersAccount_Returns404()
    {
        var accountOfB = AddAccount(TestUsers.B);

        var result = await UpdateAsync(accountOfB.Id, new UpdateAccountRequest("Mine", "Cash"));

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        Assert.Equal("Main account", _repository.Stored(accountOfB.Id).Name);
    }

    // ---- DeleteAccount ----

    [Fact]
    public async Task DeleteAccount_Unused_ReturnsNoContent()
    {
        var account = AddAccount(TestUsers.A);

        var result = await DeleteAsync(account.Id, new InMemoryTransactionRepository());

        Assert.IsType<NoContent>(result.Result);
        Assert.Empty(_repository.Accounts);
    }

    [Fact]
    public async Task DeleteAccount_WithTransactions_Returns409WithAReadableReason()
    {
        var account = AddAccount(TestUsers.A);
        var transactions = new InMemoryTransactionRepository();
        transactions.Transactions.Add(
            Transaction.CreateExpense(TestUsers.A, account.Id, Guid.CreateVersion7(), 5m, "EUR", UtcNow, null, UtcNow));

        var result = await DeleteAsync(account.Id, transactions);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        Assert.Equal("This account can't be deleted because it has transactions.", problem.ProblemDetails.Detail);
        Assert.Single(_repository.Accounts);
    }

    [Fact]
    public async Task DeleteAccount_ChangedWhileDeleting_Returns409Retryable()
    {
        var account = AddAccount(TestUsers.A);
        _repository.RejectDeleteWith = AccountDeleteOutcome.Changed;

        var result = await DeleteAsync(account.Id, new InMemoryTransactionRepository());

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        Assert.Equal("The account changed while it was being deleted. Please try again.", problem.ProblemDetails.Detail);
    }

    [Fact]
    public async Task DeleteAccount_AnotherUsersAccount_Returns404()
    {
        var accountOfB = AddAccount(TestUsers.B);

        var result = await DeleteAsync(accountOfB.Id, new InMemoryTransactionRepository());

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        Assert.Single(_repository.Accounts);
    }

    private Task<Results<Ok<AccountResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid accountId,
        UpdateAccountRequest request) =>
        AccountEndpoints.UpdateAccountAsync(
            accountId,
            request,
            TestUsers.AuthenticatedA,
            new UpdateAccountHandler(_repository),
            CancellationToken.None);

    private Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid accountId, InMemoryTransactionRepository transactions) =>
        AccountEndpoints.DeleteAccountAsync(
            accountId,
            TestUsers.AuthenticatedA,
            new DeleteAccountHandler(_repository, transactions),
            CancellationToken.None);

    private Account AddAccount(Guid userId)
    {
        var account = Account.Create(userId, "Main account", AccountType.BankAccount, "EUR", UtcNow);
        _repository.Accounts.Add(account);

        return account;
    }
}
