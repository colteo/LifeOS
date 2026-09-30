using LifeOS.Domain.Finance.Accounts;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Accounts;

public class AccountTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 9, 29, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Create_WithValidInput_ReturnsAccount()
    {
        var account = Account.Create(TestUsers.A, "Main account", AccountType.BankAccount, "EUR", CreatedAtUtc);

        Assert.NotEqual(Guid.Empty, account.Id);
        Assert.Equal("Main account", account.Name);
        Assert.Equal(AccountType.BankAccount, account.AccountType);
        Assert.Equal("EUR", account.Currency);
        Assert.Equal(CreatedAtUtc, account.CreatedAtUtc);
    }

    [Fact]
    public void Create_SetsOwningUser()
    {
        var account = Account.Create(TestUsers.A, "Main account", AccountType.BankAccount, "EUR", CreatedAtUtc);

        Assert.Equal(TestUsers.A, account.UserId);
    }

    [Fact]
    public void Create_WithEmptyUserId_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => Account.Create(Guid.Empty, "Main account", AccountType.BankAccount, "EUR", CreatedAtUtc));

        Assert.Equal("userId", exception.ParamName);
    }

    [Fact]
    public void Create_GeneratesDistinctIds()
    {
        var first = Account.Create(TestUsers.A, "Wallet", AccountType.Cash, "EUR", CreatedAtUtc);
        var second = Account.Create(TestUsers.A, "Wallet", AccountType.Cash, "EUR", CreatedAtUtc);

        Assert.NotEqual(first.Id, second.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Create_WithNullOrEmptyName_Throws(string? name)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => Account.Create(TestUsers.A, name!, AccountType.BankAccount, "EUR", CreatedAtUtc));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t\n  ")]
    public void Create_WithWhitespaceName_Throws(string name)
    {
        Assert.Throws<ArgumentException>(
            () => Account.Create(TestUsers.A, name, AccountType.BankAccount, "EUR", CreatedAtUtc));
    }

    [Fact]
    public void Create_TrimsName()
    {
        var account = Account.Create(TestUsers.A, "  Main account  ", AccountType.BankAccount, "EUR", CreatedAtUtc);

        Assert.Equal("Main account", account.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("EU")]
    [InlineData("EURO")]
    [InlineData("E1R")]
    [InlineData("€UR")]
    public void Create_WithInvalidCurrency_Throws(string? currency)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => Account.Create(TestUsers.A, "Main account", AccountType.BankAccount, currency!, CreatedAtUtc));
    }

    [Theory]
    [InlineData("eur", "EUR")]
    [InlineData("UsD", "USD")]
    [InlineData(" gbp ", "GBP")]
    public void Create_NormalizesCurrencyToUppercase(string currency, string expected)
    {
        var account = Account.Create(TestUsers.A, "Main account", AccountType.BankAccount, currency, CreatedAtUtc);

        Assert.Equal(expected, account.Currency);
    }

    [Fact]
    public void Create_NormalizesCreatedAtToUtc()
    {
        var createdAt = new DateTimeOffset(2026, 9, 29, 12, 30, 0, TimeSpan.FromHours(2));

        var account = Account.Create(TestUsers.A, "Main account", AccountType.BankAccount, "EUR", createdAt);

        Assert.Equal(TimeSpan.Zero, account.CreatedAtUtc.Offset);
        Assert.Equal(new DateTime(2026, 9, 29, 10, 30, 0), account.CreatedAtUtc.DateTime);
        Assert.Equal(createdAt.UtcTicks, account.CreatedAtUtc.UtcTicks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void Create_WithUndefinedAccountType_Throws(int accountType)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Account.Create(TestUsers.A, "Main account", (AccountType)accountType, "EUR", CreatedAtUtc));
    }
}
