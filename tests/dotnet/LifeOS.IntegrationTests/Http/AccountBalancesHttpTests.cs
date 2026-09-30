using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Finance.Accounts;
using LifeOS.Contracts.Finance.Transactions;

namespace LifeOS.IntegrationTests.Http;

public class AccountBalancesHttpTests
{
    [Fact]
    public async Task CreateAccount_WithOpeningBalance_KeepsAccountResponseUnchanged_AndBalancesShowIt()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var asOf = DateTimeOffset.UtcNow.AddMinutes(-1);

        var created = await client.PostAsJsonAsync(
            "/api/accounts",
            new CreateAccountRequest("Card", "CreditCard", "EUR", new OpeningBalanceRequest(-350m, asOf)));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.DoesNotContain("openingBalance", await created.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var balance = Assert.Single((await client.GetFromJsonAsync<List<AccountBalanceResponse>>("/api/accounts/balances"))!);
        Assert.Equal(-350m, balance.Balance);
        Assert.Equal("EUR", balance.Currency);
        Assert.Equal(-350m, balance.OpeningBalance!.Amount);
    }

    [Fact]
    public async Task CreateAccount_WithoutAsOf_Returns400()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");

        var response = await client.PostAsJsonAsync(
            "/api/accounts",
            new CreateAccountRequest("Checking", "BankAccount", "EUR", new OpeningBalanceRequest(10m, null)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Accounts.Accounts);
    }

    [Fact]
    public async Task CreateAccount_WithFutureOpeningBalance_Returns400AndCreatesNothing()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");

        var response = await client.PostAsJsonAsync(
            "/api/accounts",
            new CreateAccountRequest("Checking", "BankAccount", "EUR", new OpeningBalanceRequest(10m, DateTimeOffset.UtcNow.AddHours(1))));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Accounts.Accounts);
        Assert.Empty(factory.OpeningBalances.OpeningBalances);
    }

    [Fact]
    public async Task SetOpeningBalance_CreatedThenUnchangedThenConflict()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var account = await CreateAccountAsync(client, "Checking");
        var path = $"/api/accounts/{account.Id}/opening-balance";
        var asOf = DateTimeOffset.UtcNow.AddHours(-1);

        var first = await client.PutAsJsonAsync(path, new OpeningBalanceRequest(1000m, asOf));
        var same = await client.PutAsJsonAsync(path, new OpeningBalanceRequest(1000m, asOf));
        var different = await client.PutAsJsonAsync(path, new OpeningBalanceRequest(999m, asOf));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);
        Assert.Equal(1000m, Assert.Single(factory.OpeningBalances.OpeningBalances).Amount);
    }

    [Fact]
    public async Task SetOpeningBalance_OnAnotherUsersAccount_Returns404()
    {
        await using var factory = new LifeOSApiFactory();
        var userA = await SignInAsync(factory, "user-a");
        var userB = await SignInAsync(factory, "user-b");
        var accountOfA = await CreateAccountAsync(userA, "Checking");

        var response = await userB.PutAsJsonAsync(
            $"/api/accounts/{accountOfA.Id}/opening-balance",
            new OpeningBalanceRequest(1m, DateTimeOffset.UtcNow.AddHours(-1)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(factory.OpeningBalances.OpeningBalances);
    }

    [Fact]
    public async Task Balances_AreDerivedAndScopedToTheCaller()
    {
        await using var factory = new LifeOSApiFactory();
        var userA = await SignInAsync(factory, "user-a");
        var userB = await SignInAsync(factory, "user-b");
        var checking = await CreateAccountAsync(userA, "Checking", new OpeningBalanceRequest(1000m, DateTimeOffset.UtcNow.AddDays(-2)));
        var savings = await CreateAccountAsync(userA, "Savings");
        await CreateAccountAsync(userB, "Other");

        var transfer = await userA.PostAsJsonAsync("/api/transactions", new CreateTransactionRequest(
            "Transfer", 200m, null, checking.Id, savings.Id, null, DateTimeOffset.UtcNow.AddDays(-1), null));
        Assert.Equal(HttpStatusCode.Created, transfer.StatusCode);

        var balances = (await userA.GetFromJsonAsync<List<AccountBalanceResponse>>("/api/accounts/balances"))!;

        Assert.Equal(2, balances.Count);
        Assert.Equal(800m, balances.Single(balance => balance.AccountId == checking.Id).Balance);
        Assert.Equal(200m, balances.Single(balance => balance.AccountId == savings.Id).Balance);
        Assert.Null(balances.Single(balance => balance.AccountId == savings.Id).OpeningBalance);
    }

    [Fact]
    public async Task Balances_AtAnInstantBeforeTheBaseline_AreNull()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        await CreateAccountAsync(client, "Checking", new OpeningBalanceRequest(1000m, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)));

        var balances = (await client.GetFromJsonAsync<List<AccountBalanceResponse>>("/api/accounts/balances?atUtc=2026-08-15T00:00:00Z"))!;

        Assert.Null(Assert.Single(balances).Balance);
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("2026-09-01T00:00:00")]        // no offset
    [InlineData("2026-09-01T02:00:00+02:00")]  // not UTC
    public async Task Balances_WithInvalidAtUtc_Return400(string atUtc)
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");

        var response = await client.GetAsync($"/api/accounts/balances?atUtc={Uri.EscapeDataString(atUtc)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BalancesAndOpeningBalance_WithoutAccessToken_Return401()
    {
        await using var factory = new LifeOSApiFactory();
        var client = factory.CreateClient();

        var balances = await client.GetAsync("/api/accounts/balances");
        var set = await client.PutAsJsonAsync(
            $"/api/accounts/{Guid.CreateVersion7()}/opening-balance",
            new OpeningBalanceRequest(1m, DateTimeOffset.UtcNow));

        Assert.Equal(HttpStatusCode.Unauthorized, balances.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, set.StatusCode);
    }

    private static async Task<HttpClient> SignInAsync(LifeOSApiFactory factory, string subject)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/dev/sign-in", new DevSignInRequest(subject, null, null));
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        return client;
    }

    private static async Task<AccountResponse> CreateAccountAsync(HttpClient client, string name, OpeningBalanceRequest? openingBalance = null)
    {
        var response = await client.PostAsJsonAsync("/api/accounts", new CreateAccountRequest(name, "BankAccount", "EUR", openingBalance));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<AccountResponse>())!;
    }
}
