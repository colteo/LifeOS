using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Finance.Accounts;
using LifeOS.Contracts.Finance.Analytics;
using LifeOS.Contracts.Finance.Categories;
using LifeOS.Contracts.Finance.Transactions;

namespace LifeOS.IntegrationTests.Http;

// Monthly analytics through the real pipeline: authentication, validation, ownership and the
// response shape built from transactions created through the API.
public class AnalyticsHttpTests
{
    private const string September = "fromUtc=2026-08-31T22:00:00Z&toUtc=2026-09-30T22:00:00Z";
    private static readonly DateTimeOffset InSeptember = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WithoutAccessToken_Returns401()
    {
        await using var factory = new LifeOSApiFactory();

        var response = await factory.CreateClient().GetAsync($"/api/analytics/monthly?{September}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Month_ReturnsTotalsAndTheExpenseTree_PerCurrency()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var checking = await CreateAccountAsync(client, "Checking", "EUR");
        var savings = await CreateAccountAsync(client, "Savings", "EUR");
        var dollars = await CreateAccountAsync(client, "Dollars", "USD");
        var food = await CreateCategoryAsync(client, "Food & Drink", "Expense", null);
        var groceries = await CreateCategoryAsync(client, "Groceries", "Expense", food.Id);
        var salary = await CreateCategoryAsync(client, "Salary", "Income", null);

        await PostAsync(client, new CreateTransactionRequest("Expense", 30m, checking.Id, null, null, food.Id, InSeptember, null));
        await PostAsync(client, new CreateTransactionRequest("Expense", 190m, checking.Id, null, null, groceries.Id, InSeptember, null));
        await PostAsync(client, new CreateTransactionRequest("Income", 1500m, checking.Id, null, null, salary.Id, InSeptember, null));
        await PostAsync(client, new CreateTransactionRequest("Transfer", 400m, null, checking.Id, savings.Id, null, InSeptember, null));
        await PostAsync(client, new CreateTransactionRequest("Expense", 12m, dollars.Id, null, null, groceries.Id, InSeptember, null));
        // October: outside the requested month.
        await PostAsync(client, new CreateTransactionRequest("Expense", 999m, checking.Id, null, null, food.Id, InSeptember.AddMonths(1), null));

        var analytics = await GetAsync(client, September);

        Assert.Equal(new DateTimeOffset(2026, 8, 31, 22, 0, 0, TimeSpan.Zero), analytics.FromUtc);
        Assert.Equal(["EUR", "USD"], analytics.Currencies.Select(currency => currency.Currency));

        var eur = analytics.Currencies[0];
        Assert.Equal((220m, 1500m, 1280m), (eur.Expenses, eur.Income, eur.NetFlow));
        var foodGroup = Assert.Single(eur.ExpenseCategories);
        Assert.Equal((food.Id, "Food & Drink", 220m, 30m), (foodGroup.CategoryId!.Value, foodGroup.Name, foodGroup.Amount, foodGroup.DirectAmount));
        var groceriesRow = Assert.Single(foodGroup.Subcategories);
        Assert.Equal((groceries.Id, "Groceries", 190m), (groceriesRow.CategoryId, groceriesRow.Name, groceriesRow.Amount));

        var usd = analytics.Currencies[1];
        Assert.Equal((12m, 0m, -12m), (usd.Expenses, usd.Income, usd.NetFlow));
        Assert.Equal(12m, Assert.Single(usd.ExpenseCategories).Amount);
    }

    [Fact]
    public async Task EmptyMonth_ReturnsNoCurrencyBlocks()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");

        Assert.Empty((await GetAsync(client, September)).Currencies);
    }

    [Fact]
    public async Task AnotherUsersActivity_IsInvisible()
    {
        await using var factory = new LifeOSApiFactory();
        var userA = await SignInAsync(factory, "user-a");
        var userB = await SignInAsync(factory, "user-b");
        var account = await CreateAccountAsync(userA, "Checking", "EUR");
        var travel = await CreateCategoryAsync(userA, "Travel", "Expense", null);
        await PostAsync(userA, new CreateTransactionRequest("Expense", 80m, account.Id, null, null, travel.Id, InSeptember, null));

        Assert.Empty((await GetAsync(userB, September)).Currencies);
        Assert.Equal(80m, Assert.Single((await GetAsync(userA, September)).Currencies).Expenses);
    }

    [Theory]
    [InlineData("toUtc=2026-09-30T22:00:00Z", "fromUtc")]
    [InlineData("fromUtc=2026-08-31T22:00:00Z", "toUtc")]
    [InlineData("fromUtc=2026-09&toUtc=2026-10", "fromUtc")]
    [InlineData("fromUtc=2026-08-31T22:00:00&toUtc=2026-09-30T22:00:00Z", "fromUtc")]
    [InlineData("fromUtc=2026-09-01T00:00:00%2B02:00&toUtc=2026-09-30T22:00:00Z", "fromUtc")]
    [InlineData("fromUtc=2026-09-30T22:00:00Z&toUtc=2026-08-31T22:00:00Z", "toUtc")]
    [InlineData("fromUtc=2026-01-01T00:00:00Z&toUtc=2026-12-31T00:00:00Z", "toUtc")]
    public async Task InvalidRange_Returns400ForTheField(string query, string field)
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");

        var response = await client.GetAsync($"/api/analytics/monthly?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync());
    }

    private static async Task<MonthlyAnalyticsResponse> GetAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/analytics/monthly?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<MonthlyAnalyticsResponse>())!;
    }

    private static async Task PostAsync(HttpClient client, CreateTransactionRequest request) =>
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/transactions", request)).StatusCode);

    private static async Task<HttpClient> SignInAsync(LifeOSApiFactory factory, string subject)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/dev/sign-in", new DevSignInRequest(subject, null, null));
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        return client;
    }

    private static async Task<AccountResponse> CreateAccountAsync(HttpClient client, string name, string currency)
    {
        var response = await client.PostAsJsonAsync("/api/accounts", new CreateAccountRequest(name, "BankAccount", currency));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<AccountResponse>())!;
    }

    private static async Task<CategoryResponse> CreateCategoryAsync(HttpClient client, string name, string type, Guid? parentId)
    {
        var response = await client.PostAsJsonAsync("/api/categories", new CreateCategoryRequest(name, type, parentId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<CategoryResponse>())!;
    }
}
