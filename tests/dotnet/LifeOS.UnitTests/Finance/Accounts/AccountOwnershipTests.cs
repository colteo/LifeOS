using LifeOS.Application.Finance.Accounts.CreateAccount;
using LifeOS.Application.Finance.Accounts.GetAccounts;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Accounts;

public class AccountOwnershipTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);

    private readonly InMemoryAccountRepository _repository = new();

    [Fact]
    public async Task CreateAccount_IsOwnedByTheCaller()
    {
        var handler = new CreateAccountHandler(_repository, new FixedTimeProvider(UtcNow));

        await handler.HandleAsync(TestUsers.B, new CreateAccountCommand("Wallet", AccountType.Cash, "EUR"), CancellationToken.None);

        Assert.Equal(TestUsers.B, Assert.Single(_repository.Accounts).UserId);
    }

    [Fact]
    public async Task GetAccounts_ReturnsOnlyTheCallersAccounts()
    {
        var ofA = AddAccount(TestUsers.A, "A checking");
        AddAccount(TestUsers.B, "B checking");

        var accounts = await new GetAccountsHandler(_repository).HandleAsync(TestUsers.A, CancellationToken.None);

        Assert.Equal(ofA.Id, Assert.Single(accounts).Id);
    }

    [Fact]
    public async Task GetAccounts_ForUserWithoutAccounts_ReturnsEmpty()
    {
        AddAccount(TestUsers.A, "A checking");

        var accounts = await new GetAccountsHandler(_repository).HandleAsync(TestUsers.B, CancellationToken.None);

        Assert.Empty(accounts);
    }

    [Fact]
    public async Task GetById_ForAnotherUsersAccount_ReturnsNull()
    {
        var ofA = AddAccount(TestUsers.A, "A checking");

        Assert.Null(await _repository.GetByIdAsync(TestUsers.B, ofA.Id, CancellationToken.None));
        Assert.NotNull(await _repository.GetByIdAsync(TestUsers.A, ofA.Id, CancellationToken.None));
    }

    private Account AddAccount(Guid userId, string name)
    {
        var account = Account.Create(userId, name, AccountType.BankAccount, "EUR", UtcNow);
        _repository.Accounts.Add(account);

        return account;
    }
}
