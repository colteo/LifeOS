using LifeOS.Application.Finance.Accounts.GetAccounts;

namespace LifeOS.Application.Finance.Accounts.UpdateAccount;

public enum UpdateAccountStatus
{
    Updated,
    NotFound
}

public sealed record UpdateAccountResult(UpdateAccountStatus Status, AccountSummary? Account)
{
    public static UpdateAccountResult Updated(AccountSummary account) => new(UpdateAccountStatus.Updated, account);

    public static UpdateAccountResult NotFound() => new(UpdateAccountStatus.NotFound, null);
}
