using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Finance.Accounts;
using LifeOS.Contracts.Finance.Budgets;
using LifeOS.Contracts.Finance.Categories;
using LifeOS.Contracts.Finance.Transactions;

namespace LifeOS.IntegrationTests.Http;

public class AccountReconciliationHttpTests
{
    [Theory]
    [InlineData(1237.5, 1250, 12.5)]
    [InlineData(1250, 1237.5, -12.5)]
    [InlineData(0, -350, -350)]
    [InlineData(100, 100, 0)]
    public async Task Reconciliation_ReturnsSignedResult_AndImmediatelyReloadsBalance(decimal previous, decimal observed, decimal delta)
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignIn(factory, "a");
        var account = await NewAccount(client, previous);
        var key = Guid.NewGuid();
        var response = await Post(client, account.Id, observed, key, " bank ");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<ReconcileAccountResponse>())!;
        Assert.Equal((previous, observed, delta, observed), (result.PreviousBalance, result.ObservedBalance, result.AdjustmentAmount, result.ResultingBalance));
        Assert.Equal("EUR", result.Currency);
        Assert.Equal(TimeSpan.Zero, result.EffectiveAtUtc.Offset);
        Assert.Equal(delta == 0 ? 0 : 1, factory.Reconciliations.Adjustments.Count);
        Assert.Equal(delta == 0, result.AdjustmentId is null);
        var balances = (await client.GetFromJsonAsync<List<AccountBalanceResponse>>("/api/accounts/balances"))!;
        Assert.Equal(observed, Assert.Single(balances).Balance);
        Assert.Equal(previous, Assert.Single(factory.OpeningBalances.OpeningBalances).Amount);
        var replay = await Post(client, account.Id, observed, key, "bank");
        Assert.Equal(result, await replay.Content.ReadFromJsonAsync<ReconcileAccountResponse>());
        Assert.Single(factory.Reconciliations.Receipts);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(client, account.Id, observed + 1, key, "bank")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/accounts/{account.Id}")).StatusCode);
    }

    [Fact]
    public async Task Ownership_IsHidden_AndAuthenticationIsRequired()
    {
        await using var factory = new LifeOSApiFactory();
        var a = await SignIn(factory, "a");
        var b = await SignIn(factory, "b");
        var account = await NewAccount(a, 100);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(factory.CreateClient(), account.Id, 200, Guid.NewGuid())).StatusCode);
        var foreign = await Post(b, account.Id, 200, Guid.NewGuid());
        var missing = await Post(b, Guid.NewGuid(), 200, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await foreign.Content.ReadAsStringAsync());
        Assert.Empty(factory.Reconciliations.Receipts);
    }

    [Theory]
    [InlineData("1.00001")]
    [InlineData("1000000000000000")]
    [InlineData("-1000000000000000")]
    public async Task InvalidMoney_Returns400(string text)
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignIn(factory, "a");
        var account = await NewAccount(client, 0);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, account.Id,
            decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture), Guid.NewGuid())).StatusCode);
        Assert.Empty(factory.Reconciliations.Receipts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("bad")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task MissingOrInvalidRequestKey_Returns400(string? key)
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignIn(factory, "a");
        var account = await NewAccount(client, 0);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/accounts/{account.Id}/reconciliations")
        { Content = JsonContent.Create(new ReconcileAccountRequest(1, null)) };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task UnavailableCurrentBalance_Returns409WithoutInventingBaseline()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignIn(factory, "a");
        var response = await client.PostAsJsonAsync("/api/accounts", new CreateAccountRequest("Future baseline", "Cash", "EUR",
            new OpeningBalanceRequest(100, DateTimeOffset.UtcNow.AddMinutes(2))));
        response.EnsureSuccessStatusCode();
        var account = (await response.Content.ReadFromJsonAsync<AccountResponse>())!;
        Assert.Equal(HttpStatusCode.Conflict, (await Post(client, account.Id, 100, Guid.NewGuid())).StatusCode);
        Assert.Empty(factory.Reconciliations.Receipts);
    }

    [Fact]
    public async Task ReconciliationAndReplay_DoNotChangeBudgetAnalyticsOrTransactionHistory()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignIn(factory, "a");
        var account = await NewAccount(client, 100);
        var categoryResponse = await client.PostAsJsonAsync("/api/categories", new CreateCategoryRequest("Food", "Expense", null));
        categoryResponse.EnsureSuccessStatusCode();
        var category = (await categoryResponse.Content.ReadFromJsonAsync<CategoryResponse>())!;
        var occurred = DateTimeOffset.UtcNow.AddMinutes(-1);
        var expense = await client.PostAsJsonAsync("/api/transactions", new CreateTransactionRequest("Expense", 10, account.Id, null, null, category.Id, occurred, null));
        expense.EnsureSuccessStatusCode();
        var month = new DateTimeOffset(occurred.Year, occurred.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var range = $"fromUtc={month:yyyy-MM-ddTHH:mm:ssZ}&toUtc={month.AddMonths(1):yyyy-MM-ddTHH:mm:ssZ}";
        var budgetPath = $"/api/budgets/{month.Year}/{month.Month}/EUR";
        (await client.PutAsJsonAsync(budgetPath, new SetMonthlyBudgetRequest(100))).EnsureSuccessStatusCode();
        var beforeBudget = await client.GetStringAsync(budgetPath + "?" + range + "&utcOffsetMinutes=0");
        var beforeAnalytics = await client.GetStringAsync("/api/analytics/monthly?" + range);
        var beforeHistory = await client.GetStringAsync("/api/transactions/recent?limit=5");
        var key = Guid.NewGuid();
        (await Post(client, account.Id, 150, key)).EnsureSuccessStatusCode();
        (await Post(client, account.Id, 150, key)).EnsureSuccessStatusCode();
        Assert.Equal(beforeBudget, await client.GetStringAsync(budgetPath + "?" + range + "&utcOffsetMinutes=0"));
        Assert.Equal(beforeAnalytics, await client.GetStringAsync("/api/analytics/monthly?" + range));
        Assert.Equal(beforeHistory, await client.GetStringAsync("/api/transactions/recent?limit=5"));
        Assert.Single(factory.Transactions.Transactions);
    }

    private static async Task<AccountResponse> NewAccount(HttpClient client, decimal initial)
    {
        var response = await client.PostAsJsonAsync("/api/accounts", new CreateAccountRequest("Checking", "BankAccount", "EUR",
            new OpeningBalanceRequest(initial, DateTimeOffset.UtcNow.AddDays(-1))));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountResponse>())!;
    }
    private static async Task<HttpClient> SignIn(LifeOSApiFactory factory, string subject)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/dev/sign-in", new DevSignInRequest(subject, null, null));
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }
    private static async Task<HttpResponseMessage> Post(HttpClient client, Guid accountId, decimal observed, Guid key, string? note = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/accounts/{accountId}/reconciliations")
        { Content = JsonContent.Create(new ReconcileAccountRequest(observed, note)) };
        request.Headers.Add("Idempotency-Key", key.ToString());
        return await client.SendAsync(request);
    }
}
