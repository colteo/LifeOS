namespace LifeOS.Application.Finance.Accounts.GetAccounts;

public sealed class GetAccountsHandler
{
    private readonly IAccountRepository _accountRepository;

    public GetAccountsHandler(IAccountRepository accountRepository)
    {
        _accountRepository = accountRepository;
    }

    public async Task<IReadOnlyList<AccountSummary>> HandleAsync(Guid userId, CancellationToken cancellationToken)
    {
        var accounts = await _accountRepository.GetAllAsync(userId, cancellationToken);

        return accounts
            .Select(account => new AccountSummary(
                account.Id,
                account.Name,
                account.AccountType,
                account.Currency,
                account.CreatedAtUtc))
            .ToList();
    }
}
