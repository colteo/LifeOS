using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Finance.Accounts;
using LifeOS.Contracts.Finance.Budgets;
using LifeOS.Contracts.Finance.Categories;
using LifeOS.Contracts.Finance.Transactions;

namespace LifeOS.IntegrationTests.Http;

public class MonthlyBudgetHttpTests
{
    private const string Path = "/api/budgets/2026/10/EUR";
    private const string Range = "?fromUtc=2026-09-30T22:00:00Z&toUtc=2026-10-31T23:00:00Z&utcOffsetMinutes=120";
    private static readonly DateTimeOffset Occurred = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MissingSetUpdateRemove_AreIndependentOfTransactions()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignIn(factory, "a");
        Assert.Null((await Read(client)).Budget);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(Path, new SetMonthlyBudgetRequest(1500))).StatusCode);
        var budget = (await Read(client)).Budget!;
        Assert.Equal((1500m, 0m, 1500m), (budget.Amount, budget.Spent, budget.Remaining));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(Path, new SetMonthlyBudgetRequest(2000))).StatusCode);
        Assert.Equal(2000, (await Read(client)).Budget!.Amount);
        Assert.Empty(factory.Transactions.Transactions);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Path)).StatusCode);
        Assert.Null((await Read(client)).Budget);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Path)).StatusCode);
    }

    [Fact]
    public async Task OwnershipCurrenciesAndActualExpenseFiltering_AreEnforced()
    {
        await using var factory = new LifeOSApiFactory();
        var a = await SignIn(factory, "a");
        var b = await SignIn(factory, "b");
        var account = await Create<AccountResponse>(a, "/api/accounts", new CreateAccountRequest("Cash", "Cash", "EUR"));
        var destination = await Create<AccountResponse>(a, "/api/accounts", new CreateAccountRequest("Bank", "BankAccount", "EUR"));
        var dollars = await Create<AccountResponse>(a, "/api/accounts", new CreateAccountRequest("Dollars", "Cash", "USD"));
        var expense = await Create<CategoryResponse>(a, "/api/categories", new CreateCategoryRequest("Food", "Expense", null));
        var income = await Create<CategoryResponse>(a, "/api/categories", new CreateCategoryRequest("Salary", "Income", null));
        await a.PutAsJsonAsync(Path, new SetMonthlyBudgetRequest(1500));
        await Create<TransactionResponse>(a, "/api/transactions", new CreateTransactionRequest("Expense", 620, account.Id, null, null, expense.Id, Occurred, null));
        await Create<TransactionResponse>(a, "/api/transactions", new CreateTransactionRequest("Income", 999, account.Id, null, null, income.Id, Occurred, null));
        await Create<TransactionResponse>(a, "/api/transactions", new CreateTransactionRequest("Transfer", 999, null, account.Id, destination.Id, null, Occurred, null));
        await Create<TransactionResponse>(a, "/api/transactions", new CreateTransactionRequest("Expense", 12, dollars.Id, null, null, expense.Id, Occurred, null));
        await Create<TransactionResponse>(a, "/api/transactions", new CreateTransactionRequest("Expense", 999, account.Id, null, null, expense.Id, Occurred.AddMonths(-1), null));
        Assert.Null((await Read(b)).Budget);
        await b.PutAsJsonAsync(Path, new SetMonthlyBudgetRequest(100));
        Assert.Equal(0, (await Read(b)).Budget!.Spent);
        await b.PutAsJsonAsync(Path, new SetMonthlyBudgetRequest(200));
        await b.DeleteAsync(Path);
        var result = (await Read(a)).Budget!;
        Assert.Equal((1500m, 620m, 880m), (result.Amount, result.Spent, result.Remaining));
        var usdPath = Path.Replace("EUR", "USD");
        await a.PutAsJsonAsync(usdPath, new SetMonthlyBudgetRequest(100));
        var usd = (await a.GetFromJsonAsync<GetMonthlyBudgetResponse>(usdPath + Range))!.Budget!;
        Assert.Equal((12m, 88m), (usd.Spent, usd.Remaining));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.00001)]
    public async Task InvalidAmount_Returns400(decimal amount)
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignIn(factory, "a");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Path, new SetMonthlyBudgetRequest(amount))).StatusCode);
        Assert.Null((await Read(client)).Budget);
    }

    [Theory]
    [InlineData("/api/budgets/2026/13/EUR")]
    [InlineData("/api/budgets/0/10/EUR")]
    [InlineData("/api/budgets/2026/10/EU")]
    public async Task InvalidKey_Returns400ForEveryOperation(string path)
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignIn(factory, "a");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + Range)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path, new SetMonthlyBudgetRequest(10))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync(path)).StatusCode);
    }

    [Fact]
    public async Task MalformedRange_Returns400_AndEveryOperationRequiresAuthentication()
    {
        await using var factory = new LifeOSApiFactory();
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Path + Range)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(Path, new SetMonthlyBudgetRequest(1))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync(Path)).StatusCode);
        var client = await SignIn(factory, "a");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Path + "?fromUtc=bad&toUtc=bad&utcOffsetMinutes=0")).StatusCode);
    }

    private static async Task<GetMonthlyBudgetResponse> Read(HttpClient client) =>
        (await client.GetFromJsonAsync<GetMonthlyBudgetResponse>(Path + Range))!;
    private static async Task<T> Create<T>(HttpClient client, string path, object request)
    {
        var response = await client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
    private static async Task<HttpClient> SignIn(LifeOSApiFactory factory, string subject)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/dev/sign-in", new DevSignInRequest(subject, null, null));
        response.EnsureSuccessStatusCode();
        var tokens = (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }
}
