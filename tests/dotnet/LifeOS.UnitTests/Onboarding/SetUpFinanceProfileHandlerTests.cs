using LifeOS.Application.Finance.Categories;
using LifeOS.Application.Onboarding;
using LifeOS.Application.Onboarding.SetUpFinanceProfile;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Users;
using LifeOS.UnitTests.Fakes;
using LifeOS.UnitTests.Finance.Categories;

namespace LifeOS.UnitTests.Onboarding;

public class SetUpFinanceProfileHandlerTests
{
    private const int StarterTreeSize = 37;

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
    public async Task HandleAsync_CreatesTheWholeStarterTreeForTheUser()
    {
        var result = await SetUpAsync(_user.Id, "EUR");

        Assert.Equal(OnboardingResultStatus.Ok, result.Status);
        var created = _categories.Categories;
        Assert.Equal(StarterTreeSize, created.Count);
        Assert.All(created, category =>
        {
            Assert.Equal(_user.Id, category.UserId);
            Assert.Equal(7, category.Id.Version);
            Assert.Equal(UtcNow, category.CreatedAtUtc);
        });
        Assert.Equal(StarterTreeSize, created.Select(category => category.Id).Distinct().Count());

        // Every parent id is a persisted top-level category of the same user and type.
        Assert.All(created.Where(category => category.ParentCategoryId is not null), child =>
        {
            var parent = Assert.Single(created, candidate => candidate.Id == child.ParentCategoryId);
            Assert.Equal(_user.Id, parent.UserId);
            Assert.Equal(child.CategoryType, parent.CategoryType);
            Assert.Null(parent.ParentCategoryId);
        });
        Assert.Equal(StarterCategoriesTests.ExpectedTree(), StarterCategoriesTests.Tree(created));
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
    public async Task HandleAsync_ExistingCustomCategory_DoesNotSuppressTheStarterTree()
    {
        var pets = AddCategory(_user.Id, "Pets", CategoryType.Expense);

        await SetUpAsync(_user.Id, "EUR");

        Assert.Equal(StarterTreeSize + 1, _categories.Categories.Count);
        Assert.Contains(_categories.Categories, category => category.Id == pets.Id && category.Name == "Pets");
        Assert.Contains(_categories.Categories, category => category.Name == "Food & Drink");
        Assert.DoesNotContain(_categories.Categories, category => category.ParentCategoryId == pets.Id);
    }

    [Fact]
    public async Task HandleAsync_ExistingParentIgnoringCase_IsReusedForItsChildren()
    {
        var foodAndDrink = AddCategory(_user.Id, "food & drink", CategoryType.Expense);

        await SetUpAsync(_user.Id, "EUR");

        Assert.Equal(StarterTreeSize, _categories.Categories.Count);
        Assert.Equal(
            foodAndDrink.Id,
            Assert.Single(_categories.Categories, category => SameName(category.Name, "Food & Drink")).Id);
        Assert.Equal(
            ["Bars & cafes", "Eating out", "Groceries"],
            _categories.Categories.Where(category => category.ParentCategoryId == foodAndDrink.Id).Select(category => category.Name).Order());
    }

    [Fact]
    public async Task HandleAsync_SameChildNameUnderAnotherParent_DoesNotSuppressTheStarterChild()
    {
        var shopping = AddCategory(_user.Id, "Shopping", CategoryType.Expense);
        var groceriesUnderShopping = AddCategory(_user.Id, "Groceries", CategoryType.Expense, shopping);

        await SetUpAsync(_user.Id, "EUR");

        // Shopping is reused; the custom Groceries under it stays and does not count.
        Assert.Equal(StarterTreeSize + 1, _categories.Categories.Count);
        Assert.Contains(_categories.Categories, category => category.Id == groceriesUnderShopping.Id);
        var foodAndDrink = Assert.Single(_categories.Categories, category => category.Name == "Food & Drink");
        Assert.Single(_categories.Categories, category => category.Name == "Groceries" && category.ParentCategoryId == foodAndDrink.Id);
    }

    [Fact]
    public async Task HandleAsync_AnotherUsersCategories_HaveNoEffect()
    {
        var other = AddUser();
        AddCategory(other.Id, "Food & Drink", CategoryType.Expense);

        await SetUpAsync(_user.Id, "EUR");

        Assert.Equal(StarterTreeSize, _categories.Categories.Count(category => category.UserId == _user.Id));
        Assert.Single(_categories.Categories, category => category.UserId == other.Id);
    }

    [Fact]
    public async Task HandleAsync_TwoUsers_GetIndependentRows()
    {
        var other = AddUser();

        await SetUpAsync(_user.Id, "EUR");
        await SetUpAsync(other.Id, "EUR");

        var ofUser = _categories.Categories.Where(category => category.UserId == _user.Id).ToList();
        var ofOther = _categories.Categories.Where(category => category.UserId == other.Id).ToList();
        Assert.Equal(StarterTreeSize, ofUser.Count);
        Assert.Equal(StarterTreeSize, ofOther.Count);
        Assert.Empty(ofUser.Select(category => category.Id).Intersect(ofOther.Select(category => category.Id)));
        Assert.All(ofOther.Where(category => category.ParentCategoryId is not null), child =>
            Assert.Contains(ofOther, parent => parent.Id == child.ParentCategoryId));
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
    public async Task HandleAsync_AfterPartialFailure_RetryCompletesWithoutWritingCategories()
    {
        // A previous attempt persisted the whole starter tree but not the user update.
        _categories.Categories.AddRange(StarterCategories.CreateMissing(_user.Id, [], CreatedAtUtc));

        var result = await SetUpAsync(_user.Id, "EUR");

        Assert.Equal(OnboardingResultStatus.Ok, result.Status);
        Assert.Equal(StarterTreeSize, _categories.Categories.Count);
        Assert.Equal(0, _categories.AddAttempts);
        Assert.Equal(OnboardingStatus.PendingFirstAccount, _users.Stored(_user.Id).OnboardingStatus);
    }

    [Fact]
    public async Task HandleAsync_PartialTree_IsCompletedUnderTheExistingParents()
    {
        // Only the parents exist (e.g. created by the user): the children are added under them.
        var parents = StarterCategories.All
            .Select(starter => AddCategory(_user.Id, starter.Name, starter.CategoryType))
            .ToList();

        var result = await SetUpAsync(_user.Id, "EUR");

        Assert.Equal(OnboardingResultStatus.Ok, result.Status);
        Assert.Equal(1, _categories.AddAttempts);
        Assert.Equal(StarterTreeSize, _categories.Categories.Count);
        Assert.All(_categories.Categories.Where(category => category.ParentCategoryId is not null), child =>
            Assert.Contains(parents, parent => parent.Id == child.ParentCategoryId));
        Assert.Equal(StarterCategoriesTests.ExpectedTree(), StarterCategoriesTests.Tree(_categories.Categories));
    }

    [Fact]
    public async Task HandleAsync_ConcurrentStarterInsert_ReconcilesOnceWithoutDuplicates()
    {
        // Another request inserts "Food & Drink" between our read and our save.
        Category? concurrent = null;
        _categories.BeforeAdd = () =>
        {
            _categories.BeforeAdd = null;
            concurrent = Category.Create(_user.Id, "Food & Drink", CategoryType.Expense, parent: null, UtcNow);
            _categories.Categories.Add(concurrent);
        };

        var result = await SetUpAsync(_user.Id, "EUR");

        Assert.Equal(OnboardingResultStatus.Ok, result.Status);
        Assert.Equal(2, _categories.AddAttempts);
        Assert.Equal(StarterTreeSize, _categories.Categories.Count);
        Assert.Equal(3, _categories.Categories.Count(category => category.ParentCategoryId == concurrent!.Id));
    }

    [Fact]
    public async Task HandleAsync_SecondConcurrentConflict_ReturnsConflictWithoutUpdatingUser()
    {
        // Every save conflicts: the handler reconciles once and then gives up.
        _categories.BeforeAdd = InsertFirstMissingStarterCategory;

        var result = await SetUpAsync(_user.Id, "EUR");

        Assert.Equal(OnboardingResultStatus.Conflict, result.Status);
        Assert.Equal(2, _categories.AddAttempts);
        Assert.Equal(OnboardingStatus.PendingFinanceProfile, _users.Stored(_user.Id).OnboardingStatus);
    }

    [Fact]
    public async Task HandleAsync_OnlyParentsExistAndBothWritesFail_DoesNotAdvanceTheUser()
    {
        // A partial tree (all parents, children still missing) never completes the step.
        foreach (var starter in StarterCategories.All)
        {
            AddCategory(_user.Id, starter.Name, starter.CategoryType);
        }

        _categories.BeforeAdd = InsertFirstMissingStarterCategory;

        var result = await SetUpAsync(_user.Id, "EUR");

        Assert.Equal(OnboardingResultStatus.Conflict, result.Status);
        Assert.Equal(StarterCategories.All.Count + 2, _categories.Categories.Count);
        Assert.False(StarterCategories.IsComplete(_user.Id, _categories.Categories));
        Assert.Equal(OnboardingStatus.PendingFinanceProfile, _users.Stored(_user.Id).OnboardingStatus);
        Assert.Equal(0, _users.OnboardingUpdates);
    }

    [Fact]
    public async Task HandleAsync_BothWritesFailButFinalReReadFindsCompleteTree_Succeeds()
    {
        // A concurrent setup commits "Food & Drink" before our first save and the rest of the tree
        // before our retry (e.g. after we lost a deadlock and re-read too early). Both our writes
        // fail, but the final re-read finds nothing missing.
        var calls = 0;
        _categories.BeforeAdd = () =>
        {
            calls++;

            if (calls == 1)
            {
                _categories.Categories.Add(Category.Create(_user.Id, "Food & Drink", CategoryType.Expense, parent: null, UtcNow));
            }
            else
            {
                _categories.Categories.AddRange(StarterCategories.CreateMissing(_user.Id, _categories.Categories, UtcNow));
            }
        };

        var result = await SetUpAsync(_user.Id, "EUR");

        Assert.Equal(OnboardingResultStatus.Ok, result.Status);
        Assert.Equal(2, _categories.AddAttempts);
        Assert.Equal(StarterTreeSize, _categories.Categories.Count);
        Assert.Equal(OnboardingStatus.PendingFirstAccount, _users.Stored(_user.Id).OnboardingStatus);
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

    // Simulates a concurrent request committing the first starter category this handler is about to
    // insert (a missing parent, or a missing child of an existing parent), so the save conflicts.
    private void InsertFirstMissingStarterCategory() =>
        _categories.Categories.Add(StarterCategories.CreateMissing(_user.Id, _categories.Categories, UtcNow)[0]);

    private Task<OnboardingResult> SetUpAsync(Guid userId, string currency, DateTimeOffset? now = null) =>
        new SetUpFinanceProfileHandler(_users, _categories, new FixedTimeProvider(now ?? UtcNow))
            .HandleAsync(userId, new SetUpFinanceProfileCommand(currency), CancellationToken.None);

    private User AddUser()
    {
        var user = User.CreateFromExternalIdentity(null, null, CreatedAtUtc);
        _users.Users.Add(user);

        return user;
    }

    private Category AddCategory(Guid userId, string name, CategoryType categoryType, Category? parent = null)
    {
        var category = Category.Create(userId, name, categoryType, parent, CreatedAtUtc);
        _categories.Categories.Add(category);

        return category;
    }

    private static bool SameName(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private void CompleteStored(Guid userId) => _users.Stored(userId).CompleteOnboarding(UtcNow);
}
