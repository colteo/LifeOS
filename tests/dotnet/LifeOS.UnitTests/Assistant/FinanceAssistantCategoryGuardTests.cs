using LifeOS.Application.Finance.Assistant;
using LifeOS.Domain.Assistant;
using LifeOS.Domain.Finance.Assistant;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;
using FinanceOperation = LifeOS.Domain.Assistant.AssistantOperation<LifeOS.Domain.Finance.Assistant.FinanceTransactionDraft>;

namespace LifeOS.UnitTests.Assistant;

// AI-007 F3-B2: a category the model marked as stated (the user's choice) after live run 1, where gpt-6-luna returned
// descriptions as categories ("di visita medica", "di rimborso spese"). The user's own words decide: an exact name is
// theirs, part of a name only with the word "categoria", anything else is asked. Never applied as a guess, never turned
// into a suggestion or a note.
public class FinanceAssistantCategoryGuardTests
{
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Created = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // The synthetic lists of assistant_finance v1, plus names a real list may hold.
    private static readonly Category Ristoranti = NewCategory("Ristoranti", CategoryType.Expense);
    private static readonly Category Spesa = NewCategory("Spesa", CategoryType.Expense);
    private static readonly Category Salute = NewCategory("Salute", CategoryType.Expense);
    private static readonly Category ViaggiLavoro = NewCategory("Viaggi lavoro", CategoryType.Expense);
    private static readonly Category ViaggiPrivati = NewCategory("Viaggi privati", CategoryType.Expense);
    private static readonly Category Rimborsi = NewCategory("Rimborsi", CategoryType.Income);
    private static readonly Category RimborsoSpeseViaggio = NewCategory("Rimborso spese viaggio", CategoryType.Income);
    private static readonly Category[] All = [Ristoranti, Spesa, Salute, ViaggiLavoro, ViaggiPrivati, Rimborsi, RimborsoSpeseViaggio];

    // ---- Explicit choice ----

    [Theory]
    [InlineData("Salute", "categoria Salute")]
    [InlineData("salute", "nella categoria salute")]
    [InlineData("Salute", "in Salute")] // "Mettila in Salute": an exact name needs no marker
    [InlineData("Salute", "Salute")]
    public void TheExactNameOfACategory_IsTheUsersChoice(string mention, string span)
    {
        Assert.Equal((Salute, null), FinanceMentionResolver.StatedCategory(mention, span, TransactionType.Expense, All));
    }

    [Fact]
    public void PartOfAName_IsTheUsersChoice_OnlyWithTheWordCategoria()
    {
        Assert.Equal((RimborsoSpeseViaggio, null),
            FinanceMentionResolver.StatedCategory("rimborso spese", "categoria rimborso spese", TransactionType.Income, All));
        Assert.Equal((null, FinanceTurnIssues.CategoryAmbiguous),
            FinanceMentionResolver.StatedCategory("viaggi", "categorie viaggi", TransactionType.Expense, All));
    }

    // ---- Description vs category ----

    // Live run 1, #4: "120 euro di rimborso spese" stated. With a real list holding "Rimborso spese viaggio", the
    // description would have become the user's choice of that category; now it is asked.
    [Theory]
    [InlineData("rimborso spese", "di rimborso spese")]
    [InlineData("rimborso spese", "rimborso spese")]
    [InlineData("spese", "per spese")]
    public void PartOfANameWithoutTheWordCategoria_IsAsked_NeverAppliedAsTheUsersChoice(string mention, string span)
    {
        Assert.Equal((null, FinanceTurnIssues.CategoryUnclear),
            FinanceMentionResolver.StatedCategory(mention, span, TransactionType.Income, All));
    }

    // "di", "per", "come" are not markers: a word of a name after them is still only part of a name.
    [Theory]
    [InlineData("di viaggi")]
    [InlineData("per viaggi")]
    [InlineData("come viaggi")]
    [InlineData("in viaggi")]
    public void PrepositionsAreNotMarkers(string span)
    {
        Assert.False(FinanceMentionResolver.NamesACategory(span));
        Assert.Equal((null, FinanceTurnIssues.CategoryUnclear), FinanceMentionResolver.StatedCategory("viaggi", span, TransactionType.Expense, All));
    }

