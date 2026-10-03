using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LifeOS.App.Services.Nutrition;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Nutrition;
using LifeOS.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// NUT-001 through the real API pipeline (JWT, routing, endpoints) and real PostgreSQL, including the
// app's NutritionApiClient and MealJournal request shaping.
[Collection(PostgreSqlCollection.Name)]
public class NutritionHttpTests(PostgreSqlFixture fixture)
{
    private const string Meals = "/api/nutrition/meals";
    private static readonly DateOnly Today = new(2026, 10, 3);

    [Fact]
    public async Task EveryEndpoint_RequiresAuthentication()
    {
        await using var factory = await FactoryAsync();
        var anonymous = factory.CreateClient();
        var path = $"{Meals}/{Guid.CreateVersion7()}";

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Meals}?date=2026-10-03")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Meals, Create("Pasta", "13:10"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(path, new UpdateMealRequest("Pasta", null, new TimeOnly(13, 0)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Crud_DateFiltering_AndNewestFirst()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);

        var created = await client.PostAsJsonAsync(Meals, Create("  Yogurt greco, banana e caffè ", "08:15", "breakfast"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var breakfast = (await created.Content.ReadFromJsonAsync<MealResponse>())!;
        Assert.Equal(created.Headers.Location!.ToString(), $"{Meals}/{breakfast.Id}");
        Assert.Equal(("Yogurt greco, banana e caffè", "Breakfast", Today, new TimeOnly(8, 15)), (breakfast.Description, breakfast.MealType, breakfast.Date, breakfast.Time));
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 6, 15, 0, TimeSpan.Zero), breakfast.OccurredAtUtc);

        var coffee = await CreateAsync(client, Create("Caffè", "10:30"));
        Assert.Null(coffee.MealType);
        await CreateAsync(client, Create("Cena", "20:15", "Dinner"));
        await CreateAsync(client, Create("Pranzo", "13:10", "Lunch"));
        await CreateAsync(client, Create("Ieri", "23:30") with { Date = Today.AddDays(-1) });

        Assert.Equal(["Cena", "Pranzo", "Caffè", "Yogurt greco, banana e caffè"], (await DayAsync(client, "2026-10-03")).Select(meal => meal.Description));
        Assert.Equal(["Ieri"], (await DayAsync(client, "2026-10-02")).Select(meal => meal.Description));
        Assert.Empty(await DayAsync(client, "2026-10-04"));

