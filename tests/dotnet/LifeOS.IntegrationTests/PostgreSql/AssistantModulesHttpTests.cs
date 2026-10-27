using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LifeOS.Application.Assistant;
using LifeOS.Contracts.Assistant;
using LifeOS.Contracts.Auth;
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

// AI-008 through the real API pipeline (JWT, routing, endpoints, the DI composition of Program.cs in Development,
// where water and meals are enabled) and real PostgreSQL, with a scripted interpreter in place of the AI service:
// a water or meal draft opened by the conversation is shown, changed, confirmed or cancelled through the same
// endpoints as a Finance one, with its own draft section, and a confirmation writes exactly one record.
[Collection(PostgreSqlCollection.Name)]
public class AssistantModulesHttpTests(PostgreSqlFixture fixture)
{
    private const string Assistant = "/api/assistant";

    [Fact]
    public async Task AWaterDraft_IsShownChangedAndConfirmedOnce_ThroughTheOperationEndpoints()
    {
        var interpreter = new ScriptedAssistantInterpreter().Then(Drink(Water(Stated("300", "300 ml"), Stated("ml"))));
        await using var factory = await FactoryAsync(interpreter);
        var client = await SignInAsync(factory);
        var conversation = await ConversationAsync(client);

        var turn = await SendAsync(client, conversation, "Ho bevuto 300 ml d'acqua");
        Assert.Equal("Ho preparato 300 ml d'acqua. Vuoi modificarla?", turn.Reply!.Content);

        var open = (await client.GetFromJsonAsync<AssistantConversationDetailResponse>($"{Assistant}/conversations/{conversation}"))!.ActiveOperation!;
        Assert.Equal((AssistantOperationKindNames.HydrationIntakeCreate, AssistantOperationStatuses.AwaitingConfirmation, 300, true),
            (open.Kind, open.Status, open.Hydration!.AmountMl, open.CanConfirm));
        Assert.Null(open.Draft);
        Assert.Null(open.Meal);
        Assert.Equal("Europe/Rome", open.Hydration.Occurrence!.TimeZoneId);

        // A Finance or meal field does not belong to a water draft.
        await AssertInvalidAsync(client.PatchAsJsonAsync($"{Assistant}/operations/{open.Id}", Patch(1) with { Amount = 5m }), "amount");
        await AssertInvalidAsync(client.PatchAsJsonAsync($"{Assistant}/operations/{open.Id}", Patch(1) with { MealType = "Lunch" }), "mealType");
        await AssertInvalidAsync(client.PatchAsJsonAsync($"{Assistant}/operations/{open.Id}", Patch(1) with { AmountMl = 4001 }), "amountMl");

        var changed = await ReadAsync<AssistantOperationResponse>(await client.PatchAsJsonAsync($"{Assistant}/operations/{open.Id}",
            Patch(1) with { AmountMl = 500 }));
        Assert.Equal((2, 500), (changed.DraftVersion, changed.Hydration!.AmountMl));
        Assert.Equal(0, await CountAsync("hydration_intakes", turn.UserMessage.ConversationId));

        var stale = await client.PostAsJsonAsync($"{Assistant}/operations/{open.Id}/confirm", new AssistantOperationVersionRequest(1));
        await AssertProblemAsync(stale, HttpStatusCode.Conflict, AssistantErrorCodes.VersionConflict);

        var confirmed = await ReadAsync<AssistantOperationConfirmationResponse>(
            await client.PostAsJsonAsync($"{Assistant}/operations/{open.Id}/confirm", new AssistantOperationVersionRequest(2)));
        var replayed = await ReadAsync<AssistantOperationConfirmationResponse>(
            await client.PostAsJsonAsync($"{Assistant}/operations/{open.Id}/confirm", new AssistantOperationVersionRequest(2)));

        Assert.Equal((false, true), (confirmed.Replayed, replayed.Replayed));
        Assert.Null(confirmed.TransactionId);
        Assert.Equal((confirmed.ResultId, AssistantOperationStatuses.Executed), (replayed.ResultId, replayed.Operation.Status));
        Assert.Equal(confirmed.ResultId, confirmed.Operation.ResultId);
        Assert.Equal(1, await IntakesAsync(confirmed.ResultId!.Value, 500));

        var read = (await client.GetFromJsonAsync<AssistantOperationResponse>($"{Assistant}/operations/{open.Id}"))!;
        Assert.Equal((AssistantOperationStatuses.Executed, confirmed.ResultId), (read.Status, read.ResultId));
        Assert.Null((await client.GetFromJsonAsync<AssistantConversationDetailResponse>($"{Assistant}/conversations/{conversation}"))!.ActiveOperation);

        // An executed draft can no longer change.
        await AssertProblemAsync(await client.PatchAsJsonAsync($"{Assistant}/operations/{open.Id}", Patch(2) with { AmountMl = 250 }),
            HttpStatusCode.Conflict, AssistantErrorCodes.OperationExecuted);
    }

