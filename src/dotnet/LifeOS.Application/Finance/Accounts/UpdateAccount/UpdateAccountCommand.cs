using LifeOS.Domain.Finance.Accounts;

namespace LifeOS.Application.Finance.Accounts.UpdateAccount;

// The editable fields of an account. The currency is immutable and not part of the command.
public sealed record UpdateAccountCommand(Guid AccountId, string Name, AccountType AccountType);
