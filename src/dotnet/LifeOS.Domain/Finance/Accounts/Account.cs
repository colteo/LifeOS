namespace LifeOS.Domain.Finance.Accounts;

public sealed class Account
{
    private const int CurrencyCodeLength = 3;

    private Account(Guid id, Guid userId, string name, AccountType accountType, string currency, DateTimeOffset createdAtUtc)
    {
        Id = id;
        UserId = userId;
        Name = name;
        AccountType = accountType;
        Currency = currency;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; }

    // The owning LifeOS user.
    public Guid UserId { get; }

    public string Name { get; }

    public AccountType AccountType { get; }

    public string Currency { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public static Account Create(Guid userId, string name, AccountType accountType, string currency, DateTimeOffset createdAtUtc)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A valid user id is required.", nameof(userId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!Enum.IsDefined(accountType))
        {
            throw new ArgumentOutOfRangeException(nameof(accountType), accountType, "Account type is not supported.");
        }

        return new Account(
            Guid.CreateVersion7(),
            userId,
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
