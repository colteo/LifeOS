using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Finance.Accounts;
using LifeOS.Contracts.Finance.Categories;
using LifeOS.Contracts.Finance.PlannedExpenses;
using LifeOS.App.Services.Finance;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LifeOS.IntegrationTests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public class PlannedExpenseHttpTests(PostgreSqlFixture fixture)
{
    private const string Query = "/api/planned-expenses?from=2026-01-01&to=2026-12-31&utcOffsetMinutes=0";
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HTTP_Auth_CRUD_Isolation_Validation_Concurrency_AndSafeDeletion()
    {
        await using var scope = fixture.CreateScope();
        var connection = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.GetConnectionString()!;
        await using var factory = new PlannedApiFactory(connection, Now);
        var anon = factory.CreateClient(); var a = await SignIn(factory); var b = await SignIn(factory);
        var account = await Create<AccountResponse>(a, "/api/accounts", new CreateAccountRequest("Cash", "Cash", "EUR"));
        var category = await Create<CategoryResponse>(a, "/api/categories", new CreateCategoryRequest("Fees", "Expense", null));
        var income = await Create<CategoryResponse>(a, "/api/categories", new CreateCategoryRequest("Income", "Income", null));
        var input = new SavePlannedExpenseRequest(" Visa ", account.Id, category.Id, 20, new(2026, 10, 10), " note ");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(Query)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/planned-expenses", input)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.PostAsJsonAsync("/api/planned-expenses", input)).StatusCode);
        foreach (var invalid in new[] { input with { Name = " " }, input with { ExpectedAmount = 0 }, input with { ExpectedAmount = 1.00001m }, input with { CategoryId = income.Id }, input with { AccountId = Guid.NewGuid() } })
            Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync("/api/planned-expenses", invalid)).StatusCode);
        var saved = await Create<PlannedExpenseSavedResponse>(a, "/api/planned-expenses", input);
        var path = $"/api/planned-expenses/{saved.Id}";
        var get = path + "?utcOffsetMinutes=0";
        var item = (await a.GetFromJsonAsync<PlannedExpenseResponse>(get))!;
        Assert.Equal("Visa", item.Name); Assert.Equal("note", item.Note); Assert.Equal("Due", item.Status);
        Assert.Single((await a.GetFromJsonAsync<List<PlannedExpenseResponse>>(Query))!);
        Assert.Empty((await b.GetFromJsonAsync<List<PlannedExpenseResponse>>(Query))!);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.GetAsync(Query.Replace("to=2026-12-31", "to=2040-12-31"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.GetAsync(Query.Replace("utcOffsetMinutes=0", "utcOffsetMinutes=841"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.PutAsJsonAsync(path, input with { Name = "Edited" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync(get)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PutAsJsonAsync(path, input)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(get)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsJsonAsync(path, input)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.DeleteAsync(path)).StatusCode);
        var actual = new ConfirmPlannedExpenseRequest(25, "actual", Now.AddMonths(1));
        foreach (var action in new[] { "confirm", "cancel", "restore" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync(path + $"/{action}?utcOffsetMinutes=0", actual)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsJsonAsync(path + $"/{action}?utcOffsetMinutes=0", actual)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Conflict, (await a.DeleteAsync($"/api/accounts/{account.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.DeleteAsync($"/api/categories/{category.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync(path + "/confirm?utcOffsetMinutes=0", actual with { Amount = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsync(path + "/cancel?utcOffsetMinutes=0", null)).StatusCode);
        Assert.Equal("Cancelled", (await a.GetFromJsonAsync<PlannedExpenseResponse>(get))!.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await a.PutAsJsonAsync(path, input)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsync(path + "/restore?utcOffsetMinutes=0", null)).StatusCode);
        var confirmations = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => a.PostAsJsonAsync(path + "/confirm?utcOffsetMinutes=0", actual)));
        var ids = new List<Guid?>();
        foreach (var r in confirmations) { r.EnsureSuccessStatusCode(); ids.Add((await r.Content.ReadFromJsonAsync<PlannedExpenseActionResponse>())!.TransactionId); }
        Assert.Single(ids.Distinct()); Assert.NotNull(ids[0]);
        Assert.Equal("Confirmed", (await a.GetFromJsonAsync<PlannedExpenseResponse>(get))!.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await a.PutAsJsonAsync(path, input)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsync(path + "/restore?utcOffsetMinutes=0", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"/api/transactions/{ids[0]}")).StatusCode);
        Assert.Equal("Due", (await a.GetFromJsonAsync<PlannedExpenseResponse>(get))!.Status);
        var again = await a.PostAsJsonAsync(path + "/confirm?utcOffsetMinutes=0", actual);
        var id = (await again.Content.ReadFromJsonAsync<PlannedExpenseActionResponse>())!.TransactionId;
        Assert.Equal(HttpStatusCode.OK, (await a.DeleteAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync($"/api/transactions/{id}")).StatusCode);
    }

    [Fact]
    public async Task RealAppPath_MonthNavigation_ReviewRetry_FutureCancelRestore_AndActualHistory()
    {
        await using var scope = fixture.CreateScope();
        var connection = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.GetConnectionString()!;
        await using var factory = new PlannedApiFactory(connection, Now); var client = await SignIn(factory);
        var api = new PlannedExpensesApiClient(client); var recurring = new RecurringApiClient(client); var transactions = new TransactionsApiClient(client);
        var account = await Create<AccountResponse>(client, "/api/accounts", new CreateAccountRequest("Cash", "Cash", "EUR"));
        var category = await Create<CategoryResponse>(client, "/api/categories", new CreateCategoryRequest("Fees", "Expense", null));
        var september = new DateTime(2026, 9, 1); var october = september.AddMonths(1);
        var saved = await api.SaveAsync(null, new("Visa", account.Id, category.Id, 20, new(2026, 9, 30), "planned")); Assert.True(saved.IsSuccess);
        var future = await api.SaveAsync(null, new("Insurance", account.Id, category.Id, 100, new(2026, 10, 31), null)); Assert.True(future.IsSuccess);
        var due = Assert.Single(PlannedExpensePlanning.ForMonth((await api.QueryAsync(september, september)).Value!, 2026, 9));
        var projected = Assert.Single(PlannedExpensePlanning.ForMonth((await api.QueryAsync(october, october)).Value!, 2026, 10));
        Assert.Equal("Due", due.Status); Assert.Equal("Projected", projected.Status);
        Assert.False(new PlannedExpenseFlow(projected).CanConfirm);
        Assert.False((await api.ActAsync(projected, "confirm", new(100, null, Now))).IsSuccess);
        var flow = new PlannedExpenseFlow(due); flow.Review(); flow.Amount = "25"; flow.Note = "actual";
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");
        Assert.True(flow.TryConfirmation(zone, out var request)); Assert.Equal(10, request!.OccurredAtUtc.Hour);
        Assert.True(await flow.ConfirmAsync(api, zone)); Assert.False(flow.Busy);
        Assert.Empty(PlannedExpensePlanning.ForMonth((await api.QueryAsync(september, september)).Value!, 2026, 9));
        var history = await TransactionMonthLoader.LoadAsync(september, zone, transactions, recurring);
        var tx = Assert.Single(history.History.Value!); Assert.Equal(25, tx.Amount); Assert.Equal("actual", tx.Note);
        Assert.Equal(tx.Id, (await api.ActAsync(due, "confirm", request)).Value!.TransactionId);
        Assert.True(await new PlannedExpenseFlow(projected).CancelExpenseAsync(api));
        Assert.Empty(PlannedExpensePlanning.ForMonth((await api.QueryAsync(october, october)).Value!, 2026, 10));
        var cancelled = Assert.Single((await api.QueryAsync(october, october)).Value!); Assert.Equal("Cancelled", cancelled.Status);
        Assert.True((await api.ActAsync(cancelled, "restore")).IsSuccess);
        Assert.Single(PlannedExpensePlanning.ForMonth((await api.QueryAsync(october, october)).Value!, 2026, 10));
        Assert.Single((await TransactionMonthLoader.LoadAsync(september, zone, transactions, recurring)).History.Value!);
        Assert.Empty((await TransactionMonthLoader.LoadAsync(october, zone, transactions, recurring)).History.Value!);
    }
    [Fact]
    public async Task TransactionsTabs_PlannedSeparatesRecurringFromOneOff_ActualHoldsOnlyRealTransactions()
    {
        await using var scope = fixture.CreateScope();
        var connection = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.GetConnectionString()!;
        await using var factory = new PlannedApiFactory(connection, Now); var client = await SignIn(factory);
        var api = new PlannedExpensesApiClient(client); var recurring = new RecurringApiClient(client); var transactions = new TransactionsApiClient(client);
        var account = await Create<AccountResponse>(client, "/api/accounts", new CreateAccountRequest("Cash", "Cash", "EUR"));
        var category = await Create<CategoryResponse>(client, "/api/categories", new CreateCategoryRequest("Fees", "Expense", null));
        var september = new DateTime(2026, 9, 1);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");
        Assert.True((await recurring.SaveAsync(null, new("Rent", "Expense", account.Id, category.Id, 500, 15, 2026, 9, null, 2026, 9))).IsSuccess);
        Assert.True((await api.SaveAsync(null, new("Visa", account.Id, category.Id, 20, new(2026, 9, 30), null))).IsSuccess);

        // The same calls the Transactions page makes for one selected month.
        async Task<(TransactionMonthData Data, PlannedMonthView Planned)> Load()
        {
            var data = await TransactionMonthLoader.LoadAsync(september, zone, transactions, recurring);
            var oneOff = await api.QueryAsync(september, september);
            Assert.True(data.History.IsSuccess); Assert.True(data.Planning.IsSuccess); Assert.True(oneOff.IsSuccess);
            return (data, PlannedMonthView.ForMonth(data.Planning.Value!.Occurrences, oneOff.Value!, 2026, 9));
        }

        var initial = await Load();
        Assert.Empty(initial.Data.History.Value!); // Actual tab: plans never appear as transactions.
        var rent = Assert.Single(initial.Planned.Recurring); Assert.Equal("Rent", rent.Name);
        var visa = Assert.Single(initial.Planned.OneOff); Assert.Equal("Visa", visa.Name); Assert.Equal("Due", visa.Status);

        Assert.True(await new RecurringOccurrenceFlow(rent).SkipAsync(recurring));
        var skipped = await Load();
        Assert.Empty(skipped.Planned.Recurring); Assert.Single(skipped.Planned.OneOff);
        Assert.Equal(1, skipped.Planned.DueCount); Assert.Empty(skipped.Data.History.Value!);

        var flow = new PlannedExpenseFlow(visa); flow.Review();
        Assert.True(await flow.ConfirmAsync(api, zone));
        var confirmed = await Load();
        Assert.True(confirmed.Planned.IsEmpty); Assert.Equal(0, confirmed.Planned.DueCount);
        var actual = Assert.Single(confirmed.Data.History.Value!); Assert.Equal(20, actual.Amount);
    }

    private static async Task<T> Create<T>(HttpClient client, string uri, object request)
    {
        var response = await client.PostAsJsonAsync(uri, request); response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
    private static async Task<HttpClient> SignIn(PlannedApiFactory factory)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/dev/sign-in", new DevSignInRequest("recurring-test-" + Guid.NewGuid(), null, null));
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken);
        return client;
    }
    private sealed class PlannedApiFactory(string connection, DateTimeOffset? now = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            if (now is { } instant) builder.ConfigureServices(services =>
            {
                // Freeze recurrence Today without changing JWT issuance/validation clocks.
                services.RemoveAll<LifeOS.Application.Finance.PlannedExpenses.PlannedExpenseHandler>();
                services.AddScoped(p => new LifeOS.Application.Finance.PlannedExpenses.PlannedExpenseHandler(
                    p.GetRequiredService<LifeOS.Application.Finance.PlannedExpenses.IPlannedExpenseRepository>(), new FixedTimeProvider(instant)));
            });
            builder.UseSetting("ConnectionStrings:PostgreSQL", connection);
            builder.UseSetting("Authentication:LifeOS:SigningKey", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting(LifeOS.Api.Authentication.DevelopmentSignIn.EnabledKey, "true");
            builder.UseSetting("Authentication:Google:ClientId", "test-client-id.apps.googleusercontent.com");
            builder.UseSetting("Authentication:Google:ClientSecret", "test-client-secret");
            builder.UseSetting("Authentication:Google:AllowedEmails:0", "person@example.com");
        }
    }
}
