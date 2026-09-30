using LifeOS.Application.Finance.Accounts;
using LifeOS.Application.Finance.Accounts.CreateAccount;
using LifeOS.Application.Finance.Accounts.GetAccountBalances;
using LifeOS.Application.Finance.Accounts.SetOpeningBalance;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Accounts;

public class OpeningBalanceHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 16, 0, 0, TimeSpan.Zero);

    private readonly InMemoryOpeningBalanceRepository _openingBalances = new();
    private readonly InMemoryAccountRepository _accounts;
    private readonly InMemoryTransactionRepository _transactions = new();

    public OpeningBalanceHandlersTests()
    {
        _accounts = new InMemoryAccountRepository(_openingBalances);
    }

    // ---- CreateAccount ----

    [Fact]
    public async Task CreateAccount_WithOpeningBalance_SavesBothTogether()
    {
        var result = await CreateAccountAsync(new OpeningBalanceInput(-350m, Now.AddMinutes(-1)));

        var openingBalance = Assert.Single(_openingBalances.OpeningBalances);
        Assert.Equal(result.Id, openingBalance.AccountId);
        Assert.Equal(TestUsers.A, openingBalance.UserId);
        Assert.Equal(-350m, openingBalance.Amount);
        Assert.Equal(Now.AddMinutes(-1), openingBalance.AsOfUtc);
        Assert.Equal(Now, openingBalance.CreatedAtUtc);
    }

    [Fact]
    public async Task CreateAccount_WithoutOpeningBalance_CreatesOnlyTheAccount()
    {
        await CreateAccountAsync(null);

        Assert.Single(_accounts.Accounts);
        Assert.Empty(_openingBalances.OpeningBalances);
    }

    [Fact]
    public async Task CreateAccount_WithFutureOpeningBalance_ThrowsAndSavesNothing()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => CreateAccountAsync(new OpeningBalanceInput(10m, Now.AddHours(1))));

        Assert.Empty(_accounts.Accounts);
        Assert.Empty(_openingBalances.OpeningBalances);
    }

    // ---- SetOpeningBalance ----

    [Fact]
    public async Task Set_OnAccountWithout_Creates()
    {
        var account = AddAccount(TestUsers.A);

        var result = await SetAsync(TestUsers.A, account.Id, 100m, Now.AddHours(-2));

        Assert.Equal(SetOpeningBalanceStatus.Created, result.Status);
        Assert.Equal(100m, Assert.Single(_openingBalances.OpeningBalances).Amount);
    }

    [Fact]
    public async Task Set_SameValuesAgain_IsUnchanged()
    {
        var account = AddAccount(TestUsers.A);
        var asOf = Now.AddHours(-2).AddTicks(3); // sub-microsecond ticks are normalized away

        await SetAsync(TestUsers.A, account.Id, 100.50m, asOf);
        var retry = await SetAsync(TestUsers.A, account.Id, 100.5m, asOf);

        Assert.Equal(SetOpeningBalanceStatus.Unchanged, retry.Status);
        Assert.Single(_openingBalances.OpeningBalances);
    }

    [Theory]
    [InlineData(101, 0)]
    [InlineData(100, 1)]
    public async Task Set_DifferentValues_IsAConflict(int amount, int minutesLater)
    {
        var account = AddAccount(TestUsers.A);
        await SetAsync(TestUsers.A, account.Id, 100m, Now.AddHours(-2));

        var result = await SetAsync(TestUsers.A, account.Id, amount, Now.AddHours(-2).AddMinutes(minutesLater));

        Assert.Equal(SetOpeningBalanceStatus.Conflict, result.Status);
        Assert.Equal(100m, Assert.Single(_openingBalances.OpeningBalances).Amount);
    }

    [Fact]
    public async Task Set_OnAnotherUsersAccount_IsNotFound()
    {
        var accountOfB = AddAccount(TestUsers.B);

        var result = await SetAsync(TestUsers.A, accountOfB.Id, 100m, Now);

        Assert.Equal(SetOpeningBalanceStatus.NotFound, result.Status);
        Assert.Empty(_openingBalances.OpeningBalances);
    }

    [Fact]
    public async Task Set_OnMissingAccount_IsNotFound()
    {
        var result = await SetAsync(TestUsers.A, Guid.CreateVersion7(), 100m, Now);

        Assert.Equal(SetOpeningBalanceStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task Set_LosingAConcurrentInsert_ResolvesAgainstTheWinner()
    {
        var account = AddAccount(TestUsers.A);
        var asOf = Now.AddHours(-2);
        _openingBalances.BeforeAdd = () =>
        {
            _openingBalances.BeforeAdd = null;
            _openingBalances.OpeningBalances.Add(OpeningBalance.Create(account, 100m, asOf, Now));
        };

        var result = await SetAsync(TestUsers.A, account.Id, 100m, asOf);

        Assert.Equal(SetOpeningBalanceStatus.Unchanged, result.Status);
        Assert.Single(_openingBalances.OpeningBalances);
    }

    // ---- GetAccountBalances ----

    [Fact]
    public async Task Balances_CoverOnlyTheCallersAccounts_WithTheAccountCurrency()
    {
        var checking = AddAccount(TestUsers.A);
        AddAccount(TestUsers.B);
        _openingBalances.OpeningBalances.Add(OpeningBalance.Create(checking, 1000m, Now.AddDays(-1), Now));

        var result = await BalancesAsync(TestUsers.A, null);

        var balance = Assert.Single(result.Balances);
        Assert.Equal(checking.Id, balance.AccountId);
        Assert.Equal("EUR", balance.Currency);
        Assert.Equal(1000m, balance.Balance);
        Assert.Equal(Now, balance.AtUtc);
        Assert.Equal(1000m, balance.OpeningBalance!.Amount);
    }

    [Fact]
    public async Task Balances_TransferBetweenOwnAccounts_UpdatesBoth()
    {
        var checking = AddAccount(TestUsers.A);
        var savings = AddAccount(TestUsers.A);
        _transactions.Transactions.Add(Transaction.CreateTransfer(
            TestUsers.A, checking.Id, savings.Id, 200m, "EUR", Now.AddHours(-1), null, Now));

        var result = await BalancesAsync(TestUsers.A, null);

        Assert.Equal(-200m, result.Balances.Single(balance => balance.AccountId == checking.Id).Balance);
        Assert.Equal(200m, result.Balances.Single(balance => balance.AccountId == savings.Id).Balance);
    }

    [Fact]
    public async Task Balances_BeforeTheBaseline_AreNotAvailable()
    {
        var checking = AddAccount(TestUsers.A);
        _openingBalances.OpeningBalances.Add(OpeningBalance.Create(checking, 1000m, Now.AddDays(-1), Now));

        var result = await BalancesAsync(TestUsers.A, Now.AddDays(-2));

        Assert.Null(Assert.Single(result.Balances).Balance);
    }

    [Fact]
    public async Task Balances_WithNonUtcInstant_AreInvalid()
    {
        var result = await BalancesAsync(TestUsers.A, new DateTimeOffset(2026, 9, 30, 18, 0, 0, TimeSpan.FromHours(2)));

        Assert.Equal(GetAccountBalancesStatus.Invalid, result.Status);
        Assert.Equal("atUtc", result.Field);
    }

    private Task<CreateAccountResult> CreateAccountAsync(OpeningBalanceInput? openingBalance) =>
        new CreateAccountHandler(_accounts, new FixedTimeProvider(Now)).HandleAsync(
            TestUsers.A,
            new CreateAccountCommand("Card", AccountType.CreditCard, "EUR", openingBalance),
            CancellationToken.None);

    private Task<SetOpeningBalanceResult> SetAsync(Guid userId, Guid accountId, decimal amount, DateTimeOffset asOf) =>
        new SetOpeningBalanceHandler(_accounts, _openingBalances, new FixedTimeProvider(Now)).HandleAsync(
            userId,
            new SetOpeningBalanceCommand(accountId, amount, asOf),
            CancellationToken.None);

    private Task<GetAccountBalancesResult> BalancesAsync(Guid userId, DateTimeOffset? at) =>
        new GetAccountBalancesHandler(_accounts, _openingBalances, _transactions, new FixedTimeProvider(Now)).HandleAsync(
            userId,
            new GetAccountBalancesQuery(at),
            CancellationToken.None);

    private Account AddAccount(Guid userId)
    {
        var account = Account.Create(userId, "Account " + _accounts.Accounts.Count, AccountType.BankAccount, "EUR", Now.AddDays(-10));
        _accounts.Accounts.Add(account);

        return account;
    }
}