        var updated = await client.PutAsJsonAsync($"{Meals}/{breakfast.Id}", new UpdateMealRequest("Yogurt e frutta\nCaffè", "", new TimeOnly(21, 0)));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var edited = (await updated.Content.ReadFromJsonAsync<MealResponse>())!;
        Assert.Equal(("Yogurt e frutta\nCaffè", (string?)null, Today, new TimeOnly(21, 0)), (edited.Description, edited.MealType, edited.Date, edited.Time));
        Assert.Equal(breakfast.CreatedAtUtc, edited.CreatedAtUtc, TimeSpan.FromMicroseconds(1));
        Assert.Equal(["Yogurt e frutta\nCaffè", "Cena", "Pranzo", "Caffè"], (await DayAsync(client, "2026-10-03")).Select(meal => meal.Description));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Meals}/{coffee.Id}")).StatusCode);
        Assert.Equal(["Yogurt e frutta\nCaffè", "Cena", "Pranzo"], (await DayAsync(client, "2026-10-03")).Select(meal => meal.Description));
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Meals}/{coffee.Id}")).StatusCode);
    }

    [Fact]
    public async Task InvalidInput_Is400_WithTheField()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        var meal = await CreateAsync(client, Create("Pasta", "13:10"));

        await AssertInvalidAsync(client.PostAsJsonAsync(Meals, Create("   ", "13:10")), "description");
        await AssertInvalidAsync(client.PostAsJsonAsync(Meals, Create(null, "13:10")), "description");
        await AssertInvalidAsync(client.PostAsJsonAsync(Meals, Create(new string('a', 2001), "13:10")), "description");
        await AssertInvalidAsync(client.PostAsJsonAsync(Meals, Create("Pasta", "13:10", "Brunch")), "mealType");
        await AssertInvalidAsync(client.PostAsJsonAsync(Meals, Create("Pasta", "13:10", "1")), "mealType");
        await AssertInvalidAsync(client.PostAsJsonAsync(Meals, Create("Pasta", "13:10") with { Time = null }), "time");
        await AssertInvalidAsync(client.PostAsJsonAsync(Meals, Create("Pasta", "13:10:30")), "time");
        await AssertInvalidAsync(client.PostAsJsonAsync(Meals, Create("Pasta", "13:10") with { Date = null }), "date");
        await AssertInvalidAsync(client.PostAsJsonAsync(Meals, Create("Pasta", "13:10") with { Date = DateOnly.MaxValue }), "date");
        await AssertInvalidAsync(client.PostAsJsonAsync(Meals, Create("Pasta", "13:10") with { UtcOffsetMinutes = null }), "utcOffsetMinutes");
        await AssertInvalidAsync(client.PostAsJsonAsync(Meals, Create("Pasta", "13:10") with { UtcOffsetMinutes = 900 }), "utcOffsetMinutes");

        var path = $"{Meals}/{meal.Id}";
        await AssertInvalidAsync(client.PutAsJsonAsync(path, new UpdateMealRequest(" ", null, new TimeOnly(9, 0))), "description");
        await AssertInvalidAsync(client.PutAsJsonAsync(path, new UpdateMealRequest("Ok", "Dessert", new TimeOnly(9, 0))), "mealType");
        await AssertInvalidAsync(client.PutAsJsonAsync(path, new UpdateMealRequest("Ok", null, null)), "time");

        await AssertInvalidAsync(client.GetAsync(Meals), "date");
        await AssertInvalidAsync(client.GetAsync($"{Meals}?date=03/10/2026"), "date");
        await AssertInvalidAsync(client.GetAsync($"{Meals}?date=2026-02-30"), "date");
        await AssertInvalidAsync(client.GetAsync($"{Meals}?date=0001-01-01"), "date");

        // Nothing invalid was stored or changed.
        var stored = Assert.Single(await DayAsync(client, "2026-10-03"));
        Assert.Equal(("Pasta", new TimeOnly(13, 10)), (stored.Description, stored.Time));
    }

    [Fact]
    public async Task MissingOrOtherUsersMeal_Is404_AndStaysUntouched()
    {
        await using var factory = await FactoryAsync();
        var owner = await SignInAsync(factory);
        var other = await SignInAsync(factory);
        var meal = await CreateAsync(owner, Create("Pasta", "13:10", "Lunch"));
        var path = $"{Meals}/{meal.Id}";
        var missing = $"{Meals}/{Guid.CreateVersion7()}";
        var edit = new UpdateMealRequest("Hacked", null, new TimeOnly(9, 0));

        Assert.Empty(await DayAsync(other, "2026-10-03"));
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync(path, edit)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsJsonAsync(missing, edit)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync(missing)).StatusCode);

        var stored = Assert.Single(await DayAsync(owner, "2026-10-03"));
        Assert.Equal(("Pasta", "Lunch"), (stored.Description, stored.MealType));
    }

    [Fact]
    public async Task AppClient_AddsEditsAndDeletes_OnTheSelectedDay()
    {
        await using var factory = await FactoryAsync();
        var api = new NutritionApiClient(await SignInAsync(factory));
        var rome = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");
        var yesterday = Today.AddDays(-1);

        Assert.True((await api.CreateMealAsync(MealJournal.CreateRequest("Yogurt greco, banana e caffè", "Breakfast", Today, new TimeOnly(8, 15), rome))).IsSuccess);
        Assert.True((await api.CreateMealAsync(MealJournal.CreateRequest("Caffè", "", Today, new TimeOnly(10, 30), rome))).IsSuccess);
        var late = (await api.CreateMealAsync(MealJournal.CreateRequest("Tisana", "", yesterday, new TimeOnly(23, 30), rome))).Value!;
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 21, 30, 0, TimeSpan.Zero), late.OccurredAtUtc);

        var today = (await api.GetMealsAsync(Today)).Value!;
        Assert.Equal(["10:30", "08:15 · Breakfast"], today.Select(MealJournal.Heading));
        Assert.Equal(["Tisana"], (await api.GetMealsAsync(yesterday)).Value!.Select(meal => meal.Description));

        var invalid = await api.CreateMealAsync(MealJournal.CreateRequest(" ", "", Today, new TimeOnly(9, 0), rome));
        Assert.False(invalid.IsSuccess);
        Assert.Contains("Describe what you ate.", invalid.Errors.Single());

        var edited = await api.UpdateMealAsync(late.Id, MealJournal.UpdateRequest("Tisana alla camomilla", "Snack", new TimeOnly(22, 0)));
        Assert.Equal(("Tisana alla camomilla", "Snack", yesterday), (edited.Value!.Description, edited.Value.MealType, edited.Value.Date));

        Assert.True((await api.DeleteMealAsync(late.Id)).IsSuccess);
        Assert.Equal("This meal no longer exists.", (await api.DeleteMealAsync(late.Id)).Errors.Single());
        Assert.Empty((await api.GetMealsAsync(yesterday)).Value!);
    }

    private static CreateMealRequest Create(string? description, string time, string? mealType = null) =>
        new(description, mealType, Today, TimeOnly.Parse(time), 120);

    private static async Task<MealResponse> CreateAsync(HttpClient client, CreateMealRequest request)
    {
        var response = await client.PostAsJsonAsync(Meals, request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<MealResponse>())!;
    }

    private static async Task<List<MealResponse>> DayAsync(HttpClient client, string date) =>
        (await client.GetFromJsonAsync<List<MealResponse>>($"{Meals}?date={date}"))!;

    private static async Task AssertInvalidAsync(Task<HttpResponseMessage> request, string field)
    {
        using var response = await request;
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Exception", body);
        using var problem = JsonDocument.Parse(body);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), body);
    }

    private async Task<NutritionApiFactory> FactoryAsync()
    {
        await using var scope = fixture.CreateScope();

        return new NutritionApiFactory(scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.GetConnectionString()!);
    }

    private static async Task<HttpClient> SignInAsync(NutritionApiFactory factory)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/dev/sign-in", new DevSignInRequest("nutrition-test-" + Guid.NewGuid(), null, null));
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken);

        return client;
    }

    private sealed class NutritionApiFactory(string connection) : WebApplicationFactory<Program>
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
