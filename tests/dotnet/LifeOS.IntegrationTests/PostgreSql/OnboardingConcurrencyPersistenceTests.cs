using LifeOS.Application.Finance.Categories;
using LifeOS.Application.Onboarding;
using LifeOS.Application.Onboarding.SetUpFinanceProfile;
using LifeOS.Application.Users;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public class OnboardingConcurrencyPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);

    // ---- Test 11: the conditional onboarding UPDATE ----

    [Fact]
    public async Task ConcurrentSetUpWithSameCurrency_OneUpdateWins_LoserResolvesIdempotently()
    {
        var user = await NewUserAsync();

        var results = await RaceSetUpAsync(user.Id, "EUR", "EUR");

        Assert.Single(results, won => won);

        // The losing request re-runs the real handler: already set up with the same currency → Ok.
        await using (var loser = fixture.CreateScope())
        {
            var outcome = await SetUpHandler(loser, Users(loser), Categories(loser))
                .HandleAsync(user.Id, new SetUpFinanceProfileCommand("EUR"), CancellationToken.None);
            Assert.Equal(OnboardingResultStatus.Ok, outcome.Status);
        }

        var stored = await ReloadAsync(user.Id);
        Assert.Equal(OnboardingStatus.PendingFirstAccount, stored.OnboardingStatus);
        Assert.Equal("EUR", stored.DefaultCurrency);
    }

    [Fact]
    public async Task ConcurrentSetUpWithDifferentCurrencies_LoserDoesNotOverwriteTheWinner()
    {
        var user = await NewUserAsync();
        var currencies = new[] { "EUR", "USD" };

        var results = await RaceSetUpAsync(user.Id, currencies);

        Assert.Single(results, won => won);
        var winner = currencies[Array.IndexOf(results, true)];
        var loser = currencies[Array.IndexOf(results, false)];

        var stored = await ReloadAsync(user.Id);
        Assert.Equal(winner, stored.DefaultCurrency);
        Assert.Equal(OnboardingStatus.PendingFirstAccount, stored.OnboardingStatus);

        // Re-running the loser's request through the real handler is rejected, not applied.
        await using var scope = fixture.CreateScope();
        var outcome = await SetUpHandler(scope, Users(scope), Categories(scope))
            .HandleAsync(user.Id, new SetUpFinanceProfileCommand(loser), CancellationToken.None);
        Assert.Equal(OnboardingResultStatus.Invalid, outcome.Status);
        Assert.Equal(winner, (await ReloadAsync(user.Id)).DefaultCurrency);
    }

    [Fact]
    public async Task ConcurrentCompletions_ExactlyOneUpdateWins()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now);
        user.SetUpFinanceProfile("EUR", Now);
        await PostgresAssert.InsertAsync(fixture, user);

        await using var firstScope = fixture.CreateScope();
        await using var secondScope = fixture.CreateScope();
        var attempts = new List<(IUserRepository Repository, User User)>();

        foreach (var (scope, completedAt) in new[] { (firstScope, Now.AddMinutes(1)), (secondScope, Now.AddMinutes(2)) })
        {
            var repository = Users(scope);
            var loaded = (await repository.GetByIdAsync(user.Id, CancellationToken.None))!;
            loaded.CompleteOnboarding(completedAt);
            attempts.Add((repository, loaded));
        }

        var results = await Task.WhenAll(attempts.Select(attempt =>
            attempt.Repository.TryUpdateOnboardingAsync(attempt.User, OnboardingStatus.PendingFirstAccount, CancellationToken.None)));

        Assert.Single(results, won => won);
        var winner = attempts[Array.IndexOf(results, true)].User;

        var stored = await ReloadAsync(user.Id);
        Assert.Equal(OnboardingStatus.Completed, stored.OnboardingStatus);
        Assert.Equal(winner.OnboardingCompletedAtUtc, stored.OnboardingCompletedAtUtc);
        Assert.Equal("EUR", stored.DefaultCurrency);
    }

    // ---- Test 12: concurrent finance-profile setup against PostgreSQL ----

    [Fact]
    public async Task ConcurrentFinanceProfileSetUps_ProduceExactlyOneStarterTree()
    {
        var user = await NewUserAsync();
        var rendezvous = new Rendezvous(participants: 2);

        await using var firstScope = fixture.CreateScope();
        await using var secondScope = fixture.CreateScope();

        // Both handlers read the (empty) category set before either inserts, so both try to insert the
        // whole tree (parents and children in one batch): one save hits the real sibling-index violation
        // or loses a deadlock and must re-read/reconcile; one user update loses the conditional UPDATE
        // and must resolve to the winner's state.
        var results = await Task.WhenAll(new[] { firstScope, secondScope }.Select(scope =>
            SetUpHandler(scope, Users(scope), new PausingCategoryRepository(Categories(scope), rendezvous))
                .HandleAsync(user.Id, new SetUpFinanceProfileCommand("EUR"), CancellationToken.None)));

        Assert.All(results, result => Assert.Equal(OnboardingResultStatus.Ok, result.Status));

        AssertIsExactlyTheStarterTree(user.Id, await LoadCategoriesAsync(user.Id));

        var stored = await ReloadAsync(user.Id);
        Assert.Equal(OnboardingStatus.PendingFirstAccount, stored.OnboardingStatus);
        Assert.Equal("EUR", stored.DefaultCurrency);
    }

    [Fact]
    public async Task FinanceProfileSetUp_PersistsTheStarterTree()
    {
        var user = await NewUserAsync();

        await using (var scope = fixture.CreateScope())
        {
            var result = await SetUpHandler(scope, Users(scope), Categories(scope))
                .HandleAsync(user.Id, new SetUpFinanceProfileCommand("EUR"), CancellationToken.None);
            Assert.Equal(OnboardingResultStatus.Ok, result.Status);
        }

        AssertIsExactlyTheStarterTree(user.Id, await LoadCategoriesAsync(user.Id));
        Assert.Equal(OnboardingStatus.PendingFirstAccount, (await ReloadAsync(user.Id)).OnboardingStatus);
    }

    [Fact]
    public async Task FinanceProfileSetUp_CompletesAPartialTree_ReusingTheExistingParentAndKeepingCustomRows()
    {
        var user = await NewUserAsync();
        var foodAndDrink = Category.Create(user.Id, "food & drink", CategoryType.Expense, parent: null, Now);
        var shopping = Category.Create(user.Id, "Shopping", CategoryType.Expense, parent: null, Now);
        var groceriesUnderShopping = Category.Create(user.Id, "Groceries", CategoryType.Expense, shopping, Now);
        await PostgresAssert.InsertAsync(fixture, foodAndDrink, shopping, groceriesUnderShopping);

        await using (var scope = fixture.CreateScope())
        {
            var result = await SetUpHandler(scope, Users(scope), Categories(scope))
                .HandleAsync(user.Id, new SetUpFinanceProfileCommand("EUR"), CancellationToken.None);
            Assert.Equal(OnboardingResultStatus.Ok, result.Status);
        }

        var categories = await LoadCategoriesAsync(user.Id);

        // The custom child stays; everything else is exactly the tree, with both existing parents reused.
        Assert.Contains(categories, category => category.Id == groceriesUnderShopping.Id);
        var starterRows = categories.Where(category => category.Id != groceriesUnderShopping.Id).ToList();
        AssertIsExactlyTheStarterTree(user.Id, starterRows);
        Assert.Contains(starterRows, category => category.Id == foodAndDrink.Id);
        Assert.Contains(starterRows, category => category.Id == shopping.Id);
        Assert.Equal(3, starterRows.Count(category => category.ParentCategoryId == foodAndDrink.Id));
    }

    // The persisted rows are exactly the starter tree: 37 rows (8 Expense parents, 23 Expense
    // children, 6 Income top-level, no Income children), each child under the expected parent id, no
    // duplicate siblings, all owned by the user.
    private static void AssertIsExactlyTheStarterTree(Guid userId, IReadOnlyList<Category> categories)
    {
        Assert.Equal(37, categories.Count);
        Assert.All(categories, category => Assert.Equal(userId, category.UserId));

        var topLevel = categories.Where(category => category.ParentCategoryId is null).ToList();
        var children = categories.Where(category => category.ParentCategoryId is not null).ToList();
        Assert.Equal(8, topLevel.Count(category => category.CategoryType == CategoryType.Expense));
        Assert.Equal(6, topLevel.Count(category => category.CategoryType == CategoryType.Income));
        Assert.Equal(23, children.Count(category => category.CategoryType == CategoryType.Expense));
        Assert.DoesNotContain(children, category => category.CategoryType == CategoryType.Income);

        Assert.Equal(
            categories.Count,
            categories.Select(category => (category.CategoryType, category.ParentCategoryId, category.Name.ToLowerInvariant())).Distinct().Count());

        foreach (var starter in StarterCategories.All)
        {
            var parent = Assert.Single(topLevel, category =>
                category.CategoryType == starter.CategoryType
                && string.Equals(category.Name, starter.Name, StringComparison.OrdinalIgnoreCase));

            Assert.Equal(
                starter.Children.Order(StringComparer.OrdinalIgnoreCase),
                children.Where(child => child.ParentCategoryId == parent.Id).Select(child => child.Name).Order(StringComparer.OrdinalIgnoreCase));
        }
    }

    // Final persisted categories, always from a fresh scope.
    private async Task<IReadOnlyList<Category>> LoadCategoriesAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Categories
            .AsNoTracking()
            .Where(category => category.UserId == userId)
            .ToListAsync();
    }

    // Each participant loads the same PendingFinanceProfile user in its own scope, applies the Domain
    // transition, and then all run the conditional UPDATE concurrently.
    private async Task<bool[]> RaceSetUpAsync(Guid userId, params string[] currencies)
    {
        var scopes = currencies.Select(_ => fixture.CreateScope()).ToList();

        try
        {
            var attempts = new List<(IUserRepository Repository, User User)>();

            foreach (var (scope, currency) in scopes.Zip(currencies))
            {
                var repository = Users(scope);
                var loaded = (await repository.GetByIdAsync(userId, CancellationToken.None))!;
                loaded.SetUpFinanceProfile(currency, Now);
                attempts.Add((repository, loaded));
            }

            return await Task.WhenAll(attempts.Select(attempt =>
                attempt.Repository.TryUpdateOnboardingAsync(attempt.User, OnboardingStatus.PendingFinanceProfile, CancellationToken.None)));
        }
        finally
        {
            foreach (var scope in scopes)
            {
                await scope.DisposeAsync();
            }
        }
    }

    private static SetUpFinanceProfileHandler SetUpHandler(
        AsyncServiceScope scope,
        IUserRepository users,
        ICategoryRepository categories) =>
        new(users, categories, new FixedTimeProvider(Now));

    private static IUserRepository Users(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IUserRepository>();

    private static ICategoryRepository Categories(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<ICategoryRepository>();

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now);
        await PostgresAssert.InsertAsync(fixture, user);

        return user;
    }

    // Final persisted state, always from a fresh scope.
    private async Task<User> ReloadAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Users
            .AsNoTracking()
            .SingleAsync(user => user.Id == userId);
    }
}
