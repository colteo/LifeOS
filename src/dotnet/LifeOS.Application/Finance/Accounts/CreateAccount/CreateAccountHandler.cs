using LifeOS.Domain.Finance.Accounts;

namespace LifeOS.Application.Finance.Accounts.CreateAccount;

public sealed class CreateAccountHandler
{
    private readonly IAccountRepository _accountRepository;
    private readonly TimeProvider _timeProvider;

    public CreateAccountHandler(IAccountRepository accountRepository, TimeProvider timeProvider)
    {
        _accountRepository = accountRepository;
        _timeProvider = timeProvider;
    }

    public async Task<CreateAccountResult> HandleAsync(
        Guid userId,
        CreateAccountCommand command,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        var account = Account.Create(
            userId,
            command.Name,
            command.AccountType,
            command.Currency,
            now);

        var openingBalance = command.OpeningBalance is { } input
            ? OpeningBalance.Create(account, input.Amount, input.AsOfUtc, now)
            : null;

        await _accountRepository.AddAsync(account, openingBalance, cancellationToken);

        return new CreateAccountResult(
            account.Id,
            account.Name,
            account.AccountType,
            account.Currency,
            account.CreatedAtUtc);
    }
}
