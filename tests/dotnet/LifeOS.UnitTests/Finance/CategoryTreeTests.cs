using LifeOS.App.Services.Finance;
using LifeOS.Contracts.Finance.Categories;

namespace LifeOS.UnitTests.Finance;

public class CategoryTreeTests
{
    private static readonly DateTimeOffset Created = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sections_SeparateExpenseAndIncome()
    {
        var casa = Category("Casa", "Expense");
        var stipendio = Category("Stipendio", "Income");

        Assert.Equal([casa], CategoryTree.Build([casa, stipendio], "Expense").TopLevel.Select(node => node.Category));
        Assert.Equal([stipendio], CategoryTree.Build([casa, stipendio], "Income").TopLevel.Select(node => node.Category));
    }

    [Fact]
    public void Children_AreGroupedUnderTheirParentById()
    {
        var auto = Category("Auto", "Expense");
        var viaggi = Category("Viaggi", "Expense");
        var benzina = Category("Benzina", "Expense", auto);
        // The same child name under two parents: grouping is by id, never by name.
        var altroAuto = Category("Altro", "Expense", auto);
        var altroViaggi = Category("Altro", "Expense", viaggi);

        var section = CategoryTree.Build([altroViaggi, benzina, viaggi, altroAuto, auto], "Expense");

        var autoNode = section.TopLevel.Single(node => node.Category.Id == auto.Id);
        var viaggiNode = section.TopLevel.Single(node => node.Category.Id == viaggi.Id);
        Assert.Equal([altroAuto.Id, benzina.Id], autoNode.Children.Select(child => child.Id));
        Assert.Equal([altroViaggi.Id], viaggiNode.Children.Select(child => child.Id));
        Assert.Empty(section.Unlinked);
    }

    [Fact]
    public void EachLevel_IsSortedCaseInsensitivelyThenById_RegardlessOfInputOrder()
    {
        var tie1 = Category("casa", "Expense");
        var tie2 = Category("Casa", "Expense");
        var (first, second) = tie1.Id.CompareTo(tie2.Id) < 0 ? (tie1, tie2) : (tie2, tie1);
        var auto = Category("auto", "Expense");
        var zeta = Category("Zeta", "Expense");
        var altro = Category("Altro", "Expense");

        var section = CategoryTree.Build([zeta, second, auto, first, altro], "Expense");

        Assert.Equal(
            [altro.Id, auto.Id, first.Id, second.Id, zeta.Id],
            section.TopLevel.Select(node => node.Category.Id));
    }

    [Fact]
    public void AChildOfAChild_IsNotAThirdLevel_ButUnlinked()
    {
        var auto = Category("Auto", "Expense");
        var benzina = Category("Benzina", "Expense", auto);
        var grandchild = Category("Diesel", "Expense", benzina);

        var section = CategoryTree.Build([auto, benzina, grandchild], "Expense");

        Assert.Equal([benzina.Id], Assert.Single(section.TopLevel).Children.Select(child => child.Id));
        Assert.Equal([grandchild.Id], section.Unlinked.Select(category => category.Id));
    }

    [Fact]
    public void ChildWithAMissingParent_IsUnlinked()
    {
        var orphan = new CategoryResponse(Guid.NewGuid(), "Orfana", "Expense", Guid.NewGuid(), Created);

        var section = CategoryTree.Build([orphan], "Expense");

        Assert.Empty(section.TopLevel);
        Assert.Equal([orphan.Id], section.Unlinked.Select(category => category.Id));
    }

    [Fact]
    public void ChildWhoseParentHasTheOtherType_IsUnlinked()
    {
        var income = Category("Stipendio", "Income");
        var mismatched = new CategoryResponse(Guid.NewGuid(), "Strana", "Expense", income.Id, Created);

        var section = CategoryTree.Build([income, mismatched], "Expense");

        Assert.Empty(section.TopLevel);
        Assert.Equal([mismatched.Id], section.Unlinked.Select(category => category.Id));
    }

    [Fact]
    public void NoCategories_GiveAnEmptySection()
    {
        var section = CategoryTree.Build([], "Income");

        Assert.Empty(section.TopLevel);
        Assert.Empty(section.Unlinked);
    }

    private static CategoryResponse Category(string name, string type, CategoryResponse? parent = null) =>
        new(Guid.NewGuid(), name, type, parent?.Id, Created);
}
