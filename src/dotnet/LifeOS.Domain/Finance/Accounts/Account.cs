namespace LifeOS.Domain.Finance.Accounts;

public sealed class Account
{
    private const int CurrencyCodeLength = 3;

    private Account(Guid id, string name, AccountType accountType, string currency, DateTimeOffset createdAtUtc)
    {
        Id = id;
        Name = name;
        AccountType = accountType;
        Currency = currency;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; }

    public string Name { get; }

    public AccountType AccountType { get; }

    public string Currency { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public static Account Create(string name, AccountType accountType, string currency, DateTimeOffset createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!Enum.IsDefined(accountType))
        {
            throw new ArgumentOutOfRangeException(nameof(accountType), accountType, "Account type is not supported.");
        }

        return new Account(
            Guid.CreateVersion7(),
            name.Trim(),
            accountType,
            NormalizeCurrency(currency),
            createdAtUtc.ToUniversalTime());
    }

    private static string NormalizeCurrency(string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        var normalized = currency.Trim().ToUpperInvariant();

        if (normalized.Length != CurrencyCodeLength || !normalized.All(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException("Currency must be a 3-letter ISO-style code.", nameof(currency));
        }

        return normalized;
    }
}
