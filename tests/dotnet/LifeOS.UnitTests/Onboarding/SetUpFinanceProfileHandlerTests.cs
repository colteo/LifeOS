using LifeOS.Application.Finance.Categories;
using LifeOS.Application.Onboarding;
using LifeOS.Application.Onboarding.SetUpFinanceProfile;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Users;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Onboarding;

public class SetUpFinanceProfileHandlerTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset UtcNow = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);

    private readonly InMemoryUserRepository _users = new();
    private readonly InMemoryCategoryRepository _categories = new();
    private readonly User _user;

    public SetUpFinanceProfileHandlerTests()
    {
        _user = AddUser();
    }

    [Fact]
    public async Task HandleAsync_CreatesTheWholeStarterSetForTheUser()
    {
        var result = await SetUpAsync(_user.Id, "EUR");

        Assert.Equal(OnboardingResultStatus.Ok, result.Status);
        var created = _categories.Categories;
        Assert.Equal(15, created.Count);
        Assert.All(created, category =>
        {
            Assert.Equal(_user.Id, category.UserId);
            Assert.Null(category.ParentCategoryId);
            Assert.Equal(7, category.Id.Version);
            Assert.Equal(UtcNow, category.CreatedAtUtc);
        });
        Assert.Equal(15, created.Select(category => category.Id).Distinct().Count());
        Assert.Equal(
            StarterCategories.All.OrderBy(starter => starter.CategoryType).ThenBy(starter => starter.Name),
            created.Select(category => new StarterCategory(category.Name, category.CategoryType))
                .OrderBy(starter => starter.CategoryType).ThenBy(starter => starter.Name));
    }

    [Fact]
    public async Task HandleAsync_CreatesBothAltroCategories()
    {
        await SetUpAsync(_user.Id, "EUR");

        Assert.Contains(_categories.Categories, category => category is { Name: "Altro", CategoryType: CategoryType.Expense });
        Assert.Contains(_categories.Categories, category => category is { Name: "Altro", CategoryType: CategoryType.Income });
    }

    [Fact]
    public async Task HandleAsync_UpdatesTheUserAndReturnsTheNewState()
    {
        var result = await SetUpAsync(_user.Id, " eur ");

        Assert.Equal(OnboardingStatus.PendingFirstAccount, result.User!.OnboardingStatus);
        Assert.Equal("EUR", result.User.DefaultCurrency);
        var stored = _users.Stored(_user.Id);
        Assert.Equal(OnboardingStatus.PendingFirstAccount, stored.OnboardingStatus);
        Assert.Equal("EUR", stored.DefaultCurrency);
        Assert.Equal(UtcNow, stored.StarterCategoriesInitializedAtUtc);
    }

    [Fact]
    public async Task HandleAsync_ExistingCustomCategory_DoesNotSuppressTheStarterSet()
    {
        AddCategory(_user.Id, "Bar", CategoryType.Expense);

        await SetUpAsync(_user.Id, "EUR");

        Assert.Equal(16, _categories.Categories.Count);
        Assert.Contains(_categories.Categories, category => category.Name == "Bar");
        Assert.Contains(_categories.Categories, category => category.Name == "Casa");
    }

    [Fact]
    public async Task HandleAsync_ExistingCasaIgnoringCase_IsNotDuplicated()
    {
        var casa = AddCategory(_user.Id, "casa", CategoryType.Expense);

        await SetUpAsync(_user.Id, "EUR");

        Assert.Equal(15, _categories.Categories.Count);
        var casas = _categories.Categories
            .Where(category => string.Equals(category.Name, "Casa", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal(casa.Id, Assert.Single(casas).Id);
    }

    [Fact]
    public async Task HandleAsync_AnotherUsersCategories_HaveNoEffect()
    {
        var other = AddUser();
        AddCategory(other.Id, "Casa", CategoryType.Expense);

        await SetUpAsync(_user.Id, "EUR");

        Assert.Equal(15, _categories.Categories.Count(category => category.UserId == _user.Id));
        Assert.Single(_categories.Categories, category => category.UserId == other.Id);
    }

    [Fact]
    public async Task HandleAsync_TwoUsers_GetIndependentRows()
    {
        var other = AddUser();

        await SetUpAsync(_user.Id, "EUR");
        await SetUpAsync(other.Id, "EUR");

        var ofUser = _categories.Categories.Where(category => category.UserId == _user.Id).Select(category => category.Id);
        var ofOther = _categories.Categories.Where(category => category.UserId == other.Id).Select(category => category.Id);
        Assert.Equal(15, ofUser.Count());
        Assert.Equal(15, ofOther.Count());
        Assert.Empty(ofUser.Intersect(ofOther));
    }

    [Fact]
    public async Task HandleAsync_RetryWithSameCurrency_SucceedsWithoutWriting()
    {
        await SetUpAsync(_user.Id, "EUR");
        var ids = _categories.Categories.Select(category => category.Id).ToList();

        var retry = await SetUpAsync(_user.Id, "eur", UtcNow.AddMinutes(5));

        Assert.Equal(OnboardingResultStatus.Ok, retry.Status);
        Assert.Equal(ids, _categories.Categories.Select(category => category.Id));
        Assert.Equal(1, _users.OnboardingUpdates);
        Assert.Equal(UtcNow, _users.Stored(_user.Id).StarterCategoriesInitializedAtUtc);
    }

    [Fact]
    public async Task HandleAsync_AfterCompletion_SameCurrencySucceeds()
    {
        await SetUpAsync(_user.Id, "EUR");
        CompleteStored(_user.Id);

        var retry = await SetUpAsync(_user.Id, "EUR");

        Assert.Equal(OnboardingResultStatus.Ok, retry.Status);
        Assert.Equal(OnboardingStatus.Completed, retry.User!.OnboardingStatus);
    }

    [Fact]
    public async Task HandleAsync_RetryWithDifferentCurrency_IsRejected()
    {
        await SetUpAsync(_user.Id, "EUR");

        var retry = await SetUpAsync(_user.Id, "USD");

        Assert.Equal(OnboardingResultStatus.Invalid, retry.Status);
        Assert.Equal("defaultCurrency", retry.Field);
        Assert.Equal("EUR", _users.Stored(_user.Id).DefaultCurrency);
    }

    [Fact]
    public async Task HandleAsync_WithInvalidCurrency_ThrowsAndWritesNothing()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => SetUpAsync(_user.Id, "EU"));

        Assert.Empty(_categories.Categories);
        Assert.Equal(OnboardingStatus.PendingFinanceProfile, _users.Stored(_user.Id).OnboardingStatus);
    }

    [Fact]
    public async Task HandleAsync_WithUnknownUser_ReturnsNotFound()
    {
        var result = await SetUpAsync(Guid.CreateVersion7(), "EUR");

        Assert.Equal(OnboardingResultStatus.NotFound, result.Status);
        Assert.Empty(_categories.Categories);
    }

    [Fact]
    public async Task HandleAsync_AfterPartialFailure_RetryCompletesWithoutDuplicates()
    {
        // A previous attempt persisted the starter categories but not the user update.
        foreach (var starter in StarterCategories.All)
        {
            AddCategory(_user.Id, starter.Name, starter.CategoryType);
        }

        var result = await SetUpAsync(_user.Id, "EUR");

        Assert.Equal(OnboardingResultStatus.Ok, result.Status);
        Assert.Equal(15, _categories.Categories.Count);
        Assert.Equal(0, _categories.AddAttempts);
        Assert.Equal(OnboardingStatus.PendingFirstAccount, _users.Stored(_user.Id).OnboardingStatus);
    }

    [Fact]
    public async Task HandleAsync_ConcurrentStarterInsert_ReconcilesOnceWithoutDuplicates()
    {
        // Another request inserts "Casa" between our read and our save.
        _categories.BeforeAdd = () =>
        {
            _categories.BeforeAdd = null;
            _categories.Categories.Add(Category.Create(_user.Id, "Casa", CategoryType.Expense, parent: null, UtcNow));
        };

        var result = await SetUpAsync(_user.Id, "EUR");

        Assert.Equal(OnboardingResultStatus.Ok, result.Status);
        Assert.Equal(2, _categories.AddAttempts);
        Assert.Equal(15, _categories.Categories.Count);
    }

    [Fact]
    public async Task HandleAsync_SecondConcurrentConflict_ReturnsConflictWithoutUpdatingUser()
    {
        // Every save conflicts: the handler reconciles once and then gives up.
        _categories.BeforeAdd = () =>
            _categories.Categories.Add(Category.Create(_user.Id, "Casa", CategoryType.Expense, parent: null, UtcNow));

        var result = await SetUpAsync(_user.Id, "EUR");

        Assert.Equal(OnboardingResultStatus.Conflict, result.Status);
        Assert.Equal(2, _categories.AddAttempts);
        Assert.Equal(OnboardingStatus.PendingFinanceProfile, _users.Stored(_user.Id).OnboardingStatus);
    }

    [Fact]
    public async Task HandleAsync_ConcurrentSetupWithSameCurrency_Succeeds()
    {
        _users.BeforeUpdateOnboarding = () =>
        {
            _users.BeforeUpdateOnboarding = null;
            _users.Stored(_user.Id).SetUpFinanceProfile("EUR", UtcNow);
        };

        var result = await SetUpAsync(_user.Id, "EUR");

        Assert.Equal(OnboardingResultStatus.Ok, result.Status);
        Assert.Equal(OnboardingStatus.PendingFirstAccount, result.User!.OnboardingStatus);
    }

    [Fact]
    public async Task HandleAsync_ConcurrentSetupWithDifferentCurrency_IsRejected()
    {
        _users.BeforeUpdateOnboarding = () =>
        {
            _users.BeforeUpdateOnboarding = null;
            _users.Stored(_user.Id).SetUpFinanceProfile("USD", UtcNow);
        };

        var result = await SetUpAsync(_user.Id, "EUR");

        Assert.Equal(OnboardingResultStatus.Invalid, result.Status);
        Assert.Equal("USD", _users.Stored(_user.Id).DefaultCurrency);
    }

    private Task<OnboardingResult> SetUpAsync(Guid userId, string currency, DateTimeOffset? now = null) =>
        new SetUpFinanceProfileHandler(_users, _categories, new FixedTimeProvider(now ?? UtcNow))
            .HandleAsync(userId, new SetUpFinanceProfileCommand(currency), CancellationToken.None);

    private User AddUser()
    {
        var user = User.CreateFromExternalIdentity(null, null, CreatedAtUtc);
        _users.Users.Add(user);

        return user;
    }

    private Category AddCategory(Guid userId, string name, CategoryType categoryType)
    {
        var category = Category.Create(userId, name, categoryType, parent: null, CreatedAtUtc);
        _categories.Categories.Add(category);

        return category;
    }

    private void CompleteStored(Guid userId) => _users.Stored(userId).CompleteOnboarding(UtcNow);
}
