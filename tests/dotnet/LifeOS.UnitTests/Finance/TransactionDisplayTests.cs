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
        Assert.Equal("Oggi 18:05", Compact(new DateTimeOffset(2026, 9, 30, 16, 5, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void CompactDateTime_Yesterday()
    {
        Assert.Equal("Ieri 09:30", Compact(new DateTimeOffset(2026, 9, 29, 7, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void CompactDateTime_UsesTheLocalDate()
    {
        // 23:30 UTC on the 29th is already the 30th at UTC+2.
        Assert.Equal("Oggi 01:30", Compact(new DateTimeOffset(2026, 9, 29, 23, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void CompactDateTime_SameYear()
    {
        Assert.Equal("27 settembre · 14:10", Compact(new DateTimeOffset(2026, 9, 27, 12, 10, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void CompactDateTime_PreviousYear()
    {
        Assert.Equal("27 settembre 2025 · 14:10", Compact(new DateTimeOffset(2025, 9, 27, 12, 10, 0, TimeSpan.Zero)));
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

    private static string Compact(DateTimeOffset occurredAtUtc) =>
        TransactionDisplay.CompactDateTime(occurredAtUtc, Today, UtcPlusTwo, Italian);
}
