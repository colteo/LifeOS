using LifeOS.Application.Finance.Accounts;
using LifeOS.Application.Finance.Accounts.DeleteAccount;
using LifeOS.Application.Finance.Accounts.UpdateAccount;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Accounts;

public class AccountManagementHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 16, 0, 0, TimeSpan.Zero);

    private readonly InMemoryOpeningBalanceRepository _openingBalances = new();
    private readonly InMemoryAccountRepository _accounts;
    private readonly InMemoryTransactionRepository _transactions = new();

    public AccountManagementHandlersTests()
    {
        _accounts = new InMemoryAccountRepository(_openingBalances);
    }

    // ---- UpdateAccount ----

    [Fact]
    public async Task Update_RenamesAndChangesTheType_KeepingCurrencyAndOpeningBalance()
    {
        var account = AddAccount(TestUsers.A, "Main", AccountType.BankAccount, "USD");
        var openingBalance = AddOpeningBalance(account, 250m);

        var result = await UpdateAsync(TestUsers.A, account.Id, "  Everyday  ", AccountType.Savings);

        Assert.Equal(UpdateAccountStatus.Updated, result.Status);
        Assert.Equal("Everyday", result.Account!.Name);
        Assert.Equal(AccountType.Savings, result.Account.AccountType);
        Assert.Equal("USD", result.Account.Currency);
        Assert.Equal(account.CreatedAtUtc, result.Account.CreatedAtUtc);

        var stored = _accounts.Stored(account.Id);
        Assert.Equal("Everyday", stored.Name);
        Assert.Equal(AccountType.Savings, stored.AccountType);
        Assert.Equal("USD", stored.Currency);
        Assert.Equal(openingBalance.Id, Assert.Single(_openingBalances.OpeningBalances).Id);
    }

    [Fact]
    public async Task Update_WithTheSameValues_Succeeds()
    {
        var account = AddAccount(TestUsers.A, "Main", AccountType.Cash, "EUR");

        var result = await UpdateAsync(TestUsers.A, account.Id, "Main", AccountType.Cash);

        Assert.Equal(UpdateAccountStatus.Updated, result.Status);
        Assert.Equal("Main", _accounts.Stored(account.Id).Name);
    }

    [Fact]
    public async Task Update_AnotherUsersAccount_IsNotFoundAndUnchanged()
    {
        var accountOfB = AddAccount(TestUsers.B, "B's", AccountType.Cash, "EUR");

        var result = await UpdateAsync(TestUsers.A, accountOfB.Id, "Mine now", AccountType.Savings);

        Assert.Equal(UpdateAccountStatus.NotFound, result.Status);
        Assert.Null(result.Account);
        Assert.Equal("B's", _accounts.Stored(accountOfB.Id).Name);
        Assert.Equal(AccountType.Cash, _accounts.Stored(accountOfB.Id).AccountType);
    }

    [Fact]
    public async Task Update_MissingAccount_IsNotFound()
    {
        var result = await UpdateAsync(TestUsers.A, Guid.CreateVersion7(), "Main", AccountType.Cash);

        Assert.Equal(UpdateAccountStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task Update_AccountDeletedBetweenReadAndWrite_IsNotFound()
    {
        var account = AddAccount(TestUsers.A, "Main", AccountType.Cash, "EUR");
        _accounts.BeforeWrite = () => _accounts.Accounts.Clear();

        var result = await UpdateAsync(TestUsers.A, account.Id, "Renamed", AccountType.Cash);

        Assert.Equal(UpdateAccountStatus.NotFound, result.Status);
        Assert.Empty(_accounts.Accounts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Update_WithBlankName_ThrowsAndStoresNothing(string name)
    {
        var account = AddAccount(TestUsers.A, "Main", AccountType.Cash, "EUR");

        await Assert.ThrowsAnyAsync<ArgumentException>(() => UpdateAsync(TestUsers.A, account.Id, name, AccountType.Savings));

        Assert.Equal("Main", _accounts.Stored(account.Id).Name);
        Assert.Equal(AccountType.Cash, _accounts.Stored(account.Id).AccountType);
    }

    // ---- DeleteAccount ----

    [Fact]
    public async Task Delete_UnusedAccountWithoutOpeningBalance_DeletesIt()
    {
        var account = AddAccount(TestUsers.A, "Main", AccountType.Cash, "EUR");
        var other = AddAccount(TestUsers.A, "Other", AccountType.Cash, "EUR");

        var result = await DeleteAsync(TestUsers.A, account.Id);

        Assert.Equal(DeleteAccountResult.Deleted, result);
        Assert.Equal(other.Id, Assert.Single(_accounts.Accounts).Id);
    }

    [Fact]
    public async Task Delete_UnusedAccountWithOpeningBalance_DeletesBoth()
    {
        var account = AddAccount(TestUsers.A, "Main", AccountType.Cash, "EUR");
        AddOpeningBalance(account, 100m);
        var other = AddAccount(TestUsers.A, "Other", AccountType.Cash, "EUR");
        var othersBalance = AddOpeningBalance(other, 5m);

        var result = await DeleteAsync(TestUsers.A, account.Id);

        Assert.Equal(DeleteAccountResult.Deleted, result);
        Assert.Equal(other.Id, Assert.Single(_accounts.Accounts).Id);
        Assert.Equal(othersBalance.Id, Assert.Single(_openingBalances.OpeningBalances).Id);
    }

    [Fact]
    public async Task Delete_TheLastAccount_IsAllowed()
    {
        var account = AddAccount(TestUsers.A, "Only", AccountType.Cash, "EUR");

        Assert.Equal(DeleteAccountResult.Deleted, await DeleteAsync(TestUsers.A, account.Id));
        Assert.Empty(_accounts.Accounts);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("source")]
    [InlineData("destination")]
    public async Task Delete_AccountReferencedByATransaction_IsBlockedAndDeletesNothing(string reference)
    {
        var account = AddAccount(TestUsers.A, "Main", AccountType.Cash, "EUR");
        var openingBalance = AddOpeningBalance(account, 100m);
        var other = AddAccount(TestUsers.A, "Other", AccountType.Cash, "EUR");
        _transactions.Transactions.Add(reference switch
        {
            "account" => Transaction.CreateExpense(TestUsers.A, account.Id, Guid.CreateVersion7(), 10m, "EUR", Now, null, Now),
            "source" => Transaction.CreateTransfer(TestUsers.A, account.Id, other.Id, 10m, "EUR", Now, null, Now),
            _ => Transaction.CreateTransfer(TestUsers.A, other.Id, account.Id, 10m, "EUR", Now, null, Now)
        });

        var result = await DeleteAsync(TestUsers.A, account.Id);

        Assert.Equal(DeleteAccountResult.HasTransactions, result);
        Assert.Equal(2, _accounts.Accounts.Count);
        Assert.Equal(openingBalance.Id, Assert.Single(_openingBalances.OpeningBalances).Id);
        Assert.Single(_transactions.Transactions);
    }

    [Fact]
    public async Task Delete_AnotherUsersTransactions_DoNotBlock()
    {
        // Impossible through the API (references are owner-scoped); the check is scoped all the same.
        var account = AddAccount(TestUsers.A, "Main", AccountType.Cash, "EUR");
        _transactions.Transactions.Add(
            Transaction.CreateExpense(TestUsers.B, account.Id, Guid.CreateVersion7(), 10m, "EUR", Now, null, Now));

        Assert.Equal(DeleteAccountResult.Deleted, await DeleteAsync(TestUsers.A, account.Id));
    }

    [Fact]
    public async Task Delete_AnotherUsersAccount_IsNotFoundAndKeepsIt()
    {
        var accountOfB = AddAccount(TestUsers.B, "B's", AccountType.Cash, "EUR");
        AddOpeningBalance(accountOfB, 100m);

        var result = await DeleteAsync(TestUsers.A, accountOfB.Id);

        Assert.Equal(DeleteAccountResult.NotFound, result);
        Assert.Single(_accounts.Accounts);
        Assert.Single(_openingBalances.OpeningBalances);
    }

    [Fact]
    public async Task Delete_MissingAccount_IsNotFound()
    {
        Assert.Equal(DeleteAccountResult.NotFound, await DeleteAsync(TestUsers.A, Guid.CreateVersion7()));
    }

    [Fact]
    public async Task Delete_AccountDeletedConcurrently_IsNotFound()
    {
        var account = AddAccount(TestUsers.A, "Main", AccountType.Cash, "EUR");
        _accounts.BeforeWrite = () => _accounts.Accounts.Clear();

        Assert.Equal(DeleteAccountResult.NotFound, await DeleteAsync(TestUsers.A, account.Id));
    }

    [Theory]
    [InlineData(AccountDeleteOutcome.HasTransactions, DeleteAccountResult.HasTransactions)]
    [InlineData(AccountDeleteOutcome.Changed, DeleteAccountResult.Changed)]
    public async Task Delete_RejectedByTheDatabaseBackstop_ReportsWhyAndDeletesNothing(
        AccountDeleteOutcome outcome,
        DeleteAccountResult expected)
    {
        // A transaction or opening balance created after the handler's check.
        var account = AddAccount(TestUsers.A, "Main", AccountType.Cash, "EUR");
        _accounts.RejectDeleteWith = outcome;

        var result = await DeleteAsync(TestUsers.A, account.Id);

        Assert.Equal(expected, result);
        Assert.Single(_accounts.Accounts);
    }

    private Task<UpdateAccountResult> UpdateAsync(Guid userId, Guid accountId, string name, AccountType accountType) =>
        new UpdateAccountHandler(_accounts).HandleAsync(
            userId,
            new UpdateAccountCommand(accountId, name, accountType),
            CancellationToken.None);

    private Task<DeleteAccountResult> DeleteAsync(Guid userId, Guid accountId) =>
        new DeleteAccountHandler(_accounts, _transactions).HandleAsync(userId, accountId, CancellationToken.None);

    private Account AddAccount(Guid userId, string name, AccountType accountType, string currency)
    {
        var account = Account.Create(userId, name, accountType, currency, Now.AddDays(-10));
        _accounts.Accounts.Add(account);

        return account;
    }

    private OpeningBalance AddOpeningBalance(Account account, decimal amount)
    {
        var openingBalance = OpeningBalance.Create(account, amount, Now.AddDays(-5), Now.AddDays(-5));
        _openingBalances.OpeningBalances.Add(openingBalance);

        return openingBalance;
    }
}