    [Fact]
    public async Task AMealDraft_IsChangedAndCancelled_AndNothingIsRecorded()
    {
        var interpreter = new ScriptedAssistantInterpreter().Then(Meal(MealFields(Stated("pasta al pomodoro"))));
        await using var factory = await FactoryAsync(interpreter);
        var client = await SignInAsync(factory);
        var conversation = await ConversationAsync(client);

        await SendAsync(client, conversation, "Ho mangiato pasta al pomodoro");
        var open = (await client.GetFromJsonAsync<AssistantConversationDetailResponse>($"{Assistant}/conversations/{conversation}"))!.ActiveOperation!;
        Assert.Equal((AssistantOperationKindNames.NutritionMealCreate, "pasta al pomodoro", (string?)null, true),
            (open.Kind, open.Meal!.Description, open.Meal.MealType, open.CanConfirm));

        await AssertInvalidAsync(client.PatchAsJsonAsync($"{Assistant}/operations/{open.Id}", Patch(1) with { MealType = "pranzo" }), "mealType");
        await AssertInvalidAsync(client.PatchAsJsonAsync($"{Assistant}/operations/{open.Id}", Patch(1) with { AmountMl = 300 }), "amountMl");

        var changed = await ReadAsync<AssistantOperationResponse>(await client.PatchAsJsonAsync($"{Assistant}/operations/{open.Id}",
            Patch(1) with { MealType = "Snack", Description = "pasta al pomodoro e insalata" }));
        Assert.Equal(("Snack", "pasta al pomodoro e insalata"), (changed.Meal!.MealType, changed.Meal.Description));

        var cancelled = await ReadAsync<AssistantOperationResponse>(
            await client.PostAsJsonAsync($"{Assistant}/operations/{open.Id}/cancel", new AssistantOperationVersionRequest(2)));
        Assert.Equal(AssistantOperationStatuses.Cancelled, cancelled.Status);

        await AssertProblemAsync(await client.PostAsJsonAsync($"{Assistant}/operations/{open.Id}/confirm", new AssistantOperationVersionRequest(2)),
            HttpStatusCode.Conflict, AssistantErrorCodes.OperationCancelled);
        Assert.Equal(0, await MealsAsync(conversation));
    }

    [Fact]
    public async Task AnotherUsersWaterDraft_IsNotFound()
    {
        var interpreter = new ScriptedAssistantInterpreter().Then(Drink(Water(Stated("300", "300 ml"), Stated("ml"))));
        await using var factory = await FactoryAsync(interpreter);
        var owner = await SignInAsync(factory);
        var other = await SignInAsync(factory);
        var conversation = await ConversationAsync(owner);
        await SendAsync(owner, conversation, "300 ml");
        var open = (await owner.GetFromJsonAsync<AssistantConversationDetailResponse>($"{Assistant}/conversations/{conversation}"))!.ActiveOperation!;

        await AssertProblemAsync(await other.GetAsync($"{Assistant}/operations/{open.Id}"), HttpStatusCode.NotFound, AssistantErrorCodes.OperationNotFound);
        await AssertProblemAsync(await other.PostAsJsonAsync($"{Assistant}/operations/{open.Id}/confirm", new AssistantOperationVersionRequest(1)),
            HttpStatusCode.NotFound, AssistantErrorCodes.OperationNotFound);
    }

