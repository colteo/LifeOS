using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LifeOS.Application.Assistant;
using LifeOS.Contracts.Assistant;
using LifeOS.Contracts.Auth;
using LifeOS.Domain.Assistant;
using LifeOS.Infrastructure.Persistence;
using LifeOS.IntegrationTests.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// AI-007 F1.2-B through the real API pipeline (JWT, routing, endpoints, DI) and real PostgreSQL: the
// HTTP contract, status codes and stable codes, user isolation, idempotency and concurrency. F1 has no AI:
// every accepted message is stored and its turn fails at once with assistant_unavailable.
//
// The host's clock is real time plus an adjustable offset (tokens stay valid), so a lease can be made to
// expire. The database is shared by the PostgreSQL collection: every assertion is scoped to this test's
// own users (each sign-in is a new user).
[Collection(PostgreSqlCollection.Name)]
public class AssistantHttpTests(PostgreSqlFixture fixture)
{
    private const string Conversations = "/api/assistant/conversations";

    // ---- Authentication and DI ----

    [Fact]
    public async Task EveryEndpoint_RequiresAuthentication()
    {
        await using var factory = await FactoryAsync();
        var anonymous = factory.CreateClient();
        var id = Guid.CreateVersion7();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Conversations, new CreateAssistantConversationRequest(id))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Conversations)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync($"{Conversations}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Conversations}/{id}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync($"{Conversations}/{id}/messages", new SendAssistantMessageRequest(id, "Ciao"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/assistant/messages/{id}/retry", null)).StatusCode);
    }

    // Every endpoint's dependencies resolve from the host; the handlers that accept or close a turn are not
    // registered (only AssistantTurnProcessor composes them).
    [Fact]
    public async Task Handlers_AreRegistered_ButCompletingATurnIsNot()
    {
        await using var factory = await FactoryAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;

        Assert.NotNull(services.GetRequiredService<CreateAssistantConversationHandler>());
        Assert.NotNull(services.GetRequiredService<ListAssistantConversationsHandler>());
        Assert.NotNull(services.GetRequiredService<GetAssistantConversationMessagesHandler>());
        Assert.NotNull(services.GetRequiredService<DeleteAssistantConversationHandler>());
        Assert.NotNull(services.GetRequiredService<AssistantTurnProcessor>());
        Assert.Null(services.GetService<SendAssistantMessageHandler>());
        Assert.Null(services.GetService<RetryAssistantTurnHandler>());
        Assert.Null(services.GetService<CompleteAssistantTurnHandler>());
        Assert.Null(services.GetService<FailAssistantTurnHandler>());
        Assert.Same(services.GetRequiredService<IAssistantConversationRepository>(), services.GetRequiredService<IAssistantConversationRepository>());
    }

    // ---- Conversations ----

    [Fact]
    public async Task CreateConversation_Is201_ThenReplays200_AlsoUnderConcurrency()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        var clientId = Guid.CreateVersion7();

        var responses = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => client.PostAsJsonAsync(Conversations, new CreateAssistantConversationRequest(clientId))));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.All(responses, response => Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK }));
        var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadFromJsonAsync<AssistantConversationResponse>()));
        Assert.Single(bodies.Select(body => body!.Id).Distinct());
        var created = responses.Single(response => response.StatusCode == HttpStatusCode.Created);
        var conversation = bodies[Array.IndexOf(responses, created)]!;
        Assert.Equal($"{Conversations}/{conversation.Id}/messages", created.Headers.Location!.ToString());
        Assert.Equal((clientId, conversation.CreatedAtUtc), (conversation.ClientConversationId, conversation.UpdatedAtUtc));

        var replay = await client.PostAsJsonAsync(Conversations, new CreateAssistantConversationRequest(clientId));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(conversation, await replay.Content.ReadFromJsonAsync<AssistantConversationResponse>());
        Assert.Equal([conversation.Id], (await ConversationsAsync(client)).Items.Select(c => c.Id));

        // The same client id of another user is another conversation.
        var other = await SignInAsync(factory);
        var foreign = await other.PostAsJsonAsync(Conversations, new CreateAssistantConversationRequest(clientId));
        Assert.Equal(HttpStatusCode.Created, foreign.StatusCode);
        Assert.NotEqual(conversation.Id, (await foreign.Content.ReadFromJsonAsync<AssistantConversationResponse>())!.Id);
    }

    [Fact]
    public async Task ListConversations_IsPaged_MostRecentlyActiveFirst()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        var first = await CreateConversationAsync(client);
        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        var second = await CreateConversationAsync(client);
        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        var third = await CreateConversationAsync(client);
        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        await SendAsync(client, first, "Ciao"); // first becomes the most recently active

        var page1 = await ConversationsAsync(client, "?limit=2");
        var page2 = await ConversationsAsync(client, $"?limit=2&cursor={page1.NextCursor}");

        Assert.Equal([first, third], page1.Items.Select(c => c.Id));
        Assert.NotNull(page1.NextCursor);
        Assert.Equal([second], page2.Items.Select(c => c.Id));
        Assert.Null(page2.NextCursor);
        Assert.Equal([first, third, second], (await ConversationsAsync(client)).Items.Select(c => c.Id));
    }

    // ---- Messages ----

    // F1: the message is stored and its turn fails at once with assistant_unavailable. No assistant message,
    // no Completed turn, nothing left Processing: the next message is accepted at once.
    [Fact]
    public async Task SendMessage_StoresIt_AndReportsAssistantUnavailable_WithoutAnyReply()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        var conversation = await CreateConversationAsync(client);
        var clientId = Guid.CreateVersion7();

        var response = await client.PostAsJsonAsync($"{Conversations}/{conversation}/messages",
            new SendAssistantMessageRequest(clientId, "  Speso 5€ per colazione con Pamela  "));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var turn = (await response.Content.ReadFromJsonAsync<AssistantTurnResponse>())!;
        Assert.False(turn.Replayed);
        Assert.Null(turn.Reply);
        var message = turn.UserMessage;
        Assert.Equal((conversation, 1, "User", "Speso 5€ per colazione con Pamela", clientId, (Guid?)null),
            (message.ConversationId, message.Sequence, message.Role, message.Content, message.ClientMessageId, message.ReplyToMessageId));
        Assert.Equal(new AssistantTurnStateResponse(AssistantTurnStatuses.Failed, 1, AssistantFailureCodes.AssistantUnavailable), message.Turn);

        var page = await MessagesAsync(client, conversation);
        Assert.Equal(message, Assert.Single(page.Items));
        Assert.Equal(conversation, page.Conversation.Id);
        Assert.Null(page.NextAfterSequence);

        var next = await SendAsync(client, conversation, "Un altro messaggio");
        Assert.Equal(2, next.UserMessage.Sequence);
        Assert.All((await MessagesAsync(client, conversation)).Items, item => Assert.Equal("User", item.Role));
    }

    // Replays never store a second message: the same text returns the stored message (200, a new attempt of
    // its failed turn), another text is a 409 with a stable code.
    [Fact]
    public async Task SendMessage_ReplayIs200_SameMessage_AndAnotherTextIs409()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        var conversation = await CreateConversationAsync(client);
        var clientId = Guid.CreateVersion7();
        var path = $"{Conversations}/{conversation}/messages";

        var first = await SendAsync(client, conversation, "Ciao", clientId);
        var replay = await client.PostAsJsonAsync(path, new SendAssistantMessageRequest(clientId, " Ciao "));
        var otherText = await client.PostAsJsonAsync(path, new SendAssistantMessageRequest(clientId, "Ciao!"));

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = (await replay.Content.ReadFromJsonAsync<AssistantTurnResponse>())!;
        Assert.True(replayed.Replayed);
        Assert.Equal(first.UserMessage.Id, replayed.UserMessage.Id);
        Assert.Equal(new AssistantTurnStateResponse(AssistantTurnStatuses.Failed, 2, AssistantFailureCodes.AssistantUnavailable), replayed.UserMessage.Turn);
        await AssertProblemAsync(otherText, HttpStatusCode.Conflict, AssistantErrorCodes.ClientMessageIdReused);
        Assert.Single((await MessagesAsync(client, conversation)).Items);
    }

    // The same message sent at once several times (e.g. a client retrying after a timeout): one message.
    // Besides 201/200, a request may get 409 turn_in_progress (it saw another request's attempt running) or
    // 503 assistant_busy (its optimistic writes lost three times in a row — each replay of the Failed turn
    // starts a new attempt); both are temporary, and repeating the request returns the stored message.
    [Fact]
    public async Task ConcurrentSends_WithTheSameClientMessageId_StoreOneMessage()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        var conversation = await CreateConversationAsync(client);
        var request = new SendAssistantMessageRequest(Guid.CreateVersion7(), "Ciao");

        var responses = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => client.PostAsJsonAsync($"{Conversations}/{conversation}/messages", request)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        foreach (var response in responses.Where(response => response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.OK)))
        {
            await AssertTemporaryRefusalAsync(response);
        }

        var stored = Assert.Single((await MessagesAsync(client, conversation)).Items);
        Assert.Equal((AssistantTurnStatuses.Failed, AssistantFailureCodes.AssistantUnavailable), (stored.Turn!.Status, stored.Turn.FailureCode));

        var repeated = await client.PostAsJsonAsync($"{Conversations}/{conversation}/messages", request);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(stored.Id, (await repeated.Content.ReadFromJsonAsync<AssistantTurnResponse>())!.UserMessage.Id);
        Assert.Single((await MessagesAsync(client, conversation)).Items);
    }

    // Different messages at once: each is either accepted with its own sequence or refused temporarily
    // (turn_in_progress, assistant_busy); never a duplicate sequence or a database error.
    [Fact]
    public async Task ConcurrentSends_OfDifferentMessages_KeepSequencesUnique()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        var conversation = await CreateConversationAsync(client);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(index =>
            client.PostAsJsonAsync($"{Conversations}/{conversation}/messages", new SendAssistantMessageRequest(Guid.CreateVersion7(), $"Messaggio {index}"))));

        var accepted = responses.Count(response => response.StatusCode == HttpStatusCode.Created);
        Assert.InRange(accepted, 1, 4);
        foreach (var response in responses.Where(response => response.StatusCode != HttpStatusCode.Created))
        {
            await AssertTemporaryRefusalAsync(response);
        }

        var stored = (await MessagesAsync(client, conversation)).Items;
        Assert.Equal(Enumerable.Range(1, accepted), stored.Select(message => message.Sequence));
    }

    [Fact]
    public async Task GetMessages_IsPagedBySequence()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        var conversation = await CreateConversationAsync(client);

        foreach (var text in new[] { "Uno", "Due", "Tre" })
        {
            await SendAsync(client, conversation, text);
        }

        var page1 = await MessagesAsync(client, conversation, "?limit=2");
        var page2 = await MessagesAsync(client, conversation, $"?afterSequence={page1.NextAfterSequence}&limit=2");

        Assert.Equal([(1, "Uno"), (2, "Due")], page1.Items.Select(m => (m.Sequence, m.Content)));
        Assert.Equal(2, page1.NextAfterSequence);
        Assert.Equal([(3, "Tre")], page2.Items.Select(m => (m.Sequence, m.Content)));
        Assert.Null(page2.NextAfterSequence);
        Assert.Empty((await MessagesAsync(client, conversation, "?afterSequence=3")).Items);
        Assert.Equal(3, (await MessagesAsync(client, conversation, $"?limit={AssistantPaging.MaxMessages}")).Items.Count);
    }

    // ---- Turns: retry, in-progress, recovery ----

    // The user's retry of a Failed turn is a new attempt of the same message (no new message); only the
    // latest message can be retried, and an assistant message or an unknown id is a 404.
    [Fact]
    public async Task Retry_StartsANewAttempt_OfTheLatestFailedMessage_Only()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        var conversation = await CreateConversationAsync(client);
        var older = await SendAsync(client, conversation, "Primo");
        var latest = await SendAsync(client, conversation, "Secondo");

        var retry = await client.PostAsync($"/api/assistant/messages/{latest.UserMessage.Id}/retry", null);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var retried = (await retry.Content.ReadFromJsonAsync<AssistantTurnResponse>())!;
        Assert.Equal((latest.UserMessage.Id, false, (AssistantMessageResponse?)null), (retried.UserMessage.Id, retried.Replayed, retried.Reply));
        Assert.Equal(new AssistantTurnStateResponse(AssistantTurnStatuses.Failed, 2, AssistantFailureCodes.AssistantUnavailable), retried.UserMessage.Turn);

        await AssertProblemAsync(await client.PostAsync($"/api/assistant/messages/{older.UserMessage.Id}/retry", null),
            HttpStatusCode.Conflict, AssistantErrorCodes.TurnSuperseded);
        await AssertProblemAsync(await client.PostAsync($"/api/assistant/messages/{Guid.CreateVersion7()}/retry", null),
            HttpStatusCode.NotFound, AssistantErrorCodes.MessageNotFound);
        Assert.Equal(2, (await MessagesAsync(client, conversation)).Items.Count);
    }

    // A turn left Processing by a request that never closed it (in F1 only a crashed request does that):
    // while its lease runs, new messages and retries are 409 turn_in_progress; once it ends, reading the
    // conversation fails it with interrupted, and the conversation goes on.
    [Fact]
    public async Task AProcessingTurn_Is409_UntilItsLeaseEnds_ThenRecoveredAsInterrupted()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        var conversation = await CreateConversationAsync(client);
        var stuck = await AcceptWithoutProcessingAsync(factory, conversation, "Rimasto in sospeso");

        var blocked = await client.PostAsJsonAsync($"{Conversations}/{conversation}/messages", new SendAssistantMessageRequest(Guid.CreateVersion7(), "Altro"));
        var problem = await AssertProblemAsync(blocked, HttpStatusCode.Conflict, AssistantErrorCodes.TurnInProgress);
        Assert.Equal(stuck.ToString(), problem.GetProperty("messageId").GetString());
        await AssertProblemAsync(await client.PostAsync($"/api/assistant/messages/{stuck}/retry", null),
            HttpStatusCode.Conflict, AssistantErrorCodes.TurnInProgress);
        Assert.Equal(AssistantTurnStatuses.Processing, Assert.Single((await MessagesAsync(client, conversation)).Items).Turn!.Status);

        factory.Clock.Advance(AssistantTurnPolicy.LeaseDuration);

        var recovered = Assert.Single((await MessagesAsync(client, conversation)).Items);
        Assert.Equal(new AssistantTurnStateResponse(AssistantTurnStatuses.Failed, 1, AssistantFailureCodes.Interrupted), recovered.Turn);
        Assert.Equal(2, (await SendAsync(client, conversation, "Altro")).UserMessage.Sequence);
    }

    // ---- Deletion ----

    [Fact]
    public async Task Delete_RemovesTheConversationAndItsMessages_AndIsThen404()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        var deleted = await CreateConversationAsync(client);
        var message = await SendAsync(client, deleted, "Ciao");
        var kept = await CreateConversationAsync(client);
        await SendAsync(client, kept, "Resto");

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Conversations}/{deleted}")).StatusCode);

        await AssertProblemAsync(await client.DeleteAsync($"{Conversations}/{deleted}"), HttpStatusCode.NotFound, AssistantErrorCodes.ConversationNotFound);
        await AssertProblemAsync(await client.GetAsync($"{Conversations}/{deleted}/messages"), HttpStatusCode.NotFound, AssistantErrorCodes.ConversationNotFound);
        await AssertProblemAsync(await client.PostAsJsonAsync($"{Conversations}/{deleted}/messages", new SendAssistantMessageRequest(Guid.CreateVersion7(), "Ancora")),
            HttpStatusCode.NotFound, AssistantErrorCodes.ConversationNotFound);
        await AssertProblemAsync(await client.PostAsync($"/api/assistant/messages/{message.UserMessage.Id}/retry", null),
            HttpStatusCode.NotFound, AssistantErrorCodes.MessageNotFound);
        Assert.Equal([kept], (await ConversationsAsync(client)).Items.Select(c => c.Id));

        // The messages are physically gone (cascade); the other conversation's stay.
        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();
        Assert.False(await db.AssistantMessages.AnyAsync(m => m.ConversationId == deleted));
        Assert.False(await db.AssistantConversations.AnyAsync(c => c.Id == deleted));
        Assert.Equal(1, await db.AssistantMessages.CountAsync(m => m.ConversationId == kept));
    }

    // ---- Isolation ----

    // Another user's conversation or message answers exactly like a missing one, and stays untouched.
    [Fact]
    public async Task AnotherUsersResources_AreIndistinguishableFromMissingOnes()
    {
        await using var factory = await FactoryAsync();
        var owner = await SignInAsync(factory);
        var intruder = await SignInAsync(factory);
        var conversation = await CreateConversationAsync(owner);
        var message = await SendAsync(owner, conversation, "Privato");
        var missing = Guid.CreateVersion7();

        Assert.Empty((await ConversationsAsync(intruder)).Items);

        foreach (var (foreign, absent) in new[]
        {
            (intruder.GetAsync($"{Conversations}/{conversation}/messages"), intruder.GetAsync($"{Conversations}/{missing}/messages")),
            (intruder.PostAsJsonAsync($"{Conversations}/{conversation}/messages", new SendAssistantMessageRequest(Guid.CreateVersion7(), "Intruso")),
                intruder.PostAsJsonAsync($"{Conversations}/{missing}/messages", new SendAssistantMessageRequest(Guid.CreateVersion7(), "Intruso"))),
            (intruder.PostAsync($"/api/assistant/messages/{message.UserMessage.Id}/retry", null), intruder.PostAsync($"/api/assistant/messages/{missing}/retry", null)),
            (intruder.DeleteAsync($"{Conversations}/{conversation}"), intruder.DeleteAsync($"{Conversations}/{missing}"))
        })
        {
            using var foreignResponse = await foreign;
            using var absentResponse = await absent;
            Assert.Equal(HttpStatusCode.NotFound, foreignResponse.StatusCode);
            Assert.Equal(await ProblemTextAsync(absentResponse), await ProblemTextAsync(foreignResponse));
        }

        // A user id in the body is not part of the contract and is ignored.
        var smuggled = await intruder.PostAsJsonAsync(Conversations, new { clientConversationId = Guid.CreateVersion7(), userId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.Created, smuggled.StatusCode);
        Assert.Single((await ConversationsAsync(intruder)).Items);

        var stored = Assert.Single((await MessagesAsync(owner, conversation)).Items);
        Assert.Equal(message.UserMessage, stored);
    }

    // ---- Validation ----

    [Fact]
    public async Task InvalidInput_Is400_WithTheField_AndStoresNothing()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        var conversation = await CreateConversationAsync(client);
        var path = $"{Conversations}/{conversation}/messages";

        await AssertInvalidAsync(client.PostAsJsonAsync(Conversations, new CreateAssistantConversationRequest(null)), "clientConversationId");
        await AssertInvalidAsync(client.PostAsJsonAsync(Conversations, new CreateAssistantConversationRequest(Guid.Empty)), "clientConversationId");
        await AssertInvalidAsync(client.GetAsync($"{Conversations}?limit=0"), "limit");
        await AssertInvalidAsync(client.GetAsync($"{Conversations}?limit={AssistantPaging.MaxConversations + 1}"), "limit");
        await AssertInvalidAsync(client.GetAsync($"{Conversations}?cursor=nonvalido"), "cursor");

        await AssertInvalidAsync(client.PostAsJsonAsync(path, new SendAssistantMessageRequest(null, "Ciao")), "clientMessageId");
        await AssertInvalidAsync(client.PostAsJsonAsync(path, new SendAssistantMessageRequest(Guid.Empty, "Ciao")), "clientMessageId");
        await AssertInvalidAsync(client.PostAsJsonAsync(path, new SendAssistantMessageRequest(Guid.CreateVersion7(), null)), "text");
        await AssertInvalidAsync(client.PostAsJsonAsync(path, new SendAssistantMessageRequest(Guid.CreateVersion7(), " \n ")), "text");
        await AssertInvalidAsync(client.PostAsJsonAsync(path,
            new SendAssistantMessageRequest(Guid.CreateVersion7(), new string('a', AssistantMessage.MaxUserContentLength + 1))), "text");
        await AssertInvalidAsync(client.PostAsJsonAsync(path, new SendAssistantMessageRequest(Guid.CreateVersion7(), "Ciao\0mondo")), "text");

        await AssertInvalidAsync(client.GetAsync($"{path}?afterSequence=-1"), "afterSequence");
        await AssertInvalidAsync(client.GetAsync($"{path}?limit=0"), "limit");
        await AssertInvalidAsync(client.GetAsync($"{path}?limit={AssistantPaging.MaxMessages + 1}"), "limit");

        Assert.Empty((await MessagesAsync(client, conversation)).Items);
        Assert.Single((await ConversationsAsync(client)).Items);

        // The limit itself is accepted.
        var longest = await SendAsync(client, conversation, new string('a', AssistantMessage.MaxUserContentLength));
        Assert.Equal(AssistantMessage.MaxUserContentLength, longest.UserMessage.Content.Length);
    }

    // ---- Helpers ----

    // A message accepted as Processing by the Application use case alone, as a request that crashed before
    // closing its attempt would leave it (the HTTP endpoint always closes it in F1).
    private async Task<Guid> AcceptWithoutProcessingAsync(AssistantApiFactory factory, Guid conversation, string text)
    {
        var userId = await OwnerAsync(conversation);
        await using var scope = fixture.CreateScope();
        var result = await new SendAssistantMessageHandler(scope.ServiceProvider.GetRequiredService<IAssistantConversationRepository>(), factory.Clock)
            .HandleAsync(userId, conversation, new SendAssistantMessageCommand(Guid.CreateVersion7(), text), default);
        Assert.Equal(AssistantResultStatus.Ok, result.Status);

        return result.Value!.UserMessage.Id;
    }

    private async Task<Guid> OwnerAsync(Guid conversation)
    {
        await using var scope = fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().AssistantConversations
            .Where(c => c.Id == conversation).Select(c => c.UserId).SingleAsync();
    }

    private static async Task<Guid> CreateConversationAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(Conversations, new CreateAssistantConversationRequest(Guid.CreateVersion7()));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<AssistantConversationResponse>())!.Id;
    }

    private static async Task<AssistantTurnResponse> SendAsync(HttpClient client, Guid conversation, string text, Guid? clientMessageId = null)
    {
        var response = await client.PostAsJsonAsync($"{Conversations}/{conversation}/messages",
            new SendAssistantMessageRequest(clientMessageId ?? Guid.CreateVersion7(), text));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<AssistantTurnResponse>())!;
    }

    private static async Task<AssistantConversationPageResponse> ConversationsAsync(HttpClient client, string query = "") =>
        (await client.GetFromJsonAsync<AssistantConversationPageResponse>($"{Conversations}{query}"))!;

    private static async Task<AssistantMessagePageResponse> MessagesAsync(HttpClient client, Guid conversation, string query = "") =>
        (await client.GetFromJsonAsync<AssistantMessagePageResponse>($"{Conversations}/{conversation}/messages{query}"))!;

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

    // 409 turn_in_progress or 503 assistant_busy: the two refusals a client repeats later.
    private static async Task AssertTemporaryRefusalAsync(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, AssistantErrorCodes.Busy);
        }
        else
        {
            await AssertProblemAsync(response, HttpStatusCode.Conflict, AssistantErrorCodes.TurnInProgress);
        }
    }

    // The user-visible part of a problem response (the trace id differs per request).
    private static async Task<(string?, string?, string?, int)> ProblemTextAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = problem.RootElement;

        return (root.GetProperty("title").GetString(), root.GetProperty("detail").GetString(), root.GetProperty("code").GetString(),
            root.GetProperty("status").GetInt32());
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
        Assert.DoesNotContain("assistant_messages", body);
        Assert.DoesNotContain("   at ", body);
    }

    private async Task<AssistantApiFactory> FactoryAsync()
    {
        await using var scope = fixture.CreateScope();

        return new AssistantApiFactory(scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.GetConnectionString()!);
    }

    private static async Task<HttpClient> SignInAsync(AssistantApiFactory factory)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/dev/sign-in", new DevSignInRequest("assistant-test-" + Guid.NewGuid(), null, null));
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken);

        return client;
    }

    private sealed class AssistantApiFactory(string connection) : WebApplicationFactory<Program>
    {
        public AdjustableTimeProvider Clock { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:PostgreSQL", connection);
            builder.UseSetting("Authentication:LifeOS:SigningKey", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting(LifeOS.Api.Authentication.DevelopmentSignIn.EnabledKey, "true");
            builder.UseSetting("Authentication:Google:ClientId", "test-client-id.apps.googleusercontent.com");
            builder.UseSetting("Authentication:Google:ClientSecret", "test-client-secret");
            builder.UseSetting("Authentication:Google:AllowedEmails:0", "person@example.com");
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock));
        }
    }
}
