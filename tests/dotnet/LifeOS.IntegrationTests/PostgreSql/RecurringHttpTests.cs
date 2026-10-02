using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Finance.Accounts;
using LifeOS.Contracts.Finance.Categories;
using LifeOS.Contracts.Finance.Recurring;
using LifeOS.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public class RecurringHttpTests(PostgreSqlFixture fixture)
{
    private const string Query = "/api/recurring?fromYear=2026&fromMonth=1&toYear=2026&toMonth=12&utcOffsetMinutes=0";
    [Fact]
    public async Task OptionalEnd_RoundTrips_StopsProjection_AndShorteningPreservesActualHistory()
    {
        await using var scope = fixture.CreateScope();
        var connection = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.GetConnectionString()!;
        await using var factory = new RecurringApiFactory(connection); var client = await SignIn(factory);
        var account = await Create<AccountResponse>(client, "/api/accounts", new CreateAccountRequest("Cash", "Cash", "EUR"));
        var category = await Create<CategoryResponse>(client, "/api/categories", new CreateCategoryRequest("Car", "Expense", null));
        var input = new SaveRecurringRuleRequest("Installment", "Expense", account.Id, category.Id, 20, 31, 2026, 2, null, 2026, 5);
        foreach (var invalid in new[] { input with { EndYear = null }, input with { EndMonth = null }, input with { EndMonth = 1 }, input with { EndMonth = 13 } })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/recurring", invalid)).StatusCode);
        var rule = await Create<RecurringRuleResponse>(client, "/api/recurring", input);
        Assert.Equal((2026, 5), (rule.EndYear, rule.EndMonth));
        var data = (await client.GetFromJsonAsync<RecurringResponse>(Query))!;
        Assert.Equal(new[] { 2, 3, 4, 5 }, data.Occurrences.Select(o => o.Month));
        Assert.Equal(28, data.Occurrences.First().ScheduledDate.Day);
        var path = $"/api/recurring/{rule.Id}/2026/3/confirm?utcOffsetMinutes=0";
        var payload = new ConfirmRecurringRequest(25, "actual", new(2026, 3, 31, 12, 0, 0, TimeSpan.Zero));
        var response = await client.PostAsJsonAsync(path, payload); response.EnsureSuccessStatusCode();
        var actual = (await response.Content.ReadFromJsonAsync<RecurringActionResponse>())!.TransactionId;
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/recurring/{rule.Id}", input with { EndMonth = 2 })).StatusCode);
        Assert.Single((await client.GetFromJsonAsync<RecurringResponse>(Query))!.Occurrences);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/transactions/{actual}")).StatusCode);
        var retry = await client.PostAsJsonAsync(path, payload); retry.EnsureSuccessStatusCode();
        Assert.Equal(actual, (await retry.Content.ReadFromJsonAsync<RecurringActionResponse>())!.TransactionId);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/recurring/{rule.Id}/2026/6/skip?utcOffsetMinutes=0", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/recurring/{rule.Id}", input with { EndYear = null, EndMonth = null })).StatusCode);
        Assert.Equal("Confirmed", (await client.GetFromJsonAsync<RecurringResponse>(Query))!.Occurrences.Single(o => o.Month == 3).Status);
        Assert.Contains((await client.GetFromJsonAsync<RecurringResponse>(Query))!.Occurrences, o => o.Month == 6);
    }
    [Fact]
    public async Task RealHttpAndPostgres_Auth_Ownership_CRUD_ConcurrentConfirm_SkipRestore_Delete()
    {
        await using var scope = fixture.CreateScope();
        var connection = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.GetConnectionString()!;
        await using var factory = new RecurringApiFactory(connection);
        var anon = factory.CreateClient();
        var a = await SignIn(factory); var b = await SignIn(factory);
        var account = await Create<AccountResponse>(a, "/api/accounts", new CreateAccountRequest("Cash", "Cash", "EUR"));
        var category = await Create<CategoryResponse>(a, "/api/categories", new CreateCategoryRequest("Fees", "Expense", null));
        var income = await Create<CategoryResponse>(a, "/api/categories", new CreateCategoryRequest("Salary", "Income", null));
        var input = new SaveRecurringRuleRequest(" Fee ", "Expense", account.Id, category.Id, 20, 1, 2026, 2, " note ");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(Query)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/recurring", input)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.PostAsJsonAsync("/api/recurring", input)).StatusCode);
        foreach (var invalid in new[] { input with { DayOfMonth = 0 }, input with { DayOfMonth = 32 }, input with { Amount = 0 },
            input with { Amount = 1.00001m }, input with { Type = "Transfer" }, input with { CategoryId = income.Id }, input with { AccountId = Guid.NewGuid() } })
            Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync("/api/recurring", invalid)).StatusCode);
        var rule = await Create<RecurringRuleResponse>(a, "/api/recurring", input);
        Assert.Equal("Fee", rule.Name);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsJsonAsync($"/api/recurring/{rule.Id}", input)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.DeleteAsync($"/api/recurring/{rule.Id}")).StatusCode);
        Assert.Empty((await b.GetFromJsonAsync<RecurringResponse>(Query))!.Rules);
        Assert.Equal(11, (await a.GetFromJsonAsync<RecurringResponse>(Query))!.Occurrences.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.GetAsync(Query.Replace("toYear=2026", "toYear=2040"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.GetAsync(Query.Replace("fromMonth=1", "fromMonth=13"))).StatusCode);
        var path = $"/api/recurring/{rule.Id}/2026/2";
        var confirm = new ConfirmRecurringRequest(25, "actual", new(2026, 2, 1, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync(path + "/confirm?utcOffsetMinutes=0", confirm with { Amount = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync(path + "/confirm?utcOffsetMinutes=841", confirm)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync(path.Replace("/2026/2", "/2026/13") + "/confirm?utcOffsetMinutes=0", confirm)).StatusCode);
        foreach (var action in new[] { "skip", "restore", "confirm" })
        {
            var uri = path + $"/{action}?utcOffsetMinutes=0";
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync(uri, confirm)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsJsonAsync(uri, confirm)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await b.PutAsJsonAsync($"/api/recurring/{rule.Id}", input)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/recurring/{rule.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.DeleteAsync($"/api/accounts/{account.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.DeleteAsync($"/api/categories/{category.Id}")).StatusCode);
        var future = $"/api/recurring/{rule.Id}/2099/1";
        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsJsonAsync(future + "/confirm?utcOffsetMinutes=0", confirm)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsync(future + "/skip?utcOffsetMinutes=0", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsync(future + "/restore?utcOffsetMinutes=0", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsync(path + "/skip?utcOffsetMinutes=0", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsJsonAsync(path + "/confirm?utcOffsetMinutes=0", confirm)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsync(path + "/restore?utcOffsetMinutes=0", null)).StatusCode);
        var confirmations = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => a.PostAsJsonAsync(path + "/confirm?utcOffsetMinutes=0", confirm)));
        var ids = new List<Guid?>();
        foreach (var response in confirmations)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            ids.Add((await response.Content.ReadFromJsonAsync<RecurringActionResponse>())!.TransactionId);
        }
        Assert.Single(ids.Distinct()); Assert.NotNull(ids[0]);
        var updated = input with { Name = "Updated", DayOfMonth = 15, Amount = 30 };
        Assert.Equal(HttpStatusCode.OK, (await a.PutAsJsonAsync($"/api/recurring/{rule.Id}", updated)).StatusCode);
        var occurrence = (await a.GetFromJsonAsync<RecurringResponse>(Query))!.Occurrences.Single(o => o.Month == 2);
        Assert.Equal("Confirmed", occurrence.Status); Assert.Equal(1, occurrence.ScheduledDate.Day);
        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"/api/transactions/{ids[0]}")).StatusCode);
        Assert.Equal("Due", (await a.GetFromJsonAsync<RecurringResponse>(Query))!.Occurrences.Single(o => o.Month == 2).Status);
        var again = await a.PostAsJsonAsync(path + "/confirm?utcOffsetMinutes=0", confirm);
        var actualId = (await again.Content.ReadFromJsonAsync<RecurringActionResponse>())!.TransactionId;
        Assert.Equal(HttpStatusCode.OK, (await a.DeleteAsync($"/api/recurring/{rule.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync($"/api/transactions/{actualId}")).StatusCode);
        Assert.Empty((await a.GetFromJsonAsync<RecurringResponse>(Query))!.Rules);
    }

    private static async Task<T> Create<T>(HttpClient client, string uri, object request)
    {
        var response = await client.PostAsJsonAsync(uri, request); response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
    private static async Task<HttpClient> SignIn(RecurringApiFactory factory)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/dev/sign-in", new DevSignInRequest("recurring-test-" + Guid.NewGuid(), null, null));
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken);
        return client;
    }
    private sealed class RecurringApiFactory(string connection) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:PostgreSQL", connection);
            builder.UseSetting("Authentication:LifeOS:SigningKey", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting(LifeOS.Api.Authentication.DevelopmentSignIn.EnabledKey, "true");
            builder.UseSetting("Authentication:Google:ClientId", "test-client-id.apps.googleusercontent.com");
            builder.UseSetting("Authentication:Google:ClientSecret", "test-client-secret");
            builder.UseSetting("Authentication:Google:AllowedEmails:0", "person@example.com");
        }
    }
}
