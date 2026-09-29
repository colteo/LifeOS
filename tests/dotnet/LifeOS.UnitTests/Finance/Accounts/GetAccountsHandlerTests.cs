using LifeOS.Application.Finance.Accounts.GetAccounts;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Accounts;

public class GetAccountsHandlerTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 9, 29, 10, 30, 0, TimeSpan.Zero);

    private readonly InMemoryAccountRepository _repository = new();
    private readonly GetAccountsHandler _handler;

    public GetAccountsHandlerTests()
    {
        _handler = new GetAccountsHandler(_repository);
    }

    [Fact]
    public async Task HandleAsync_ReturnsAllAccountsFromRepository()
    {
        var mainAccount = Account.Create("Main account", AccountType.BankAccount, "EUR", CreatedAtUtc);
        var wallet = Account.Create("Wallet", AccountType.Cash, "USD", CreatedAtUtc.AddDays(1));
        _repository.Accounts.AddRange([mainAccount, wallet]);

        var result = await _handler.HandleAsync(CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(
            new AccountSummary(mainAccount.Id, "Main account", AccountType.BankAccount, "EUR", CreatedAtUtc),
            result);
        Assert.Contains(
            new AccountSummary(wallet.Id, "Wallet", AccountType.Cash, "USD", CreatedAtUtc.AddDays(1)),
            result);
    }

    [Fact]
    public async Task HandleAsync_WithNoAccounts_ReturnsEmptyCollection()
    {
        var result = await _handler.HandleAsync(CancellationToken.None);

        Assert.Empty(result);
    }
}
