using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Accounts;

public class OpeningBalanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 16, 0, 0, TimeSpan.Zero);

    private readonly Account _account = Account.Create(TestUsers.A, "Checking", AccountType.BankAccount, "EUR", Now.AddDays(-1));

    [Theory]
    [InlineData("1250.50")]
    [InlineData("0")]
    [InlineData("-350.00")]
    public void Create_AcceptsPositiveZeroAndNegativeAmounts(string amount)
    {
        var openingBalance = OpeningBalance.Create(_account, decimal.Parse(amount), Now, Now);

        Assert.Equal(decimal.Parse(amount), openingBalance.Amount);
    }

    [Fact]
    public void Create_TakesOwnerAndAccountFromTheAccount()
    {
        var openingBalance = OpeningBalance.Create(_account, 10m, Now, Now);

        Assert.Equal(7, openingBalance.Id.Version);
        Assert.Equal(TestUsers.A, openingBalance.UserId);
        Assert.Equal(_account.Id, openingBalance.AccountId);
        Assert.Equal(Now, openingBalance.AsOfUtc);
        Assert.Equal(Now, openingBalance.CreatedAtUtc);
    }

    [Fact]
    public void Create_AllowsAnEarlierInstantThanTheAccount()
    {
        var openingBalance = OpeningBalance.Create(_account, 10m, Now.AddMonths(-6), Now);

        Assert.Equal(Now.AddMonths(-6), openingBalance.AsOfUtc);
    }

    [Fact]
    public void Create_ToleratesSmallClockSkew()
    {
        var openingBalance = OpeningBalance.Create(_account, 10m, Now + OpeningBalance.AllowedClockSkew, Now);

        Assert.Equal(Now + OpeningBalance.AllowedClockSkew, openingBalance.AsOfUtc);
    }

    [Fact]
    public void Create_InTheFuture_Throws()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => OpeningBalance.Create(_account, 10m, Now + OpeningBalance.AllowedClockSkew + TimeSpan.FromSeconds(1), Now));

        Assert.Equal("asOfUtc", exception.ParamName);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void Create_BeyondTheSupportedRange_Throws(int sign)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => OpeningBalance.Create(_account, sign * (Transaction.MaxAmount + 1m), Now, Now));

        Assert.Equal("amount", exception.ParamName);
    }

    [Fact]
    public void Create_WithMoreThanFourDecimals_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => OpeningBalance.Create(_account, -1.23456m, Now, Now));

        Assert.Equal("amount", exception.ParamName);
    }

    [Fact]
    public void Create_StoresUtcTruncatedToMicroseconds()
    {
        var local = new DateTimeOffset(2026, 9, 30, 18, 0, 0, TimeSpan.FromHours(2)).AddTicks(1234567);

        var openingBalance = OpeningBalance.Create(_account, 10m, local, Now.AddHours(1));

        Assert.Equal(TimeSpan.Zero, openingBalance.AsOfUtc.Offset);
        Assert.Equal(local.UtcDateTime.AddTicks(-7), openingBalance.AsOfUtc.UtcDateTime);
        Assert.Equal(OpeningBalance.NormalizeAsOf(local), openingBalance.AsOfUtc);
    }
}
