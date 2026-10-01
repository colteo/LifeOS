using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Finance.Accounts;
using LifeOS.Contracts.Finance.Categories;
using LifeOS.Contracts.Finance.Transactions;

namespace LifeOS.IntegrationTests.Http;

// Transaction detail, edit and delete through the real pipeline.
public class TransactionManagementHttpTests
{
    private static readonly DateTimeOffset OccurredAtUtc = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task WithoutAccessToken_Returns401(string method)
    {
        await using var factory = new LifeOSApiFactory();
        var request = new HttpRequestMessage(new HttpMethod(method), $"/api/transactions/{Guid.CreateVersion7()}");

        if (method == "PUT")
        {
            request.Content = JsonContent.Create(new { });
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Get_OwnTransaction_ReturnsTheFullResponse_AnotherUsersIs404()
    {
        await using var factory = new LifeOSApiFactory();
        var (client, data) = await SeedAsync(factory, "user-a");
        var other = await SignInAsync(factory, "user-b");

        var response = await client.GetAsync($"/api/transactions/{data.Expense.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var transaction = (await response.Content.ReadFromJsonAsync<TransactionResponse>())!;
        Assert.Equal(
            (data.Expense.Id, "Expense", 50m, "EUR", (Guid?)data.Checking.Id, (Guid?)data.Groceries.Id, "Lunch", OccurredAtUtc),
            (transaction.Id, transaction.Type, transaction.Amount, transaction.Currency, transaction.AccountId, transaction.CategoryId, transaction.Note, transaction.OccurredAtUtc));
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/transactions/{data.Expense.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/transactions/{Guid.CreateVersion7()}")).StatusCode);
    }

    [Fact]
    public async Task Put_Expense_UpdatesIt_AndATypeInTheBodyIsIgnored()
    {
        await using var factory = new LifeOSApiFactory();
        var (client, data) = await SeedAsync(factory, "user-a");
        var newTime = OccurredAtUtc.AddDays(-3);

        var response = await client.PutAsJsonAsync($"/api/transactions/{data.Expense.Id}", new
        {
            type = "Income",
            amount = 20m,
            occurredAtUtc = newTime,
            note = "Dinner",
            accountTransaction = new { accountId = data.Savings.Id, categoryId = data.Groceries.Id }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<TransactionResponse>())!;
        Assert.Equal(("Expense", 20m, (Guid?)data.Savings.Id, "Dinner", newTime), (updated.Type, updated.Amount, updated.AccountId, updated.Note, updated.OccurredAtUtc));
        var stored = (await client.GetFromJsonAsync<TransactionResponse>($"/api/transactions/{data.Expense.Id}"))!;
        Assert.Equal(("Expense", 20m), (stored.Type, stored.Amount));
    }

    [Fact]
    public async Task Put_Transfer_UpdatesAmountAndEndpoints()
    {
        await using var factory = new LifeOSApiFactory();
        var (client, data) = await SeedAsync(factory, "user-a");

        var response = await client.PutAsJsonAsync(
            $"/api/transactions/{data.Transfer.Id}",
            new UpdateTransactionRequest(75m, OccurredAtUtc, null, null, new TransferUpdate(data.Savings.Id, data.Checking.Id)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<TransactionResponse>())!;
        Assert.Equal(("Transfer", 75m, (Guid?)data.Savings.Id, (Guid?)data.Checking.Id, (Guid?)null), (updated.Type, updated.Amount, updated.SourceAccountId, updated.DestinationAccountId, updated.CategoryId));
    }

    [Theory]
    [InlineData("missing-time", "occurredAtUtc")]
    [InlineData("both-branches", "transfer")]
    [InlineData("transfer-branch-on-expense", "transfer")]
    [InlineData("no-branch", "accountTransaction")]
    [InlineData("no-category", "accountTransaction.categoryId")]
    [InlineData("zero-amount", "amount")]
    public async Task Put_Invalid_Returns400ForTheField_AndChangesNothing(string variant, string field)
    {
        await using var factory = new LifeOSApiFactory();
        var (client, data) = await SeedAsync(factory, "user-a");
        var account = new AccountTransactionUpdate(data.Checking.Id, data.Groceries.Id);
        var transfer = new TransferUpdate(data.Checking.Id, data.Savings.Id);
        var request = variant switch
        {
            "missing-time" => new UpdateTransactionRequest(5m, null, null, account, null),
            "both-branches" => new UpdateTransactionRequest(5m, OccurredAtUtc, null, account, transfer),
            "transfer-branch-on-expense" => new UpdateTransactionRequest(5m, OccurredAtUtc, null, null, transfer),
            "no-branch" => new UpdateTransactionRequest(5m, OccurredAtUtc, null, null, null),
            "no-category" => new UpdateTransactionRequest(5m, OccurredAtUtc, null, new AccountTransactionUpdate(data.Checking.Id, null), null),
            _ => new UpdateTransactionRequest(0m, OccurredAtUtc, null, account, null)
        };

        var response = await client.PutAsJsonAsync($"/api/transactions/{data.Expense.Id}", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync());
        Assert.Equal(50m, (await client.GetFromJsonAsync<TransactionResponse>($"/api/transactions/{data.Expense.Id}"))!.Amount);
    }

    [Fact]
    public async Task Put_AnotherUsersTransactionOrReference_Returns404()
    {
        await using var factory = new LifeOSApiFactory();
        var (client, data) = await SeedAsync(factory, "user-a");
        var (other, otherData) = await SeedAsync(factory, "user-b");

        var foreignTransaction = await other.PutAsJsonAsync(
            $"/api/transactions/{data.Expense.Id}",
            new UpdateTransactionRequest(5m, OccurredAtUtc, null, new AccountTransactionUpdate(otherData.Checking.Id, otherData.Groceries.Id), null));
        var foreignAccount = await client.PutAsJsonAsync(
            $"/api/transactions/{data.Expense.Id}",
            new UpdateTransactionRequest(5m, OccurredAtUtc, null, new AccountTransactionUpdate(otherData.Checking.Id, data.Groceries.Id), null));

        Assert.Equal(HttpStatusCode.NotFound, foreignTransaction.StatusCode);
        Assert.Contains("This transaction no longer exists.", await foreignTransaction.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, foreignAccount.StatusCode);
        Assert.Equal(50m, (await client.GetFromJsonAsync<TransactionResponse>($"/api/transactions/{data.Expense.Id}"))!.Amount);
    }

    [Fact]
    public async Task Delete_OwnTransaction_Returns204_RemovesItFromHistoryAndBalances_AnotherUsersIs404()
    {
        await using var factory = new LifeOSApiFactory();
        var (client, data) = await SeedAsync(factory, "user-a");
        var other = await SignInAsync(factory, "user-b");
        var range = "fromUtc=2026-09-01T00:00:00Z&toUtc=2026-10-01T00:00:00Z";

        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/transactions/{data.Expense.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/transactions/{data.Expense.Id}")).StatusCode);

        var history = (await client.GetFromJsonAsync<List<TransactionResponse>>($"/api/transactions?{range}"))!;
        Assert.DoesNotContain(history, transaction => transaction.Id == data.Expense.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/transactions/{data.Expense.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/transactions/{data.Expense.Id}")).StatusCode);
    }

    private sealed record Seed(AccountResponse Checking, AccountResponse Savings, CategoryResponse Groceries, TransactionResponse Expense, TransactionResponse Transfer);

    private static async Task<(HttpClient Client, Seed Data)> SeedAsync(LifeOSApiFactory factory, string subject)
    {
        var client = await SignInAsync(factory, subject);
        var checking = await PostAsync<AccountResponse>(client, "/api/accounts", new CreateAccountRequest("Checking", "BankAccount", "EUR"));
        var savings = await PostAsync<AccountResponse>(client, "/api/accounts", new CreateAccountRequest("Savings", "Savings", "EUR"));
        var groceries = await PostAsync<CategoryResponse>(client, "/api/categories", new CreateCategoryRequest("Groceries", "Expense", null));
        var expense = await PostAsync<TransactionResponse>(client, "/api/transactions",
            new CreateTransactionRequest("Expense", 50m, checking.Id, null, null, groceries.Id, OccurredAtUtc, "Lunch"));
        var transfer = await PostAsync<TransactionResponse>(client, "/api/transactions",
            new CreateTransactionRequest("Transfer", 100m, null, checking.Id, savings.Id, null, OccurredAtUtc, null));

        return (client, new Seed(checking, savings, groceries, expense, transfer));
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string path, object request)
    {
        var response = await client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<T>())!;
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
}
