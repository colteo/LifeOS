using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Finance.Accounts;
using LifeOS.Contracts.Finance.Categories;
using LifeOS.Contracts.Finance.Transactions;

namespace LifeOS.IntegrationTests.Http;

// Category management through the real pipeline: rename, and delete under the safe rules.
public class CategoryManagementHttpTests
{
    private static readonly DateTimeOffset OccurredAtUtc = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Rename_TopLevelAndSubcategory_Return200AndTheTreeKeepsItsShape()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var parent = await CreateCategoryAsync(client, "Food & Drink", null);
        var child = await CreateCategoryAsync(client, "Groceries", parent.Id);

        // A type or parent in the body is not part of the contract and is ignored.
        var renamedParent = await client.PutAsJsonAsync(
            $"/api/categories/{parent.Id}",
            new { name = " Food ", type = "Income", parentCategoryId = child.Id });
        var renamedChild = await client.PutAsJsonAsync($"/api/categories/{child.Id}", new UpdateCategoryRequest("Supermarket"));

        Assert.Equal(HttpStatusCode.OK, renamedParent.StatusCode);
        Assert.Equal(HttpStatusCode.OK, renamedChild.StatusCode);
        var listed = (await client.GetFromJsonAsync<List<CategoryResponse>>("/api/categories"))!;
        var storedParent = Assert.Single(listed, category => category.Id == parent.Id);
        var storedChild = Assert.Single(listed, category => category.Id == child.Id);
        Assert.Equal(("Food", "Expense", (Guid?)null), (storedParent.Name, storedParent.Type, storedParent.ParentCategoryId));
        Assert.Equal(("Supermarket", "Expense", (Guid?)parent.Id), (storedChild.Name, storedChild.Type, storedChild.ParentCategoryId));
    }

    [Fact]
    public async Task Rename_CaseOnly_Returns200()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var travel = await CreateCategoryAsync(client, "travel", null);

        var response = await client.PutAsJsonAsync($"/api/categories/{travel.Id}", new UpdateCategoryRequest("Travel"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Travel", (await response.Content.ReadFromJsonAsync<CategoryResponse>())!.Name);
    }

    [Fact]
    public async Task Rename_ToASiblingsName_Returns409_ButTheSameNameUnderAnotherParentIsAllowed()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var foodAndDrink = await CreateCategoryAsync(client, "Food & Drink", null);
        await CreateCategoryAsync(client, "Groceries", foodAndDrink.Id);
        var eatingOut = await CreateCategoryAsync(client, "Eating out", foodAndDrink.Id);
        var shopping = await CreateCategoryAsync(client, "Shopping", null);
        var clothing = await CreateCategoryAsync(client, "Clothing", shopping.Id);

        var duplicate = await client.PutAsJsonAsync($"/api/categories/{eatingOut.Id}", new UpdateCategoryRequest("groceries"));
        var otherParent = await client.PutAsJsonAsync($"/api/categories/{clothing.Id}", new UpdateCategoryRequest("Groceries"));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains("A category with this name already exists at this level.", await duplicate.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, otherParent.StatusCode);
    }

    [Fact]
    public async Task Rename_WithBlankName_Returns400()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var travel = await CreateCategoryAsync(client, "Travel", null);

        var response = await client.PutAsJsonAsync($"/api/categories/{travel.Id}", new UpdateCategoryRequest("  "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"name\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RenameAndDelete_AnotherUsersCategory_Return404AndChangeNothing()
    {
        await using var factory = new LifeOSApiFactory();
        var userA = await SignInAsync(factory, "user-a");
        var userB = await SignInAsync(factory, "user-b");
        var ofA = await CreateCategoryAsync(userA, "Travel", null);

        var rename = await userB.PutAsJsonAsync($"/api/categories/{ofA.Id}", new UpdateCategoryRequest("Taken"));
        var delete = await userB.DeleteAsync($"/api/categories/{ofA.Id}");

        Assert.Equal(HttpStatusCode.NotFound, rename.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal("Travel", Assert.Single((await userA.GetFromJsonAsync<List<CategoryResponse>>("/api/categories"))!).Name);
    }

    [Fact]
    public async Task RenameAndDelete_MissingCategory_Return404()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var missing = Guid.CreateVersion7();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"/api/categories/{missing}", new UpdateCategoryRequest("Travel"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/categories/{missing}")).StatusCode);
    }

    [Fact]
    public async Task Delete_UnusedSubcategoryThenItsParent_Return204()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var travel = await CreateCategoryAsync(client, "Travel", null);
        var hotels = await CreateCategoryAsync(client, "Hotels", travel.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/categories/{hotels.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/categories/{travel.Id}")).StatusCode);

        Assert.Empty((await client.GetFromJsonAsync<List<CategoryResponse>>("/api/categories"))!);
    }

    [Fact]
    public async Task Delete_ParentWithSubcategories_Returns409WithReasonAndKeepsBoth()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var travel = await CreateCategoryAsync(client, "Travel", null);
        await CreateCategoryAsync(client, "Hotels", travel.Id);

        var response = await client.DeleteAsync($"/api/categories/{travel.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("This category can't be deleted because it has subcategories.", body);
        Assert.DoesNotContain("FK_", body);
        Assert.Equal(2, (await client.GetFromJsonAsync<List<CategoryResponse>>("/api/categories"))!.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_CategoryUsedByTransactions_Returns409WithReasonAndKeepsIt(bool subcategory)
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var account = await CreateAccountAsync(client);
        var travel = await CreateCategoryAsync(client, "Travel", null);
        var used = subcategory ? await CreateCategoryAsync(client, "Hotels", travel.Id) : travel;
        Assert.Equal(
            HttpStatusCode.Created,
            (await client.PostAsJsonAsync(
                "/api/transactions",
                new CreateTransactionRequest("Expense", 80m, account.Id, null, null, used.Id, OccurredAtUtc, null))).StatusCode);

        var response = await client.DeleteAsync($"/api/categories/{used.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("This category can't be deleted because it is used by transactions.", body);
        Assert.DoesNotContain("FK_", body);
        Assert.Contains(
            (await client.GetFromJsonAsync<List<CategoryResponse>>("/api/categories"))!,
            category => category.Id == used.Id);
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

    private static async Task<AccountResponse> CreateAccountAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/accounts", new CreateAccountRequest("Main", "BankAccount", "EUR"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<AccountResponse>())!;
    }

    private static async Task<CategoryResponse> CreateCategoryAsync(HttpClient client, string name, Guid? parentCategoryId)
    {
        var response = await client.PostAsJsonAsync("/api/categories", new CreateCategoryRequest(name, "Expense", parentCategoryId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<CategoryResponse>())!;
    }
}
