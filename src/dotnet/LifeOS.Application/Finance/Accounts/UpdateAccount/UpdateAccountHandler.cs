using LifeOS.Application.Finance.Accounts.GetAccounts;

namespace LifeOS.Application.Finance.Accounts.UpdateAccount;

// Renames an account and/or changes its type. Names are current metadata: existing transactions
// show the new name. The currency and the opening balance are not changed.
public sealed class UpdateAccountHandler
{
    private readonly IAccountRepository _accountRepository;

    public UpdateAccountHandler(IAccountRepository accountRepository)
    {
        _accountRepository = accountRepository;
    }

    // Throws ArgumentException for an invalid name or account type.
    public async Task<UpdateAccountResult> HandleAsync(
        Guid userId,
        UpdateAccountCommand command,
        CancellationToken cancellationToken)
    {
        // Scoped: another user's account is reported exactly like a missing one.
        var account = await _accountRepository.GetByIdAsync(userId, command.AccountId, cancellationToken);

        if (account is null)
        {
            return UpdateAccountResult.NotFound();
        }

        account.Rename(command.Name);
        account.ChangeType(command.AccountType);

        // The row may have been deleted since it was read.
        if (!await _accountRepository.TryUpdateAsync(account, cancellationToken))
        {
            return UpdateAccountResult.NotFound();
        }

        return UpdateAccountResult.Updated(new AccountSummary(
            account.Id,
            account.Name,
            account.AccountType,
            account.Currency,
            account.CreatedAtUtc));
    }
}
