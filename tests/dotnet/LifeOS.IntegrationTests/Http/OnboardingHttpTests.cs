using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Finance.Accounts;
using LifeOS.Contracts.Finance.Categories;
using LifeOS.Contracts.Onboarding;
using LifeOS.Contracts.Users;

namespace LifeOS.IntegrationTests.Http;

public class OnboardingHttpTests
{
    private const string FinanceProfilePath = "/api/onboarding/finance-profile";
    private const string CompletePath = "/api/onboarding/complete";

    [Fact]
    public async Task NewUser_ProgressesThroughOnboarding()
    {
        await using var factory = new LifeOSApiFactory();
        var userA = await SignInAsync(factory, "user-a");

        Assert.Equal("PendingFinanceProfile", (await GetMeAsync(userA)).OnboardingStatus);

        // Finance profile: default currency + the user's own starter categories.
        var setUp = await userA.PostAsJsonAsync(FinanceProfilePath, new SetUpFinanceProfileRequest("eur"));
        Assert.Equal(HttpStatusCode.OK, setUp.StatusCode);
        var afterSetUp = await setUp.Content.ReadFromJsonAsync<MeResponse>();
        Assert.Equal("PendingFirstAccount", afterSetUp!.OnboardingStatus);
        Assert.Equal("EUR", afterSetUp.DefaultCurrency);

        var categories = await userA.GetFromJsonAsync<List<CategoryResponse>>("/api/categories");
        Assert.Equal(15, categories!.Count);
        Assert.All(categories, category => Assert.Null(category.ParentCategoryId));

        var me = await GetMeAsync(userA);
        Assert.Equal("PendingFirstAccount", me.OnboardingStatus);
        Assert.Equal("EUR", me.DefaultCurrency);

        // Completion requires a first account.
        var tooEarly = await userA.PostAsync(CompletePath, content: null);
        Assert.Equal(HttpStatusCode.BadRequest, tooEarly.StatusCode);

        var account = await userA.PostAsJsonAsync("/api/accounts", new CreateAccountRequest("Checking", "BankAccount", "EUR"));
        Assert.Equal(HttpStatusCode.Created, account.StatusCode);

        var complete = await userA.PostAsync(CompletePath, content: null);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.Equal("Completed", (await complete.Content.ReadFromJsonAsync<MeResponse>())!.OnboardingStatus);
        Assert.Equal("Completed", (await GetMeAsync(userA)).OnboardingStatus);
    }

    [Fact]
    public async Task Retries_AreIdempotent()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(FinanceProfilePath, new SetUpFinanceProfileRequest("EUR"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(FinanceProfilePath, new SetUpFinanceProfileRequest("EUR"))).StatusCode);
        Assert.Equal(15, (await client.GetFromJsonAsync<List<CategoryResponse>>("/api/categories"))!.Count);

        var otherCurrency = await client.PostAsJsonAsync(FinanceProfilePath, new SetUpFinanceProfileRequest("USD"));
        Assert.Equal(HttpStatusCode.BadRequest, otherCurrency.StatusCode);
        Assert.Equal("EUR", (await GetMeAsync(client)).DefaultCurrency);

        await client.PostAsJsonAsync("/api/accounts", new CreateAccountRequest("Checking", "BankAccount", "EUR"));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(CompletePath, content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(CompletePath, content: null)).StatusCode);
        Assert.Equal("Completed", (await GetMeAsync(client)).OnboardingStatus);
    }

    [Fact]
    public async Task TwoUsers_GetIndependentStarterCategories()
    {
        await using var factory = new LifeOSApiFactory();
        var userA = await SignInAsync(factory, "user-a");
        var userB = await SignInAsync(factory, "user-b");

        await userA.PostAsJsonAsync(FinanceProfilePath, new SetUpFinanceProfileRequest("EUR"));

        Assert.Equal("PendingFinanceProfile", (await GetMeAsync(userB)).OnboardingStatus);
        Assert.Empty((await userB.GetFromJsonAsync<List<CategoryResponse>>("/api/categories"))!);

        await userB.PostAsJsonAsync(FinanceProfilePath, new SetUpFinanceProfileRequest("CHF"));

        var ofA = (await userA.GetFromJsonAsync<List<CategoryResponse>>("/api/categories"))!.Select(category => category.Id).ToList();
        var ofB = (await userB.GetFromJsonAsync<List<CategoryResponse>>("/api/categories"))!.Select(category => category.Id).ToList();
        Assert.Equal(15, ofA.Count);
        Assert.Equal(15, ofB.Count);
        Assert.Empty(ofA.Intersect(ofB));
        Assert.Equal("CHF", (await GetMeAsync(userB)).DefaultCurrency);
        Assert.Equal("EUR", (await GetMeAsync(userA)).DefaultCurrency);
    }

    [Fact]
    public async Task CompleteBeforeFinanceProfile_IsRejected()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");

        var response = await client.PostAsync(CompletePath, content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("PendingFinanceProfile", (await GetMeAsync(client)).OnboardingStatus);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("EU")]
    [InlineData("12€")]
    public async Task FinanceProfile_WithInvalidCurrency_ReturnsValidationProblem(string? currency)
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");

        var response = await client.PostAsJsonAsync(FinanceProfilePath, new SetUpFinanceProfileRequest(currency));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Categories.Categories);
    }

    [Theory]
    [InlineData(FinanceProfilePath)]
    [InlineData(CompletePath)]
    public async Task OnboardingEndpoints_WithoutAccessToken_Return401(string path)
    {
        await using var factory = new LifeOSApiFactory();

        var response = await factory.CreateClient().PostAsJsonAsync(path, new SetUpFinanceProfileRequest("EUR"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.Categories.Categories);
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

    private static async Task<MeResponse> GetMeAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<MeResponse>("/api/me"))!;
}
