using System.Globalization;
using LifeOS.App.Services.Finance;
using LifeOS.Contracts.Finance.Accounts;
using LifeOS.Contracts.Finance.Categories;
using LifeOS.Contracts.Finance.Transactions;

namespace LifeOS.UnitTests.Finance;

public class TransactionDisplayTests
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");
    private static readonly TimeZoneInfo UtcPlusTwo = TimeZoneInfo.CreateCustomTimeZone("Test/UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");
    private static readonly DateTime Today = new(2026, 9, 30);

    [Fact]
    public void CompactDateTime_Today()
    {
        Assert.Equal("Today 18:05", Compact(new DateTimeOffset(2026, 9, 30, 16, 5, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void CompactDateTime_Yesterday()
    {
        Assert.Equal("Yesterday 09:30", Compact(new DateTimeOffset(2026, 9, 29, 7, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void CompactDateTime_UsesTheLocalDate()
    {
        // 23:30 UTC on the 29th is already the 30th at UTC+2.
        Assert.Equal("Today 01:30", Compact(new DateTimeOffset(2026, 9, 29, 23, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void CompactDateTime_SameYear()
    {
        Assert.Equal("27 September · 14:10", Compact(new DateTimeOffset(2026, 9, 27, 12, 10, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void CompactDateTime_PreviousYear()
    {
        Assert.Equal("27 September 2025 · 14:10", Compact(new DateTimeOffset(2025, 9, 27, 12, 10, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData(2026, 9, 30, "Today")]
    [InlineData(2026, 9, 29, "Yesterday")]
    [InlineData(2026, 3, 1, "1 March")]
    [InlineData(2025, 12, 31, "31 December 2025")]
    public void DayLabel_IsEnglish(int year, int month, int day, string expected)
    {
        Assert.Equal(expected, TransactionDisplay.DayLabel(new DateTime(year, month, day), Today));
    }

    [Fact]
    public void DateText_IsEnglishEvenWhenTheCurrentCultureIsItalian()
    {
        var previous = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = Italian;

            Assert.Equal("27 September", TransactionDisplay.DayLabel(new DateTime(2026, 9, 27), Today));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("Expense", "−18,50 EUR", "text-danger")]
    [InlineData("Income", "+2.500,00 EUR", "text-success")]
    [InlineData("Transfer", "100,00 EUR", "text-secondary")]
    public void Amount_FollowsTheTransactionType(string type, string text, string cssClass)
    {
        var amount = type switch { "Expense" => 18.5m, "Income" => 2500m, _ => 100m };
        var transaction = new TransactionResponse(Guid.NewGuid(), type, amount, "EUR", null, null, null, null, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow);

        Assert.Equal(text, TransactionDisplay.AmountText(transaction, Italian));
        Assert.Equal(cssClass, TransactionDisplay.AmountCssClass(transaction));
    }

    [Fact]
    public void CategoryLabel_ShowsParentAndChild_AndNullWhenUnknown()
    {
        var parent = new CategoryResponse(Guid.NewGuid(), "Mangiare fuori", "Expense", null, DateTimeOffset.UtcNow);
        var child = new CategoryResponse(Guid.NewGuid(), "Bar", "Expense", parent.Id, DateTimeOffset.UtcNow);
        IReadOnlyList<CategoryResponse> categories = [parent, child];

        Assert.Equal("Mangiare fuori · Bar", TransactionDisplay.CategoryLabel(categories, child.Id));
        Assert.Equal("Mangiare fuori", TransactionDisplay.CategoryLabel(categories, parent.Id));
        Assert.Null(TransactionDisplay.CategoryLabel(categories, Guid.NewGuid()));
    }

    [Fact]
    public void AccountName_IsNullWhenUnknown()
    {
        var account = new AccountResponse(Guid.NewGuid(), "Main account", "BankAccount", "EUR", DateTimeOffset.UtcNow);

        Assert.Equal("Main account", TransactionDisplay.AccountName([account], account.Id));
        Assert.Null(TransactionDisplay.AccountName([account], Guid.NewGuid()));
    }

    // ---- Title / Details (the shared row hierarchy of Home and Transactions) ----

    private static readonly CategoryResponse FoodAndDrink =
        new(Guid.NewGuid(), "Food & Drink", "Expense", null, DateTimeOffset.UtcNow);

    private static readonly CategoryResponse Groceries =
        new(Guid.NewGuid(), "Groceries", "Expense", FoodAndDrink.Id, DateTimeOffset.UtcNow);

    private static readonly AccountResponse Checking =
        new(Guid.NewGuid(), "Checking", "BankAccount", "EUR", DateTimeOffset.UtcNow);

    private static readonly AccountResponse Savings =
        new(Guid.NewGuid(), "Savings", "Savings", "EUR", DateTimeOffset.UtcNow);

    private static readonly IReadOnlyList<CategoryResponse> Categories = [FoodAndDrink, Groceries];
    private static readonly IReadOnlyList<AccountResponse> Accounts = [Checking, Savings];

    [Fact]
    public void Expense_WithoutNote_TitleIsTheCategory_DetailsIsTheAccount()
    {
        var expense = Row("Expense", Checking.Id, null, null, Groceries.Id, note: null);

        Assert.Equal("Food & Drink · Groceries", TransactionDisplay.Title(expense, Categories));
        Assert.Equal("Checking", TransactionDisplay.Details(expense, Accounts, Categories));
    }

    [Fact]
    public void Expense_WithNote_TitleIsTheTrimmedNote_DetailsIsCategoryAndAccount()
    {
        var expense = Row("Expense", Checking.Id, null, null, Groceries.Id, note: "  Weekly shop ");

        Assert.Equal("Weekly shop", TransactionDisplay.Title(expense, Categories));
        Assert.Equal("Food & Drink · Groceries · Checking", TransactionDisplay.Details(expense, Accounts, Categories));
    }

    [Fact]
    public void BlankNote_CountsAsNoNote()
    {
        var income = Row("Income", Checking.Id, null, null, FoodAndDrink.Id, note: "   ");

        Assert.Equal("Food & Drink", TransactionDisplay.Title(income, Categories));
        Assert.Equal("Checking", TransactionDisplay.Details(income, Accounts, Categories));
    }

    [Fact]
    public void Transfer_TitleIsTheNoteOrTransfer_DetailsIsSourceToDestination()
    {
        var plain = Row("Transfer", null, Checking.Id, Savings.Id, null, note: null);
        var withNote = Row("Transfer", null, Checking.Id, Savings.Id, null, note: "Rainy-day fund");

        Assert.Equal("⇄ Transfer", TransactionDisplay.Title(plain, Categories));
        Assert.Equal("Rainy-day fund", TransactionDisplay.Title(withNote, Categories));
        Assert.Equal("Checking → Savings", TransactionDisplay.Details(plain, Accounts, Categories));
        Assert.Equal("Checking → Savings", TransactionDisplay.Details(withNote, Accounts, Categories));
    }

    [Fact]
    public void UnresolvedNames_UseFallbackWording()
    {
        var expense = Row("Expense", Guid.NewGuid(), null, null, Guid.NewGuid(), note: "Lunch");
        var transfer = Row("Transfer", null, Guid.NewGuid(), Savings.Id, null, note: null);

        Assert.Equal("Unknown category", TransactionDisplay.Title(expense with { Note = null }, Categories));
        Assert.Equal("Unknown category · Unknown account", TransactionDisplay.Details(expense, Accounts, Categories));
        Assert.Equal("Unknown account → Savings", TransactionDisplay.Details(transfer, [Savings], []));
    }

    private static TransactionResponse Row(
        string type,
        Guid? accountId,
        Guid? sourceAccountId,
        Guid? destinationAccountId,
        Guid? categoryId,
        string? note) =>
        new(Guid.NewGuid(), type, 10m, "EUR", accountId, sourceAccountId, destinationAccountId, categoryId, DateTimeOffset.UtcNow, note, DateTimeOffset.UtcNow);

    private static string Compact(DateTimeOffset occurredAtUtc) =>
        TransactionDisplay.CompactDateTime(occurredAtUtc, Today, UtcPlusTwo, Italian);
}
