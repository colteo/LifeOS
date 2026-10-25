using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LifeOS.Application.Assistant;
using LifeOS.Application.Finance.Assistant;
using LifeOS.Contracts.Assistant;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Finance.Accounts;
using LifeOS.Contracts.Finance.Categories;
using LifeOS.Contracts.Finance.Transactions;
using LifeOS.Contracts.Users;
using LifeOS.Infrastructure.Persistence;
using LifeOS.IntegrationTests.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// AI-007 F2 through the real API pipeline (JWT, routing, endpoints, DI) and real PostgreSQL: the operation
// contract, status codes and stable codes, user isolation, idempotent confirmation and the F1 endpoints next
// to it. Every sign-in is a new user, so assertions are scoped to the test's own data. Expiry is reached by
// moving the stored draft back in time (the host clock also signs the access tokens).
[Collection(PostgreSqlCollection.Name)]
public class AssistantOperationHttpTests(PostgreSqlFixture fixture)
{
    private const string Assistant = "/api/assistant";
    private const string Kind = AssistantOperationKindNames.FinanceTransactionCreate;

    [Fact]
    public async Task EveryOperationEndpoint_RequiresAuthentication()
    {
        await using var factory = await FactoryAsync();
        var anonymous = factory.CreateClient();
        var id = Guid.CreateVersion7();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Assistant}/conversations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"{Assistant}/conversations/{id}/operations", Create())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Assistant}/operations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PatchAsJsonAsync($"{Assistant}/operations/{id}", Patch(1))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync($"{Assistant}/operations/{id}/confirm", new AssistantOperationVersionRequest(1))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync($"{Assistant}/operations/{id}/cancel", new AssistantOperationVersionRequest(1))).StatusCode);
    }

    [Fact]
    public async Task TheOperationHandlers_ResolveFromTheHost()
    {
        await using var factory = await FactoryAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;

        Assert.NotNull(services.GetRequiredService<GetAssistantConversationHandler>());
        Assert.NotNull(services.GetRequiredService<CreateFinanceOperationHandler>());
        Assert.NotNull(services.GetRequiredService<GetFinanceOperationHandler>());
        Assert.NotNull(services.GetRequiredService<GetActiveFinanceOperationHandler>());
        Assert.NotNull(services.GetRequiredService<UpdateFinanceDraftHandler>());
        Assert.NotNull(services.GetRequiredService<ConfirmFinanceOperationHandler>());
        Assert.NotNull(services.GetRequiredService<CancelFinanceOperationHandler>());
    }

    // "Speso 5€ per colazione con Pamela", as structured input: the account is asked for, then confirmed once.
    [Fact]
    public async Task ADraft_IsCreatedCompletedAndConfirmed_IntoExactlyOneFinanceTransaction()
    {
        await using var factory = await FactoryAsync();
        var (client, finance) = await SignInWithFinanceAsync(factory);
        var conversation = await CreateConversationAsync(client);

        var detail = await client.GetFromJsonAsync<AssistantConversationDetailResponse>($"{Assistant}/conversations/{conversation}");
        Assert.Equal((conversation, (AssistantOperationResponse?)null), (detail!.Conversation.Id, detail.ActiveOperation));

        var created = await client.PostAsJsonAsync($"{Assistant}/conversations/{conversation}/operations",
            Create(type: "Expense", amount: 5m, categoryId: finance.Expense, note: "colazione con Pamela"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draft = (await created.Content.ReadFromJsonAsync<AssistantOperationResponse>())!;
        Assert.Equal($"{Assistant}/operations/{draft.Id}", created.Headers.Location!.ToString());
        Assert.Equal((AssistantOperationStatuses.Collecting, 1, false), (draft.Status, draft.DraftVersion, draft.CanConfirm));
        Assert.Equal(["accountId"], draft.MissingFields);
        Assert.Equal(("Ristoranti", "User", (string?)null), (draft.Draft.Category!.Name, draft.Draft.Category.Source, draft.Draft.Currency));
        Assert.Equal(("Today", "CurrentTime", "Europe/Rome"),
            (draft.Draft.Occurrence!.DateSource, draft.Draft.Occurrence.TimeSource, draft.Draft.Occurrence.TimeZoneId));
        Assert.Equal(draft.Id, (await client.GetFromJsonAsync<AssistantConversationDetailResponse>($"{Assistant}/conversations/{conversation}"))!.ActiveOperation!.Id);

        var completed = await client.PatchAsJsonAsync($"{Assistant}/operations/{draft.Id}", Patch(1, accountId: finance.Account));
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var ready = (await completed.Content.ReadFromJsonAsync<AssistantOperationResponse>())!;
        Assert.Equal((AssistantOperationStatuses.AwaitingConfirmation, 2, true, "EUR"), (ready.Status, ready.DraftVersion, ready.CanConfirm, ready.Draft.Currency));
        Assert.Equal(("Revolut", "EUR"), (ready.Draft.Account!.Name, ready.Draft.Account.Currency));

        Assert.Empty((await client.GetFromJsonAsync<List<TransactionResponse>>("/api/transactions/recent"))!);

        var confirmed = await client.PostAsJsonAsync($"{Assistant}/operations/{draft.Id}/confirm", new AssistantOperationVersionRequest(2));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var confirmation = (await confirmed.Content.ReadFromJsonAsync<AssistantOperationConfirmationResponse>())!;
        Assert.Equal((false, AssistantOperationStatuses.Executed, (Guid?)confirmation.TransactionId),
            (confirmation.Replayed, confirmation.Operation.Status, confirmation.Operation.TransactionId));

        // The transaction is an ordinary Finance transaction of the user.
        var transaction = (await client.GetFromJsonAsync<TransactionResponse>($"/api/transactions/{confirmation.TransactionId}"))!;
        Assert.Equal(("Expense", 5m, "EUR", (Guid?)finance.Account, (Guid?)finance.Expense, "colazione con Pamela"),
            (transaction.Type, transaction.Amount, transaction.Currency, transaction.AccountId, transaction.CategoryId, transaction.Note));

        // A retried confirmation (e.g. after a timeout) returns the same transaction.
        var replay = await client.PostAsJsonAsync($"{Assistant}/operations/{draft.Id}/confirm", new AssistantOperationVersionRequest(2));
        var replayed = (await replay.Content.ReadFromJsonAsync<AssistantOperationConfirmationResponse>())!;
        Assert.Equal((HttpStatusCode.OK, true, confirmation.TransactionId), (replay.StatusCode, replayed.Replayed, replayed.TransactionId));
        Assert.Single((await client.GetFromJsonAsync<List<TransactionResponse>>("/api/transactions/recent"))!);

        var executed = (await client.GetFromJsonAsync<AssistantOperationResponse>($"{Assistant}/operations/{draft.Id}"))!;
        Assert.Equal((AssistantOperationStatuses.Executed, (Guid?)confirmation.TransactionId), (executed.Status, executed.TransactionId));
        Assert.Null((await client.GetFromJsonAsync<AssistantConversationDetailResponse>($"{Assistant}/conversations/{conversation}"))!.ActiveOperation);

        var edit = await AssertProblemAsync(await client.PatchAsJsonAsync($"{Assistant}/operations/{draft.Id}", Patch(2, amount: 6m)),
            HttpStatusCode.Conflict, AssistantErrorCodes.OperationExecuted);
        Assert.Equal(confirmation.TransactionId, edit.GetProperty("transactionId").GetGuid());
        await AssertProblemAsync(await client.PostAsJsonAsync($"{Assistant}/operations/{draft.Id}/cancel", new AssistantOperationVersionRequest(2)),
            HttpStatusCode.Conflict, AssistantErrorCodes.OperationExecuted);
    }

    [Fact]
    public async Task VersionsAndCompleteness_AreEnforcedByTheServer()
    {
        await using var factory = await FactoryAsync();
        var (client, finance) = await SignInWithFinanceAsync(factory);
        var conversation = await CreateConversationAsync(client);
        var draft = await CreateOperationAsync(client, conversation, Create(type: "Expense", amount: 5m, categoryId: finance.Expense));

        var incomplete = await AssertProblemAsync(
            await client.PostAsJsonAsync($"{Assistant}/operations/{draft.Id}/confirm", new AssistantOperationVersionRequest(1)),
            HttpStatusCode.UnprocessableEntity, AssistantErrorCodes.DraftIncomplete);
        Assert.Equal(["accountId"], incomplete.GetProperty("missingFields").EnumerateArray().Select(field => field.GetString()));

        await client.PatchAsJsonAsync($"{Assistant}/operations/{draft.Id}", Patch(1, accountId: finance.Account));

        var stale = await AssertProblemAsync(await client.PatchAsJsonAsync($"{Assistant}/operations/{draft.Id}", Patch(1, amount: 9m)),
            HttpStatusCode.Conflict, AssistantErrorCodes.VersionConflict);
        Assert.Equal(2, stale.GetProperty("currentVersion").GetInt32());
        await AssertProblemAsync(await client.PostAsJsonAsync($"{Assistant}/operations/{draft.Id}/confirm", new AssistantOperationVersionRequest(1)),
            HttpStatusCode.Conflict, AssistantErrorCodes.VersionConflict);

        // A patch that changes nothing keeps the version.
        var same = await client.PatchAsJsonAsync($"{Assistant}/operations/{draft.Id}", Patch(2, amount: 5m));
        Assert.Equal(2, (await same.Content.ReadFromJsonAsync<AssistantOperationResponse>())!.DraftVersion);

        // A second operation in the same conversation waits for this one.
        var open = await AssertProblemAsync(await client.PostAsJsonAsync($"{Assistant}/conversations/{conversation}/operations", Create(amount: 1m)),
            HttpStatusCode.Conflict, AssistantErrorCodes.OperationAlreadyOpen);
        Assert.Equal(draft.Id, open.GetProperty("operationId").GetGuid());

        var cancelled = await client.PostAsJsonAsync($"{Assistant}/operations/{draft.Id}/cancel", new AssistantOperationVersionRequest(2));
        Assert.Equal(AssistantOperationStatuses.Cancelled, (await cancelled.Content.ReadFromJsonAsync<AssistantOperationResponse>())!.Status);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"{Assistant}/operations/{draft.Id}/cancel", new AssistantOperationVersionRequest(2))).StatusCode);
        await AssertProblemAsync(await client.PostAsJsonAsync($"{Assistant}/operations/{draft.Id}/confirm", new AssistantOperationVersionRequest(2)),
            HttpStatusCode.Conflict, AssistantErrorCodes.OperationCancelled);

        Assert.Empty((await client.GetFromJsonAsync<List<TransactionResponse>>("/api/transactions/recent"))!);
    }

    [Fact]
    public async Task InvalidInput_IsAFieldKeyed400()
    {
        await using var factory = await FactoryAsync();
        var (client, _) = await SignInWithFinanceAsync(factory);
        var conversation = await CreateConversationAsync(client);
        var operations = $"{Assistant}/conversations/{conversation}/operations";

        await AssertInvalidAsync(client.PostAsJsonAsync(operations, Create() with { Kind = null }), "kind");
        await AssertInvalidAsync(client.PostAsJsonAsync(operations, Create(type: "Transfer")), "type");
        await AssertInvalidAsync(client.PostAsJsonAsync(operations, Create(amount: 0m)), "amount");
        await AssertInvalidAsync(client.PostAsJsonAsync(operations, Create(amount: 1.23456m)), "amount");
        await AssertInvalidAsync(client.PostAsJsonAsync(operations, Create() with { Currency = "EURO" }), "currency");
        await AssertInvalidAsync(client.PostAsJsonAsync(operations, Create(note: new string('n', 1001))), "note");
        await AssertInvalidAsync(client.PostAsJsonAsync(operations, Create() with { OccurredOn = "23/10/2026" }), "occurredOn");
        await AssertInvalidAsync(client.PostAsJsonAsync(operations, Create() with { OccurredOn = "1899-12-31" }), "occurredOn");
        await AssertInvalidAsync(client.PostAsJsonAsync(operations, Create() with { OccurredTime = "25:00" }), "occurredTime");

        var draft = await CreateOperationAsync(client, conversation, Create(amount: 1m));
        var operation = $"{Assistant}/operations/{draft.Id}";
        await AssertInvalidAsync(client.PatchAsJsonAsync(operation, Patch(1) with { ExpectedVersion = null }), "expectedVersion");
        await AssertInvalidAsync(client.PatchAsJsonAsync(operation, Patch(1) with { Clear = ["userId"] }), "clear");
        await AssertInvalidAsync(client.PatchAsJsonAsync(operation, Patch(1, amount: 2m) with { Clear = ["amount"] }), "clear");
        await AssertInvalidAsync(client.PostAsJsonAsync($"{operation}/confirm", new AssistantOperationVersionRequest(null)), "expectedVersion");
        await AssertInvalidAsync(client.PostAsJsonAsync($"{operation}/cancel", new AssistantOperationVersionRequest(0)), "expectedVersion");

        Assert.Equal(1, (await client.GetFromJsonAsync<AssistantOperationResponse>(operation))!.DraftVersion);
    }

    [Fact]
    public async Task AnotherUsersResources_AreIndistinguishableFromMissingOnes()
    {
        await using var factory = await FactoryAsync();
        var (owner, finance) = await SignInWithFinanceAsync(factory);
        var (intruder, foreign) = await SignInWithFinanceAsync(factory);
        var conversation = await CreateConversationAsync(owner);
        var draft = await CreateOperationAsync(owner, conversation, Create(type: "Expense", amount: 5m, accountId: finance.Account, categoryId: finance.Expense));

        foreach (var id in new[] { conversation, Guid.CreateVersion7() })
        {
            await AssertProblemAsync(await intruder.GetAsync($"{Assistant}/conversations/{id}"), HttpStatusCode.NotFound, AssistantErrorCodes.ConversationNotFound);
            await AssertProblemAsync(await intruder.PostAsJsonAsync($"{Assistant}/conversations/{id}/operations", Create()), HttpStatusCode.NotFound,
                AssistantErrorCodes.ConversationNotFound);
        }

        foreach (var id in new[] { draft.Id, Guid.CreateVersion7() })
        {
            var operation = $"{Assistant}/operations/{id}";
            var responses = new[]
            {
                await intruder.GetAsync(operation),
                await intruder.PatchAsJsonAsync(operation, Patch(1, amount: 1m)),
                await intruder.PostAsJsonAsync($"{operation}/confirm", new AssistantOperationVersionRequest(1)),
                await intruder.PostAsJsonAsync($"{operation}/cancel", new AssistantOperationVersionRequest(1))
            };

            foreach (var response in responses)
            {
                await AssertProblemAsync(response, HttpStatusCode.NotFound, AssistantErrorCodes.OperationNotFound);
            }
        }

        // The owner cannot use the intruder's account or category: the same refusal as for a random id.
        var foreignAccount = await AssertProblemAsync(await owner.PatchAsJsonAsync($"{Assistant}/operations/{draft.Id}", Patch(1, accountId: foreign.Account)),
            HttpStatusCode.UnprocessableEntity, AssistantErrorCodes.AccountNotFound);
        var randomAccount = await AssertProblemAsync(
            await owner.PatchAsJsonAsync($"{Assistant}/operations/{draft.Id}", Patch(1, accountId: Guid.CreateVersion7())),
            HttpStatusCode.UnprocessableEntity, AssistantErrorCodes.AccountNotFound);
        Assert.Equal(foreignAccount.GetProperty("detail").GetString(), randomAccount.GetProperty("detail").GetString());
        await AssertProblemAsync(await owner.PatchAsJsonAsync($"{Assistant}/operations/{draft.Id}", Patch(1, categoryId: foreign.Expense)),
            HttpStatusCode.UnprocessableEntity, AssistantErrorCodes.CategoryNotFound);
        await AssertProblemAsync(await owner.PatchAsJsonAsync($"{Assistant}/operations/{draft.Id}", Patch(1, categoryId: finance.Income)),
            HttpStatusCode.UnprocessableEntity, AssistantErrorCodes.CategoryIncompatible);
        await AssertProblemAsync(await owner.PatchAsJsonAsync($"{Assistant}/operations/{draft.Id}", Patch(1) with { Currency = "USD" }),
            HttpStatusCode.UnprocessableEntity, AssistantErrorCodes.CurrencyMismatch);

        var stored = (await owner.GetFromJsonAsync<AssistantOperationResponse>($"{Assistant}/operations/{draft.Id}"))!;
        Assert.Equal((1, (Guid?)finance.Account), (stored.DraftVersion, stored.Draft.Account?.Id));
    }

    [Fact]
    public async Task AnExpiredDraft_Is409_AndStaysReadable()
    {
        await using var factory = await FactoryAsync();
        var (client, finance) = await SignInWithFinanceAsync(factory);
        var conversation = await CreateConversationAsync(client);
        var draft = await CreateOperationAsync(client, conversation, Create(type: "Expense", amount: 5m, accountId: finance.Account, categoryId: finance.Expense));

        // The last change was 25 hours ago.
        await Execute($"""
            UPDATE assistant_operations SET created_at_utc = created_at_utc - interval '25 hours',
                updated_at_utc = updated_at_utc - interval '25 hours', expires_at_utc = expires_at_utc - interval '25 hours'
            WHERE id = '{draft.Id}'
            """);

        await AssertProblemAsync(await client.PostAsJsonAsync($"{Assistant}/operations/{draft.Id}/confirm", new AssistantOperationVersionRequest(1)),
            HttpStatusCode.Conflict, AssistantErrorCodes.OperationExpired);
        await AssertProblemAsync(await client.PatchAsJsonAsync($"{Assistant}/operations/{draft.Id}", Patch(1, amount: 6m)),
            HttpStatusCode.Conflict, AssistantErrorCodes.OperationExpired);

        var expired = (await client.GetFromJsonAsync<AssistantOperationResponse>($"{Assistant}/operations/{draft.Id}"))!;
        Assert.Equal((AssistantOperationStatuses.Expired, false), (expired.Status, expired.CanConfirm));
        Assert.Empty((await client.GetFromJsonAsync<List<TransactionResponse>>("/api/transactions/recent"))!);

        // The conversation is free for a new operation.
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"{Assistant}/conversations/{conversation}/operations", Create())).StatusCode);
    }

    [Fact]
    public async Task ConcurrentConfirmations_OverHttp_RecordOneTransaction()
    {
        await using var factory = await FactoryAsync();
        var (client, finance) = await SignInWithFinanceAsync(factory);
        var conversation = await CreateConversationAsync(client);
        var draft = await CreateOperationAsync(client, conversation, Create(type: "Income", amount: 1200m, accountId: finance.Account, categoryId: finance.Income));

        var responses = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => client.PostAsJsonAsync($"{Assistant}/operations/{draft.Id}/confirm", new AssistantOperationVersionRequest(1))));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadFromJsonAsync<AssistantOperationConfirmationResponse>()));
        Assert.Single(bodies, body => !body!.Replayed);
        Assert.Single(bodies.Select(body => body!.TransactionId).Distinct());
        Assert.Equal(bodies[0]!.TransactionId, Assert.Single((await client.GetFromJsonAsync<List<TransactionResponse>>("/api/transactions/recent"))!).Id);
    }

    // Conversations stay until the user deletes them; deleting one deletes its operations, never transactions.
    [Fact]
    public async Task DeletingTheConversation_KeepsTheConfirmedTransaction()
    {
        await using var factory = await FactoryAsync();
        var (client, finance) = await SignInWithFinanceAsync(factory);
        var conversation = await CreateConversationAsync(client);
        var draft = await CreateOperationAsync(client, conversation, Create(type: "Expense", amount: 5m, accountId: finance.Account, categoryId: finance.Expense));
        var confirmation = (await (await client.PostAsJsonAsync($"{Assistant}/operations/{draft.Id}/confirm", new AssistantOperationVersionRequest(1)))
            .Content.ReadFromJsonAsync<AssistantOperationConfirmationResponse>())!;

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Assistant}/conversations/{conversation}")).StatusCode);

        await AssertProblemAsync(await client.GetAsync($"{Assistant}/operations/{draft.Id}"), HttpStatusCode.NotFound, AssistantErrorCodes.OperationNotFound);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/transactions/{confirmation.TransactionId}")).StatusCode);
    }

    // ---- Helpers ----

    private sealed record Finance(Guid Account, Guid Expense, Guid Income);

    private static CreateAssistantOperationRequest Create(string? type = null, decimal? amount = null, Guid? accountId = null, Guid? categoryId = null,
        string? note = null) =>
        new(Kind, type, amount, null, accountId, categoryId, note, null, null);

    private static UpdateAssistantOperationRequest Patch(int version, decimal? amount = null, Guid? accountId = null, Guid? categoryId = null) =>
        new(version, null, amount, null, accountId, categoryId, null, null, null, null);

    private static async Task<Guid> CreateConversationAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync($"{Assistant}/conversations", new CreateAssistantConversationRequest(Guid.CreateVersion7()));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<AssistantConversationResponse>())!.Id;
    }

    private static async Task<AssistantOperationResponse> CreateOperationAsync(HttpClient client, Guid conversation, CreateAssistantOperationRequest request)
    {
        var response = await client.PostAsJsonAsync($"{Assistant}/conversations/{conversation}/operations", request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<AssistantOperationResponse>())!;
    }

    // A new user in Europe/Rome with one EUR account and an expense and an income category.
    private static async Task<(HttpClient, Finance)> SignInWithFinanceAsync(OperationApiFactory factory)
    {
        var client = factory.CreateClient();
        var signIn = await client.PostAsJsonAsync("/api/auth/dev/sign-in", new DevSignInRequest("assistant-op-test-" + Guid.NewGuid(), null, null));
        signIn.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await signIn.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken);

        (await client.PutAsJsonAsync("/api/me/time-zone", new SetTimeZoneRequest("Europe/Rome"))).EnsureSuccessStatusCode();
        var account = await PostAsync<AccountResponse>(client, "/api/accounts", new CreateAccountRequest("Revolut", "BankAccount", "EUR"));
        var expense = await PostAsync<CategoryResponse>(client, "/api/categories", new CreateCategoryRequest("Ristoranti", "Expense", null));
        var income = await PostAsync<CategoryResponse>(client, "/api/categories", new CreateCategoryRequest("Stipendio", "Income", null));

        return (client, new Finance(account.Id, expense.Id, income.Id));
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string path, object body)
    {
        var response = await client.PostAsJsonAsync(path, body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    // Status, stable code, and no internal detail; returns the problem for further checks.
    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"{response.StatusCode}: {body}");
        AssertNoInternals(body);
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));

        return problem;
    }

    private static async Task AssertInvalidAsync(Task<HttpResponseMessage> request, string field)
    {
        using var response = await request;
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{response.StatusCode}: {body}");
        AssertNoInternals(body);

        using var problem = JsonDocument.Parse(body);
        Assert.Equal(AssistantErrorCodes.InvalidRequest, problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), body);
    }

    private static void AssertNoInternals(string body)
    {
        Assert.DoesNotContain("Exception", body);
        Assert.DoesNotContain("Npgsql", body);
        Assert.DoesNotContain("assistant_operations", body);
        Assert.DoesNotContain("   at ", body);
    }

    private async Task Execute(string sql)
    {
        await using var scope = fixture.CreateScope();
        await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<OperationApiFactory> FactoryAsync()
    {
        await using var scope = fixture.CreateScope();

        return new OperationApiFactory(scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.GetConnectionString()!);
    }

    private sealed class OperationApiFactory(string connection) : WebApplicationFactory<Program>
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
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(new AdjustableTimeProvider()));
        }
    }
}
