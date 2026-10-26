using System.Net.Http.Headers;
using System.Net.Http.Json;
using LifeOS.App.Services.Assistant;
using LifeOS.Application.Assistant;
using LifeOS.Contracts.Assistant;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Finance.Accounts;
using LifeOS.Contracts.Finance.Categories;
using LifeOS.Contracts.Users;
using LifeOS.Infrastructure.Persistence;
using LifeOS.IntegrationTests.Http;
using LifeOS.UnitTests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static LifeOS.UnitTests.Fakes.Interpretations;

namespace LifeOS.IntegrationTests.PostgreSql;

// AI-007 F4: the Android app's Assistant client and conversation flow against the real API pipeline (JWT,
// routing, F1/F2 endpoints) and real PostgreSQL, with a scripted interpreter in place of the AI service. It proves
// the app needs no new API: conversation, idempotent send, retry of a failed turn, draft edit with the user's
// own account and category, one explicit confirmation, and the same state after reopening.
[Collection(PostgreSqlCollection.Name)]
public class AssistantAppClientHttpTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task TheAppFlow_RecordsOneTransaction_OnlyOnConfirm_AndReopeningShowsTheSameConversation()
    {
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Expense(Fields(amount: Stated("5", "5 euro"), currency: Stated("EUR", "euro"), category: Inferred("Ristoranti"),
                note: Stated("colazione con Pamela"))))
            .Then(AssistantInterpretationFailure.Unavailable)
            .Then(Update(Fields(account: Stated("Revolut"))));
        await using var factory = await FactoryAsync(interpreter);
        var (http, account, bar) = await SignInWithFinanceAsync(factory);
        var api = new AssistantApiClient(http, http);

        // New conversation: the same client key twice is one conversation.
        var key = Guid.CreateVersion7();
        var conversation = (await api.CreateConversationAsync(key)).Value!;
        Assert.Equal(conversation.Id, (await api.CreateConversationAsync(key)).Value!.Id);

        var flow = new AssistantChatFlow(api, conversation.Id);
        await flow.LoadAsync();
        flow.Text = "Ho speso 5 euro per colazione con Pamela";
        await flow.SendAsync();

        Assert.Equal(["Ho speso 5 euro per colazione con Pamela", "Da quale conto?"], flow.Messages.Select(message => message.Content));
        Assert.Equal((AssistantOperationStatuses.Collecting, "Suggested", "colazione con Pamela"),
            (flow.Operation!.Status, flow.Operation.Draft.Category!.Source, flow.Operation.Draft.Note));
        Assert.False(await flow.ConfirmAsync());

        // A failed turn: Retry is a new attempt of the same message.
        flow.Text = "dal conto Revolut";
        await flow.SendAsync();
        var failed = flow.RetryableMessage!;
        Assert.Equal(AssistantFailureCodes.AssistantUnavailable, failed.Turn!.FailureCode);

        await flow.RetryAsync();

        Assert.Null(flow.RetryableMessage);
        Assert.Equal((AssistantTurnStatuses.Completed, 2), (flow.Messages.Single(message => message.Id == failed.Id).Turn!.Status,
            flow.Messages.Single(message => message.Id == failed.Id).Turn!.Attempt));
        Assert.Equal(account, flow.Operation!.Draft.Account!.Id);

        // The user's own category chosen in the editor replaces the suggestion; the note is kept.
        var editor = AssistantDraftEditor.From(flow.Operation);
        editor.CategoryId = bar.ToString();
        Assert.True(await flow.SaveDraftAsync(editor.BuildPatch(flow.Operation.DraftVersion).Request));
        Assert.Equal((bar, "User", "colazione con Pamela", true), (flow.Operation.Draft.Category!.Id, flow.Operation.Draft.Category.Source,
            flow.Operation.Draft.Note, flow.Operation.CanConfirm));
        Assert.Equal(0, await TransactionCountAsync(account));

        // One explicit confirmation; a repeated request returns the same transaction, never a second one.
        var confirmations = await Task.WhenAll(flow.ConfirmAsync(), flow.ConfirmAsync());
        Assert.Equal([true, false], confirmations);
        var transactionId = flow.Operation.TransactionId!.Value;
        var replay = await api.ConfirmOperationAsync(flow.Operation.Id, flow.Operation.DraftVersion);
        Assert.Equal((transactionId, true), (replay.Value!.TransactionId, replay.Value.Replayed));
        Assert.Equal(1, await TransactionCountAsync(account));

        // Reopening: the same messages, no open draft, nothing lost.
        var reopened = new AssistantChatFlow(api, conversation.Id);
        await reopened.LoadAsync();
        Assert.Equal(flow.Messages.Select(message => (message.Id, message.Content)), reopened.Messages.Select(message => (message.Id, message.Content)));
        Assert.Null(reopened.Operation);
        Assert.Contains(conversation.Id, (await api.GetConversationsAsync()).Value!.Items.Select(item => item.Id));
    }

    private static async Task<(HttpClient Client, Guid Account, Guid Bar)> SignInWithFinanceAsync(AppApiFactory factory)
    {
        var client = factory.CreateClient();
        var signIn = await client.PostAsJsonAsync("/api/auth/dev/sign-in", new DevSignInRequest("assistant-app-test-" + Guid.NewGuid(), null, null));
        signIn.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await signIn.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken);

        (await client.PutAsJsonAsync("/api/me/time-zone", new SetTimeZoneRequest("Europe/Rome"))).EnsureSuccessStatusCode();
        var account = await PostAsync<AccountResponse>(client, "/api/accounts", new CreateAccountRequest("Revolut", "BankAccount", "EUR"));
        await PostAsync<CategoryResponse>(client, "/api/categories", new CreateCategoryRequest("Ristoranti", "Expense", null));
        var bar = await PostAsync<CategoryResponse>(client, "/api/categories", new CreateCategoryRequest("Bar", "Expense", null));

        return (client, account.Id, bar.Id);
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string path, object body)
    {
        var response = await client.PostAsJsonAsync(path, body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<int> TransactionCountAsync(Guid account)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM transactions WHERE account_id = {account}").SingleAsync();
    }

    private async Task<AppApiFactory> FactoryAsync(IAssistantInterpreter interpreter)
    {
        await using var scope = fixture.CreateScope();

        return new AppApiFactory(scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.GetConnectionString()!, interpreter);
    }

    private sealed class AppApiFactory(string connection, IAssistantInterpreter interpreter) : WebApplicationFactory<Program>
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
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(new AdjustableTimeProvider());
                services.AddSingleton(interpreter);
            });
        }
    }
}
