using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Domain.Finance.Budgets;

public sealed class MonthlyBudget
{
    private MonthlyBudget(Guid id, Guid userId, int year, int month, string currency, decimal amount)
    {
        Id = id;
        UserId = userId;
        Year = year;
        Month = month;
        Currency = currency;
        Amount = amount;
    }

    public Guid Id { get; }
    public Guid UserId { get; }
    public int Year { get; }
    public int Month { get; }
    public string Currency { get; }
    public decimal Amount { get; }

    public static MonthlyBudget Create(Guid userId, int year, int month, string currency, decimal amount)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("A valid user id is required.", nameof(userId));
        ValidateMonth(year, month);
        if (amount <= 0 || amount > Transaction.MaxAmount)
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be positive and within the supported monetary range.");
        if (decimal.Round(amount, Transaction.MaxDecimalPlaces) != amount)
            throw new ArgumentException("Amount must have at most 4 decimal places.", nameof(amount));
        return new MonthlyBudget(Guid.CreateVersion7(), userId, year, month, NormalizeCurrency(currency), amount);
    }

    public static void ValidateMonth(int year, int month)
    {
        // The upper bound allows constructing the following month's exclusive boundary.
        if (year is < 1 or > 9998)
            throw new ArgumentOutOfRangeException(nameof(year), "Year must be between 1 and 9998.");
        if (month is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(month), "Month must be between 1 and 12.");
    }

    public static string NormalizeCurrency(string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        var normalized = currency.Trim().ToUpperInvariant();
        if (normalized.Length != 3 || !normalized.All(char.IsAsciiLetterUpper))
            throw new ArgumentException("Currency must be a 3-letter ISO-style code.", nameof(currency));
        return normalized;
    }
}
