using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Finance.Accounts;
using LifeOS.Contracts.Finance.Categories;
using LifeOS.Contracts.Finance.Transactions;

namespace LifeOS.IntegrationTests.Http;

public class RecentTransactionsHttpTests
{
    private const string RecentPath = "/api/transactions/recent";

    [Fact]
    public async Task WithoutAccessToken_Returns401()
    {
        await using var factory = new LifeOSApiFactory();

        var response = await factory.CreateClient().GetAsync(RecentPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DefaultLimit_ReturnsTheNewestFive()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var (account, category) = await SetUpAsync(client);

        for (var hours = 0; hours < 7; hours++)
        {
            await CreateExpenseAsync(client, account, category, DateTimeOffset.UtcNow.AddHours(-hours - 1));
        }

        var recent = (await client.GetFromJsonAsync<List<TransactionResponse>>(RecentPath))!;

        Assert.Equal(5, recent.Count);
        Assert.Equal(recent.OrderByDescending(transaction => transaction.OccurredAtUtc), recent);
    }

    [Fact]
    public async Task ExplicitLimit_IsRespected()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var (account, category) = await SetUpAsync(client);

        for (var hours = 0; hours < 3; hours++)
        {
            await CreateExpenseAsync(client, account, category, DateTimeOffset.UtcNow.AddHours(-hours - 1));
        }

        var recent = (await client.GetFromJsonAsync<List<TransactionResponse>>($"{RecentPath}?limit=2"))!;

        Assert.Equal(2, recent.Count);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("21")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("2.5")]
    public async Task InvalidLimit_Returns400(string limit)
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");

        var response = await client.GetAsync($"{RecentPath}?limit={limit}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnotherUsersTransactions_NeverAppear()
    {
        await using var factory = new LifeOSApiFactory();
        var userA = await SignInAsync(factory, "user-a");
        var userB = await SignInAsync(factory, "user-b");
        var (accountA, categoryA) = await SetUpAsync(userA);
        var (accountB, categoryB) = await SetUpAsync(userB);
        var ofA = await CreateExpenseAsync(userA, accountA, categoryA, DateTimeOffset.UtcNow.AddHours(-2));
        await CreateExpenseAsync(userB, accountB, categoryB, DateTimeOffset.UtcNow.AddHours(-1));

        var recent = (await userA.GetFromJsonAsync<List<TransactionResponse>>(RecentPath))!;

        Assert.Equal(ofA.Id, Assert.Single(recent).Id);
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

    private static async Task<(Guid Account, Guid Category)> SetUpAsync(HttpClient client)
    {
        var account = await client.PostAsJsonAsync("/api/accounts", new CreateAccountRequest("Checking", "BankAccount", "EUR"));
        var category = await client.PostAsJsonAsync("/api/categories", new CreateCategoryRequest("Casa", "Expense", null));

        return ((await account.Content.ReadFromJsonAsync<AccountResponse>())!.Id,
            (await category.Content.ReadFromJsonAsync<CategoryResponse>())!.Id);
    }

    private static async Task<TransactionResponse> CreateExpenseAsync(HttpClient client, Guid account, Guid category, DateTimeOffset occurredAtUtc)
    {
        var response = await client.PostAsJsonAsync(
            "/api/transactions",
            new CreateTransactionRequest("Expense", 10m, account, null, null, category, occurredAtUtc, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<TransactionResponse>())!;
    }
}
