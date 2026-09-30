using LifeOS.Api.Finance;
using LifeOS.Application.Finance.Accounts.CreateAccount;
using LifeOS.Application.Finance.Accounts.GetAccounts;
using LifeOS.Contracts.Finance.Accounts;
using LifeOS.Domain.Finance.Accounts;
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
}
