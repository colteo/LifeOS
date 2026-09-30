namespace LifeOS.Contracts.Finance.Accounts;

// The editable fields of an account. The currency is immutable and cannot be changed.
public sealed record UpdateAccountRequest(
    string Name,
    string Type);