    [Theory]
    [InlineData("categoria Casa", true)]
    [InlineData("nella CATEGORIA casa", true)]
    [InlineData("categorìa casa", true)] // accents are ignored, as for names
    [InlineData("le categorie", true)]
    [InlineData("categorizzata", false)] // whole words only
    [InlineData("la spesa", false)]
    [InlineData(null, false)]
    public void TheMarker_IsTheWholeWordCategoria(string? span, bool expected)
    {
        Assert.Equal(expected, FinanceMentionResolver.NamesACategory(span));
    }

    // Live run 1, #2: "45 euro di visita medica" stated. Nothing matches: asked; the category is not set.
    [Theory]
    [InlineData("visita medica", "di visita medica", TransactionType.Expense)]
    [InlineData("rimborso spese", "di rimborso spese", TransactionType.Expense)] // an income category is no answer for an expense
    [InlineData("Criptovalute", "categoria Criptovalute", TransactionType.Expense)] // explicit, but the user has none: asked, not invented
    public void AMentionThatNamesNoCompatibleCategory_IsNotFound(string mention, string span, TransactionType type)
    {
        Assert.Equal((null, FinanceTurnIssues.CategoryNotFound), FinanceMentionResolver.StatedCategory(mention, span, type, All));
    }

    // Live run 1, #5: "24 euro di spesa" stated "spesa". The exact name of a category is applied as the user's choice;
    // the category is the right one, and the user sees it on the card before confirming.
    [Fact]
    public void ADescriptionEqualToACategoryName_ResolvesToThatCategory()
    {
        Assert.Equal((Spesa, null), FinanceMentionResolver.StatedCategory("spesa", "di spesa", TransactionType.Expense, All));
    }

    [Fact]
    public void SameNamedCategoriesOfBothTypes_AreAmbiguousWhileTheTypeIsUnknown()
    {
        var foodExpense = NewCategory("Cibo", CategoryType.Expense);
        var foodIncome = NewCategory("Cibo", CategoryType.Income);

        Assert.Equal((null, FinanceTurnIssues.CategoryAmbiguous), FinanceMentionResolver.StatedCategory("cibo", "categoria cibo", null, [foodExpense, foodIncome]));
        Assert.Equal((foodIncome, null), FinanceMentionResolver.StatedCategory("cibo", "cibo", TransactionType.Income, [foodExpense, foodIncome]));
    }

    // Ownership: resolution reads only the list it is given (the user-scoped repository's); an empty list finds nothing.
    [Fact]
    public void WithoutCategories_NothingIsResolved()
    {
        Assert.Equal((null, FinanceTurnIssues.CategoryNotFound), FinanceMentionResolver.StatedCategory("Salute", "categoria Salute", TransactionType.Expense, []));
    }

    // ---- Suggestions are unchanged: one exact name or nothing ----

    [Theory]
    [InlineData("Salute", FinanceMentionMatch.Resolved)]
    [InlineData("salute", FinanceMentionMatch.Resolved)]
    [InlineData("Viaggi", FinanceMentionMatch.NotFound)]
    [InlineData("Medicina", FinanceMentionMatch.NotFound)]
    public void ASuggestion_MustNameOneCategoryExactly(string mention, FinanceMentionMatch expected)
    {
        Assert.Equal(expected, FinanceMentionResolver.Category(mention, TransactionType.Expense, All, allowPartial: false).Match);
    }

    // ---- The reply asks; it neither claims the category is missing nor that there are several ----

    [Fact]
    public void AnUnclearMention_IsAskedWithTheUsersWords()
    {
        var operation = FinanceOperation.Create(UserId, Guid.CreateVersion7(), AssistantOperationKinds.FinanceTransactionCreate, 1,
            FinanceTransactionDraft.Empty, Created);
        var view = new FinanceOperationView(operation, null, null, [], false, false);

        Assert.Equal("Quale categoria intendi per «rimborso spese»?",
            FinanceAssistantReplies.AfterChange(view, new FinanceTurnIssue(FinanceTurnIssues.CategoryUnclear, "rimborso spese")));
        Assert.Equal("Non trovo la categoria «visita medica». Quale categoria?",
            FinanceAssistantReplies.AfterChange(view, new FinanceTurnIssue(FinanceTurnIssues.CategoryNotFound, "visita medica")));
    }

    private static Category NewCategory(string name, CategoryType type) => Category.Create(UserId, name, type, null, Created);
}
