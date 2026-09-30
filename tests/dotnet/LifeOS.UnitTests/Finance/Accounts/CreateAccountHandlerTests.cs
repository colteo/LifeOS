using LifeOS.Application.Finance.Accounts.CreateAccount;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Accounts;

public class CreateAccountHandlerTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 9, 29, 10, 30, 0, TimeSpan.Zero);

    private readonly InMemoryAccountRepository _repository = new();
    private readonly CreateAccountHandler _handler;

    public CreateAccountHandlerTests()
    {
        _handler = new CreateAccountHandler(_repository, new FixedTimeProvider(UtcNow));
    }

    [Fact]
    public async Task HandleAsync_WithValidInput_CreatesAndPersistsAccount()
    {
        var command = new CreateAccountCommand("  Main account ", AccountType.BankAccount, "eur");

        var result = await _handler.HandleAsync(TestUsers.A, command, CancellationToken.None);

        var persisted = Assert.Single(_repository.Accounts);
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal("Main account", result.Name);
        Assert.Equal(AccountType.BankAccount, result.AccountType);
        Assert.Equal("EUR", result.Currency);
        Assert.Equal(persisted.Id, result.Id);
    }

    [Fact]
    public async Task HandleAsync_UsesCurrentTimeFromTimeProvider()
    {
        var command = new CreateAccountCommand("Main account", AccountType.BankAccount, "EUR");

        var result = await _handler.HandleAsync(TestUsers.A, command, CancellationToken.None);

        Assert.Equal(UtcNow, result.CreatedAtUtc);
        Assert.Equal(UtcNow, _repository.Accounts.Single().CreatedAtUtc);
    }

    [Fact]
    public async Task HandleAsync_PassesCreatedAccountToRepository()
    {
        var command = new CreateAccountCommand("Wallet", AccountType.Cash, "USD");

        var result = await _handler.HandleAsync(TestUsers.A, command, CancellationToken.None);

        var persisted = Assert.Single(_repository.Accounts);
        Assert.Equal(result.Id, persisted.Id);
        Assert.Equal("Wallet", persisted.Name);
        Assert.Equal(AccountType.Cash, persisted.AccountType);
        Assert.Equal("USD", persisted.Currency);
        Assert.Equal(UtcNow, persisted.CreatedAtUtc);
    }

    [Theory]
    [InlineData(" ", AccountType.BankAccount, "EUR")]
    [InlineData("Main account", AccountType.BankAccount, "EURO")]
    [InlineData("Main account", (AccountType)99, "EUR")]
    public async Task HandleAsync_WithInvalidInput_ThrowsAndDoesNotPersist(
        string name,
        AccountType accountType,
        string currency)
    {
        var command = new CreateAccountCommand(name, accountType, currency);

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _handler.HandleAsync(TestUsers.A, command, CancellationToken.None));

        Assert.Empty(_repository.Accounts);
    }
}
