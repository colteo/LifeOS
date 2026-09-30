using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Finance.Accounts;
using LifeOS.Contracts.Finance.Categories;
using LifeOS.Contracts.Finance.Transactions;

namespace LifeOS.IntegrationTests.Http;

// Account management through the real pipeline: edit name/type, delete under the safe rules.
public class AccountManagementHttpTests
{
    private static readonly DateTimeOffset OccurredAtUtc = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Update_RenamesAndChangesTheType_CurrencyStays()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var account = await CreateAccountAsync(client, "Main", "EUR");

        // A currency in the body is not part of the contract and is ignored.
        var response = await client.PutAsJsonAsync(
            $"/api/accounts/{account.Id}",
            new { name = " Everyday ", type = "creditcard", currency = "USD" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<AccountResponse>())!;
        Assert.Equal("Everyday", updated.Name);
        Assert.Equal("CreditCard", updated.Type);
        Assert.Equal("EUR", updated.Currency);

        var listed = Assert.Single((await client.GetFromJsonAsync<List<AccountResponse>>("/api/accounts"))!);
        Assert.Equal(("Everyday", "CreditCard", "EUR"), (listed.Name, listed.Type, listed.Currency));
    }

    [Theory]
    [InlineData("", "Cash", "name")]
    [InlineData("Main", "Crypto", "type")]
    public async Task Update_WithInvalidInput_Returns400AndChangesNothing(string name, string type, string field)
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var account = await CreateAccountAsync(client, "Main", "EUR");

        var response = await client.PutAsJsonAsync($"/api/accounts/{account.Id}", new UpdateAccountRequest(name, type));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync());
        var listed = Assert.Single((await client.GetFromJsonAsync<List<AccountResponse>>("/api/accounts"))!);
        Assert.Equal(("Main", "BankAccount"), (listed.Name, listed.Type));
    }

    [Fact]
    public async Task UpdateAndDelete_AnotherUsersAccount_Return404AndChangeNothing()
    {
        await using var factory = new LifeOSApiFactory();
        var userA = await SignInAsync(factory, "user-a");
        var userB = await SignInAsync(factory, "user-b");
        var accountOfA = await CreateAccountAsync(userA, "Main", "EUR");

        var update = await userB.PutAsJsonAsync($"/api/accounts/{accountOfA.Id}", new UpdateAccountRequest("Taken", "Cash"));
        var delete = await userB.DeleteAsync($"/api/accounts/{accountOfA.Id}");

        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        var listed = Assert.Single((await userA.GetFromJsonAsync<List<AccountResponse>>("/api/accounts"))!);
        Assert.Equal("Main", listed.Name);
    }

    [Fact]
    public async Task UpdateAndDelete_MissingAccount_Return404()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var missing = Guid.CreateVersion7();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"/api/accounts/{missing}", new UpdateAccountRequest("Main", "Cash"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/accounts/{missing}")).StatusCode);
    }

    [Fact]
    public async Task Delete_AccountWithOpeningBalance_DeletesBoth()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var account = await CreateAccountAsync(client, "Main", "EUR");
        var setBalance = await client.PutAsJsonAsync(
            $"/api/accounts/{account.Id}/opening-balance",
            new OpeningBalanceRequest(250m, DateTimeOffset.UtcNow.AddDays(-1)));
        Assert.Equal(HttpStatusCode.Created, setBalance.StatusCode);

        var response = await client.DeleteAsync($"/api/accounts/{account.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<AccountResponse>>("/api/accounts"))!);
        Assert.Empty((await client.GetFromJsonAsync<List<AccountBalanceResponse>>("/api/accounts/balances"))!);
        Assert.Empty(factory.OpeningBalances.OpeningBalances);

        // Deleting again: it no longer exists.
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/accounts/{account.Id}")).StatusCode);
    }

    [Theory]
    [InlineData("expense")]
    [InlineData("transfer-source")]
    [InlineData("transfer-destination")]
    public async Task Delete_AccountWithTransactions_Returns409WithReasonAndKeepsIt(string reference)
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var account = await CreateAccountAsync(client, "Main", "EUR");
        var other = await CreateAccountAsync(client, "Other", "EUR");
        var category = await CreateCategoryAsync(client, "Groceries");

        var transaction = reference switch
        {
            "expense" => new CreateTransactionRequest("Expense", 12.50m, account.Id, null, null, category.Id, OccurredAtUtc, null),
            "transfer-source" => new CreateTransactionRequest("Transfer", 5m, null, account.Id, other.Id, null, OccurredAtUtc, null),
            _ => new CreateTransactionRequest("Transfer", 5m, null, other.Id, account.Id, null, OccurredAtUtc, null)
        };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/transactions", transaction)).StatusCode);

        var response = await client.DeleteAsync($"/api/accounts/{account.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("This account can't be deleted because it has transactions.", body);
        Assert.DoesNotContain("FK_", body);
        Assert.Equal(2, (await client.GetFromJsonAsync<List<AccountResponse>>("/api/accounts"))!.Count);
    }

    [Fact]
    public async Task Delete_TheLastAccountAfterOnboarding_KeepsOnboardingCompleted()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/onboarding/finance-profile", new { defaultCurrency = "EUR" })).StatusCode);
        var account = await CreateAccountAsync(client, "Only", "EUR");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/onboarding/complete", content: null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/accounts/{account.Id}")).StatusCode);

        Assert.Empty((await client.GetFromJsonAsync<List<AccountResponse>>("/api/accounts"))!);
        Assert.Contains("\"Completed\"", await client.GetStringAsync("/api/me"));
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

    private static async Task<AccountResponse> CreateAccountAsync(HttpClient client, string name, string currency)
    {
        var response = await client.PostAsJsonAsync("/api/accounts", new CreateAccountRequest(name, "BankAccount", currency));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<AccountResponse>())!;
    }

    private static async Task<CategoryResponse> CreateCategoryAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/categories", new CreateCategoryRequest(name, "Expense", null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<CategoryResponse>())!;
    }
}
