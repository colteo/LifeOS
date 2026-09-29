using LifeOS.Application.Finance.Categories.CreateCategory;
using LifeOS.Domain.Finance.Categories;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Categories;

public class CreateCategoryHandlerTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 9, 29, 10, 30, 0, TimeSpan.Zero);

    private readonly InMemoryCategoryRepository _repository = new();
    private readonly CreateCategoryHandler _handler;

    public CreateCategoryHandlerTests()
    {
        _handler = new CreateCategoryHandler(_repository, new FixedTimeProvider(UtcNow));
    }

    [Fact]
    public async Task HandleAsync_TopLevel_CreatesAndPersistsCategory()
    {
        var result = await _handler.HandleAsync(
            new CreateCategoryCommand("  Auto ", CategoryType.Expense, null),
            CancellationToken.None);

        Assert.Equal(CreateCategoryStatus.Created, result.Status);
        var persisted = Assert.Single(_repository.Categories);
        Assert.Equal(persisted.Id, result.Category!.Id);
        Assert.Equal("Auto", result.Category.Name);
        Assert.Equal(CategoryType.Expense, result.Category.CategoryType);
        Assert.Null(result.Category.ParentCategoryId);
    }

    [Fact]
    public async Task HandleAsync_WithExistingParent_CreatesSubcategory()
    {
        var parent = AddExisting("Auto", CategoryType.Expense);

        var result = await _handler.HandleAsync(
            new CreateCategoryCommand("Benzina", CategoryType.Expense, parent.Id),
            CancellationToken.None);

        Assert.Equal(CreateCategoryStatus.Created, result.Status);
        Assert.Equal(parent.Id, result.Category!.ParentCategoryId);
        Assert.Equal(2, _repository.Categories.Count);
    }

    [Fact]
    public async Task HandleAsync_UsesCurrentTimeFromTimeProvider()
    {
        var result = await _handler.HandleAsync(
            new CreateCategoryCommand("Auto", CategoryType.Expense, null),
            CancellationToken.None);

        Assert.Equal(UtcNow, result.Category!.CreatedAtUtc);
        Assert.Equal(UtcNow, _repository.Categories.Single().CreatedAtUtc);
    }

    [Fact]
    public async Task HandleAsync_WithMissingParent_ReturnsParentNotFoundAndDoesNotPersist()
    {
        var result = await _handler.HandleAsync(
            new CreateCategoryCommand("Benzina", CategoryType.Expense, Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.Equal(CreateCategoryStatus.ParentNotFound, result.Status);
        Assert.Null(result.Category);
        Assert.Empty(_repository.Categories);
    }

    [Fact]
    public async Task HandleAsync_WithParentOfDifferentType_ThrowsAndDoesNotPersist()
    {
        var incomeParent = AddExisting("Stipendio", CategoryType.Income);

        await Assert.ThrowsAsync<ArgumentException>(() => _handler.HandleAsync(
            new CreateCategoryCommand("Benzina", CategoryType.Expense, incomeParent.Id),
            CancellationToken.None));

        Assert.Single(_repository.Categories);
    }

    [Fact]
    public async Task HandleAsync_WithSubcategoryAsParent_ThrowsAndDoesNotPersist()
    {
        var parent = AddExisting("Auto", CategoryType.Expense);
        var child = AddExisting("Benzina", CategoryType.Expense, parent);

        await Assert.ThrowsAsync<ArgumentException>(() => _handler.HandleAsync(
            new CreateCategoryCommand("Diesel", CategoryType.Expense, child.Id),
            CancellationToken.None));

        Assert.Equal(2, _repository.Categories.Count);
    }

    [Theory]
    [InlineData("Auto")]
    [InlineData("auto")]
    [InlineData("  AUTO  ")]
    public async Task HandleAsync_WithDuplicateTopLevelName_ReturnsDuplicateNameAndDoesNotPersist(string name)
    {
        AddExisting("Auto", CategoryType.Expense);

        var result = await _handler.HandleAsync(
            new CreateCategoryCommand(name, CategoryType.Expense, null),
            CancellationToken.None);

        Assert.Equal(CreateCategoryStatus.DuplicateName, result.Status);
        Assert.Single(_repository.Categories);
    }

    [Fact]
    public async Task HandleAsync_WithDuplicateSubcategoryName_ReturnsDuplicateName()
    {
        var parent = AddExisting("Auto", CategoryType.Expense);
        AddExisting("Benzina", CategoryType.Expense, parent);

        var result = await _handler.HandleAsync(
            new CreateCategoryCommand("BENZINA", CategoryType.Expense, parent.Id),
            CancellationToken.None);

        Assert.Equal(CreateCategoryStatus.DuplicateName, result.Status);
        Assert.Equal(2, _repository.Categories.Count);
    }

    [Fact]
    public async Task HandleAsync_SameNameWithDifferentType_IsAllowed()
    {
        AddExisting("Altro", CategoryType.Expense);

        var result = await _handler.HandleAsync(
            new CreateCategoryCommand("Altro", CategoryType.Income, null),
            CancellationToken.None);

        Assert.Equal(CreateCategoryStatus.Created, result.Status);
    }

    [Fact]
    public async Task HandleAsync_SameNameUnderDifferentParent_IsAllowed()
    {
        var auto = AddExisting("Auto", CategoryType.Expense);
        var casa = AddExisting("Casa", CategoryType.Expense);
        AddExisting("Manutenzione", CategoryType.Expense, auto);

        var result = await _handler.HandleAsync(
            new CreateCategoryCommand("Manutenzione", CategoryType.Expense, casa.Id),
            CancellationToken.None);

        Assert.Equal(CreateCategoryStatus.Created, result.Status);
    }

    [Fact]
    public async Task HandleAsync_TopLevelNameMatchingASubcategory_IsAllowed()
    {
        var auto = AddExisting("Auto", CategoryType.Expense);
        AddExisting("Benzina", CategoryType.Expense, auto);

        var result = await _handler.HandleAsync(
            new CreateCategoryCommand("Benzina", CategoryType.Expense, null),
            CancellationToken.None);

        Assert.Equal(CreateCategoryStatus.Created, result.Status);
    }

    private Category AddExisting(string name, CategoryType categoryType, Category? parent = null)
    {
        var category = Category.Create(name, categoryType, parent, UtcNow);
        _repository.Categories.Add(category);

        return category;
    }
}
