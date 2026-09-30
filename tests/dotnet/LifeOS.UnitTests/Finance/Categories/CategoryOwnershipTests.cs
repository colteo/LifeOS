using LifeOS.Application.Finance.Categories.CreateCategory;
using LifeOS.Application.Finance.Categories.GetCategories;
using LifeOS.Domain.Finance.Categories;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Categories;

public class CategoryOwnershipTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);

    private readonly InMemoryCategoryRepository _repository = new();
    private readonly CreateCategoryHandler _handler;

    public CategoryOwnershipTests()
    {
        _handler = new CreateCategoryHandler(_repository, new FixedTimeProvider(UtcNow));
    }

    [Fact]
    public async Task CreateCategory_IsOwnedByTheCaller()
    {
        var result = await CreateAsync(TestUsers.B, "Casa", parentCategoryId: null);

        Assert.Equal(CreateCategoryStatus.Created, result.Status);
        Assert.Equal(TestUsers.B, Assert.Single(_repository.Categories).UserId);
    }

    [Fact]
    public async Task GetCategories_ReturnsOnlyTheCallersCategories()
    {
        var ofA = AddCategory(TestUsers.A, "Casa");
        AddCategory(TestUsers.B, "Auto");

        var categories = await new GetCategoriesHandler(_repository).HandleAsync(TestUsers.A, CancellationToken.None);

        Assert.Equal(ofA.Id, Assert.Single(categories).Id);
    }

    [Fact]
    public async Task TwoUsers_MayEachCreateTheSameName()
    {
        var ofA = await CreateAsync(TestUsers.A, "Casa", parentCategoryId: null);
        var ofB = await CreateAsync(TestUsers.B, "Casa", parentCategoryId: null);

        Assert.Equal(CreateCategoryStatus.Created, ofA.Status);
        Assert.Equal(CreateCategoryStatus.Created, ofB.Status);
        Assert.Equal(2, _repository.Categories.Count);
    }

    [Fact]
    public async Task DuplicateName_IsStillRejectedWithinTheSameUser()
    {
        await CreateAsync(TestUsers.A, "Casa", parentCategoryId: null);

        var duplicate = await CreateAsync(TestUsers.A, "casa", parentCategoryId: null);

        Assert.Equal(CreateCategoryStatus.DuplicateName, duplicate.Status);
    }

    [Fact]
    public async Task CreateCategory_WithAnotherUsersParent_ReturnsParentNotFound()
    {
        var parentOfA = AddCategory(TestUsers.A, "Auto");

        var result = await CreateAsync(TestUsers.B, "Benzina", parentOfA.Id);

        Assert.Equal(CreateCategoryStatus.ParentNotFound, result.Status);
        Assert.Single(_repository.Categories);
    }

    [Fact]
    public async Task CreateCategory_WithOwnParent_Succeeds()
    {
        var parentOfB = AddCategory(TestUsers.B, "Auto");

        var result = await CreateAsync(TestUsers.B, "Benzina", parentOfB.Id);

        Assert.Equal(CreateCategoryStatus.Created, result.Status);
        Assert.Equal(parentOfB.Id, result.Category!.ParentCategoryId);
    }

    private Task<CreateCategoryResult> CreateAsync(Guid userId, string name, Guid? parentCategoryId) =>
        _handler.HandleAsync(userId, new CreateCategoryCommand(name, CategoryType.Expense, parentCategoryId), CancellationToken.None);

    private Category AddCategory(Guid userId, string name)
    {
        var category = Category.Create(userId, name, CategoryType.Expense, parent: null, UtcNow);
        _repository.Categories.Add(category);

        return category;
    }
}
