using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Journal;
using LifeOS.Domain.Journal;
using LifeOS.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// JRN-001 through the real API pipeline (JWT, routing, endpoints) and real PostgreSQL.
[Collection(PostgreSqlCollection.Name)]
public class JournalHttpTests(PostgreSqlFixture fixture)
{
    private const string Journal = "/api/journal";
    private static readonly DateTimeOffset Day = new(2026, 10, 1, 21, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task EveryEndpoint_RequiresAuthentication()
    {
        await using var factory = await FactoryAsync();
        var anonymous = factory.CreateClient();
        var path = $"{Journal}/{Guid.CreateVersion7()}";
        var body = new CreateJournalEntryRequest(Day, null, "Testo");

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Journal)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Journal, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(path, new UpdateJournalEntryRequest(Day, null, "Testo"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Crud_AndNewestFirst()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);

        var created = await client.PostAsJsonAsync(Journal,
            new CreateJournalEntryRequest(new DateTimeOffset(2026, 10, 1, 23, 30, 0, TimeSpan.FromHours(2)), "  Sera ", "  Pensieri\n\nsparsi.  "));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var entry = (await created.Content.ReadFromJsonAsync<JournalEntryResponse>())!;
        Assert.Equal($"{Journal}/{entry.Id}", created.Headers.Location!.ToString());
        Assert.Equal((Day, "Sera", "Pensieri\n\nsparsi."), (entry.OccurredAtUtc, entry.Title, entry.Content));
        Assert.Equal(entry.CreatedAtUtc, entry.UpdatedAtUtc);

        var fetched = (await client.GetFromJsonAsync<JournalEntryResponse>($"{Journal}/{entry.Id}"))!;
        Assert.Equal(entry, fetched);

        var untitled = await CreateAsync(client, Day.AddDays(1), "Domani", null);
        Assert.Null(untitled.Title);
        var older = await CreateAsync(client, Day.AddDays(-3), "Prima", "Vecchia");

        Assert.Equal([untitled.Id, entry.Id, older.Id], (await PageAsync(client)).Items.Select(item => item.Id));

        // Moving an entry's occurred-at time moves it in the timeline; the title can be removed.
        var updated = await client.PutAsJsonAsync($"{Journal}/{older.Id}", new UpdateJournalEntryRequest(Day.AddDays(2), null, "Riscritta"));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var edited = (await updated.Content.ReadFromJsonAsync<JournalEntryResponse>())!;
        Assert.Equal((Day.AddDays(2), (string?)null, "Riscritta", older.CreatedAtUtc), (edited.OccurredAtUtc, edited.Title, edited.Content, edited.CreatedAtUtc));
        Assert.True(edited.UpdatedAtUtc >= older.UpdatedAtUtc);
        Assert.Equal(edited, await client.GetFromJsonAsync<JournalEntryResponse>($"{Journal}/{older.Id}"));
        Assert.Equal([older.Id, untitled.Id, entry.Id], (await PageAsync(client)).Items.Select(item => item.Id));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Journal}/{entry.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Journal}/{entry.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Journal}/{entry.Id}")).StatusCode);
        Assert.Equal([older.Id, untitled.Id], (await PageAsync(client)).Items.Select(item => item.Id));
    }

    [Fact]
    public async Task InvalidInput_Is400_WithTheField_AndChangesNothing()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        var entry = await CreateAsync(client, Day, "Testo", "Titolo");

        await AssertInvalidAsync(client.PostAsJsonAsync(Journal, new CreateJournalEntryRequest(Day, null, null)), "content");
        await AssertInvalidAsync(client.PostAsJsonAsync(Journal, new CreateJournalEntryRequest(Day, null, " \n ")), "content");
        await AssertInvalidAsync(client.PostAsJsonAsync(Journal,
            new CreateJournalEntryRequest(Day, null, new string('c', JournalEntry.ContentMaxLength + 1))), "content");
        await AssertInvalidAsync(client.PostAsJsonAsync(Journal,
            new CreateJournalEntryRequest(Day, new string('t', JournalEntry.TitleMaxLength + 1), "Testo")), "title");
        await AssertInvalidAsync(client.PostAsJsonAsync(Journal, new CreateJournalEntryRequest(null, null, "Testo")), "occurredAtUtc");
        await AssertInvalidAsync(client.PostAsJsonAsync(Journal, new CreateJournalEntryRequest(DateTimeOffset.MinValue, null, "Testo")), "occurredAtUtc");

        // A user id in the body is not part of the contract and is ignored.
        var smuggled = await client.PostAsJsonAsync(Journal, new { occurredAtUtc = Day, content = "Mio", userId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.Created, smuggled.StatusCode);
        var mine = (await smuggled.Content.ReadFromJsonAsync<JournalEntryResponse>())!;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Journal}/{mine.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Journal}/{mine.Id}")).StatusCode);