    [Fact]
    public async Task TheDevelopmentHost_ComposesTheRouterWithTheThreeModules()
    {
        await using var factory = await FactoryAsync(new ScriptedAssistantInterpreter());
        await using var scope = factory.Services.CreateAsyncScope();

        Assert.IsType<AssistantCapabilityRouter>(scope.ServiceProvider.GetRequiredService<IAssistantCapability>());
        Assert.Equal(["FinanceAssistantModule", "HydrationAssistantModule", "MealAssistantModule"],
            scope.ServiceProvider.GetServices<IAssistantModule>().Select(module => module.GetType().Name));
    }

    // ---- Helpers ----

    private static UpdateAssistantOperationRequest Patch(int version) => new(version, null, null, null, null, null, null, null, null, null);

    private static async Task<AssistantTurnResponse> SendAsync(HttpClient client, Guid conversation, string text)
    {
        var response = await client.PostAsJsonAsync($"{Assistant}/conversations/{conversation}/messages",
            new SendAssistantMessageRequest(Guid.CreateVersion7(), text));

        return await ReadAsync<AssistantTurnResponse>(response);
    }

    private static async Task<Guid> ConversationAsync(HttpClient client) =>
        (await ReadAsync<AssistantConversationResponse>(
            await client.PostAsJsonAsync($"{Assistant}/conversations", new CreateAssistantConversationRequest(Guid.CreateVersion7())))).Id;

    private static async Task<HttpClient> SignInAsync(ModulesApiFactory factory)
    {
        var client = factory.CreateClient();
        var signIn = await client.PostAsJsonAsync("/api/auth/dev/sign-in", new DevSignInRequest("assistant-modules-test-" + Guid.NewGuid(), null, null));
        signIn.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await signIn.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken);
        (await client.PutAsJsonAsync("/api/me/time-zone", new SetTimeZoneRequest("Europe/Rome"))).EnsureSuccessStatusCode();

        return client;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"{response.StatusCode}: {body}");
        Assert.DoesNotContain("Exception", body);
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    private static async Task AssertInvalidAsync(Task<HttpResponseMessage> request, string field)
    {
        using var response = await request;
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{response.StatusCode}: {body}");

        using var problem = JsonDocument.Parse(body);
        Assert.Equal(AssistantErrorCodes.InvalidRequest, problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), body);
    }

    private async Task<int> IntakesAsync(Guid id, int amountMl)
    {
        await using var scope = fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM hydration_intakes WHERE id = {id} AND amount_ml = {amountMl}").SingleAsync();
    }

    // Intakes of the conversation's owner (none may exist before a confirmation).
    private async Task<int> CountAsync(string table, Guid conversation)
    {
        await using var scope = fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database
            .SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM {table} t JOIN assistant_conversations c ON c.user_id = t.user_id WHERE c.id = {{0}}",
                conversation)
            .SingleAsync();
    }

    private Task<int> MealsAsync(Guid conversation) => CountAsync("meal_entries", conversation);

    private async Task<ModulesApiFactory> FactoryAsync(IAssistantInterpreter interpreter)
    {
        await using var scope = fixture.CreateScope();

        return new ModulesApiFactory(scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.GetConnectionString()!, interpreter);
    }

    private sealed class ModulesApiFactory(string connection, IAssistantInterpreter interpreter) : WebApplicationFactory<Program>
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
