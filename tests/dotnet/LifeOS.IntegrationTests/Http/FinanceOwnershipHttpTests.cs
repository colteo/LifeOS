using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Finance.Accounts;
using LifeOS.Contracts.Finance.Categories;
using LifeOS.Contracts.Finance.Transactions;

namespace LifeOS.IntegrationTests.Http;

// Finance data is owned by the authenticated user: other users can neither see it nor reference it.
public class FinanceOwnershipHttpTests
{
    private const string Range = "fromUtc=2026-09-01T00:00:00Z&toUtc=2026-10-01T00:00:00Z";
    private static readonly DateTimeOffset OccurredAtUtc = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("GET", "/api/accounts")]
    [InlineData("POST", "/api/accounts")]
    [InlineData("PUT", "/api/accounts/0199a0f0-0000-7000-8000-000000000001")]
    [InlineData("DELETE", "/api/accounts/0199a0f0-0000-7000-8000-000000000001")]
    [InlineData("GET", "/api/categories")]
    [InlineData("POST", "/api/categories")]
    [InlineData("PUT", "/api/categories/0199a0f0-0000-7000-8000-000000000002")]
    [InlineData("DELETE", "/api/categories/0199a0f0-0000-7000-8000-000000000002")]
    [InlineData("GET", "/api/transactions?" + Range)]
    [InlineData("POST", "/api/transactions")]
    public async Task FinanceEndpoints_WithoutAccessToken_Return401(string method, string path)
    {
        await using var factory = new LifeOSApiFactory();
        var request = new HttpRequestMessage(new HttpMethod(method), path);

        if (method is "POST" or "PUT")
        {
            request.Content = JsonContent.Create(new { });
        }

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.Accounts.Accounts);
        Assert.Empty(factory.Categories.Categories);
        Assert.Empty(factory.Transactions.Transactions);
    }

    [Fact]
    public async Task Accounts_AreVisibleOnlyToTheirOwner()
    {
        await using var factory = new LifeOSApiFactory();
        var userA = await SignInAsync(factory, "user-a");
        var userB = await SignInAsync(factory, "user-b");

        var created = await CreateAccountAsync(userA, "Account A");

        var listA = await userA.GetFromJsonAsync<List<AccountResponse>>("/api/accounts");
        var listB = await userB.GetFromJsonAsync<List<AccountResponse>>("/api/accounts");
        Assert.Equal(created.Id, Assert.Single(listA!).Id);
        Assert.Empty(listB!);
    }

    [Fact]
    public async Task Categories_AreVisibleOnlyToTheirOwner_AndNamesAreIndependent()
    {
        await using var factory = new LifeOSApiFactory();
        var userA = await SignInAsync(factory, "user-a");
        var userB = await SignInAsync(factory, "user-b");

        var ofA = await CreateCategoryAsync(userA, "Casa", parentCategoryId: null);
        var ofB = await CreateCategoryAsync(userB, "Casa", parentCategoryId: null);

        var listA = await userA.GetFromJsonAsync<List<CategoryResponse>>("/api/categories");
        var listB = await userB.GetFromJsonAsync<List<CategoryResponse>>("/api/categories");
        Assert.Equal(ofA.Id, Assert.Single(listA!).Id);
        Assert.Equal(ofB.Id, Assert.Single(listB!).Id);
    }

    [Fact]
    public async Task Transactions_AreVisibleOnlyToTheirOwner()
    {
        await using var factory = new LifeOSApiFactory();
        var userA = await SignInAsync(factory, "user-a");
        var userB = await SignInAsync(factory, "user-b");
        var accountA = await CreateAccountAsync(userA, "Account A");
        var categoryA = await CreateCategoryAsync(userA, "Category A", parentCategoryId: null);

        var expense = await userA.PostAsJsonAsync("/api/transactions", Expense(accountA.Id, categoryA.Id));
        Assert.Equal(HttpStatusCode.Created, expense.StatusCode);

        var listA = await userA.GetFromJsonAsync<List<TransactionResponse>>("/api/transactions?" + Range);
        var listB = await userB.GetFromJsonAsync<List<TransactionResponse>>("/api/transactions?" + Range);
        Assert.Single(listA!);
        Assert.Empty(listB!);
    }

    [Fact]
    public async Task ReferencingAnotherUsersAccount_Returns404()
    {
        await using var factory = new LifeOSApiFactory();
        var userA = await SignInAsync(factory, "user-a");
        var userB = await SignInAsync(factory, "user-b");
        var accountA = await CreateAccountAsync(userA, "Account A");
        var accountB = await CreateAccountAsync(userB, "Account B");
        var categoryB = await CreateCategoryAsync(userB, "Category B", parentCategoryId: null);

        var expense = await userB.PostAsJsonAsync("/api/transactions", Expense(accountA.Id, categoryB.Id));
        var transferFrom = await userB.PostAsJsonAsync("/api/transactions", Transfer(accountA.Id, accountB.Id));
        var transferTo = await userB.PostAsJsonAsync("/api/transactions", Transfer(accountB.Id, accountA.Id));

        Assert.Equal(HttpStatusCode.NotFound, expense.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, transferFrom.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, transferTo.StatusCode);
        Assert.Empty(factory.Transactions.Transactions);
    }

    [Fact]
    public async Task ReferencingAnotherUsersCategory_Returns404()
    {
        await using var factory = new LifeOSApiFactory();
        var userA = await SignInAsync(factory, "user-a");
        var userB = await SignInAsync(factory, "user-b");
        var categoryA = await CreateCategoryAsync(userA, "Category A", parentCategoryId: null);
        var accountB = await CreateAccountAsync(userB, "Account B");

        var expense = await userB.PostAsJsonAsync("/api/transactions", Expense(accountB.Id, categoryA.Id));
        var subcategory = await userB.PostAsJsonAsync(
            "/api/categories",
            new CreateCategoryRequest("Sub of A", "Expense", categoryA.Id));

        Assert.Equal(HttpStatusCode.NotFound, expense.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, subcategory.StatusCode);
        Assert.Empty(factory.Transactions.Transactions);
        Assert.Single(factory.Categories.Categories);
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

    private static async Task<AccountResponse> CreateAccountAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/accounts", new CreateAccountRequest(name, "BankAccount", "EUR"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<AccountResponse>())!;
    }

    private static async Task<CategoryResponse> CreateCategoryAsync(HttpClient client, string name, Guid? parentCategoryId)
    {
        var response = await client.PostAsJsonAsync("/api/categories", new CreateCategoryRequest(name, "Expense", parentCategoryId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<CategoryResponse>())!;
    }

    private static CreateTransactionRequest Expense(Guid accountId, Guid categoryId) =>
        new("Expense", 12.50m, accountId, null, null, categoryId, OccurredAtUtc, null);

    private static CreateTransactionRequest Transfer(Guid sourceAccountId, Guid destinationAccountId) =>
        new("Transfer", 100m, null, sourceAccountId, destinationAccountId, null, OccurredAtUtc, null);
}