        var path = $"{Journal}/{entry.Id}";
        await AssertInvalidAsync(client.PutAsJsonAsync(path, new UpdateJournalEntryRequest(Day, null, "  ")), "content");
        await AssertInvalidAsync(client.PutAsJsonAsync(path, new UpdateJournalEntryRequest(null, null, "Testo")), "occurredAtUtc");
        await AssertInvalidAsync(client.PutAsJsonAsync(path,
            new UpdateJournalEntryRequest(Day, new string('t', JournalEntry.TitleMaxLength + 1), "Testo")), "title");

        var stored = Assert.Single((await PageAsync(client)).Items);
        Assert.Equal(entry, stored);
    }

    [Fact]
    public async Task MissingOrOtherUsersEntry_Is404_AndStaysUntouched()
    {
        await using var factory = await FactoryAsync();
        var owner = await SignInAsync(factory);
        var other = await SignInAsync(factory);
        var entry = await CreateAsync(owner, Day, "Privato", "Mio");
        var path = $"{Journal}/{entry.Id}";
        var missing = $"{Journal}/{Guid.CreateVersion7()}";
        var edit = new UpdateJournalEntryRequest(Day, null, "Rubato");

        Assert.Empty((await PageAsync(other)).Items);

        // Another user's entry is reported exactly like a missing one.
        var foreignGet = await other.GetAsync(path);
        var missingGet = await owner.GetAsync(missing);
        Assert.Equal(HttpStatusCode.NotFound, foreignGet.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingGet.StatusCode);
        Assert.Equal(await ProblemTextAsync(missingGet), await ProblemTextAsync(foreignGet));

        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync(path, edit)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsJsonAsync(missing, edit)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync(missing)).StatusCode);

        Assert.Equal(entry, await owner.GetFromJsonAsync<JournalEntryResponse>(path));
    }

    [Fact]
    public async Task Pagination_UsesLimitAndCursor()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        var expected = new List<Guid>();

        for (var i = 0; i < 5; i++)
        {
            expected.Add((await CreateAsync(client, Day.AddDays(-i), $"entry {i}", null)).Id);
        }

        var first = await PageAsync(client, "?limit=2");
        Assert.Equal(expected[..2], first.Items.Select(item => item.Id));
        Assert.NotNull(first.NextCursor);

        var second = await PageAsync(client, $"?limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Equal(expected[2..4], second.Items.Select(item => item.Id));

        var last = await PageAsync(client, $"?limit=2&cursor={Uri.EscapeDataString(second.NextCursor!)}");
        Assert.Equal(expected[4..], last.Items.Select(item => item.Id));
        Assert.Null(last.NextCursor);

        // Default page size.
        Assert.Equal(expected, (await PageAsync(client)).Items.Select(item => item.Id));

        await AssertInvalidAsync(client.GetAsync($"{Journal}?limit=0"), "limit");
        await AssertInvalidAsync(client.GetAsync($"{Journal}?limit=51"), "limit");
        await AssertInvalidAsync(client.GetAsync($"{Journal}?cursor=nope"), "cursor");
        await AssertInvalidAsync(client.GetAsync($"{Journal}?cursor=1_2"), "cursor");
        await AssertInvalidAsync(client.GetAsync($"{Journal}?cursor=-1_2_{Guid.CreateVersion7():N}"), "cursor");
        await AssertInvalidAsync(client.GetAsync($"{Journal}?cursor=1_2_not-a-guid"), "cursor");
    }

    private static async Task<JournalEntryResponse> CreateAsync(HttpClient client, DateTimeOffset occurredAt, string content, string? title)
    {
        var response = await client.PostAsJsonAsync(Journal, new CreateJournalEntryRequest(occurredAt, title, content));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JournalEntryResponse>())!;
    }

    private static async Task<JournalEntryPageResponse> PageAsync(HttpClient client, string query = "") =>
        (await client.GetFromJsonAsync<JournalEntryPageResponse>($"{Journal}{query}"))!;

    // The user-visible part of a problem response (the trace id differs per request).
    private static async Task<(string?, string?, int)> ProblemTextAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = problem.RootElement;

        return (root.GetProperty("title").GetString(), root.GetProperty("detail").GetString(), root.GetProperty("status").GetInt32());
    }

    private static async Task AssertInvalidAsync(Task<HttpResponseMessage> request, string field)
    {
        using var response = await request;
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Exception", body);
        using var problem = JsonDocument.Parse(body);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), body);
    }

    private async Task<JournalApiFactory> FactoryAsync()
    {
        await using var scope = fixture.CreateScope();

        return new JournalApiFactory(scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.GetConnectionString()!);
    }

    private static async Task<HttpClient> SignInAsync(JournalApiFactory factory)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/dev/sign-in", new DevSignInRequest("journal-test-" + Guid.NewGuid(), null, null));
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken);

        return client;
    }

    private sealed class JournalApiFactory(string connection) : WebApplicationFactory<Program>
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
