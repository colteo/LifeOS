namespace LifeOS.Domain.Finance.Transactions;

public sealed class Transaction
{
    // The monetary range supported by LifeOS (persisted as numeric(19,4)).
    public const decimal MaxAmount = 999_999_999_999_999.9999m;
    public const int MaxDecimalPlaces = 4;

    private const int CurrencyCodeLength = 3;

    private Transaction(
        Guid id,
        TransactionType transactionType,
        decimal amount,
        string currency,
        Guid? accountId,
        Guid? sourceAccountId,
        Guid? destinationAccountId,
        Guid? categoryId,
        string? note,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        TransactionType = transactionType;
        Amount = amount;
        Currency = currency;
        AccountId = accountId;
        SourceAccountId = sourceAccountId;
        DestinationAccountId = destinationAccountId;
        CategoryId = categoryId;
        Note = note;
        OccurredAtUtc = occurredAtUtc;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; }

    public TransactionType TransactionType { get; }

    // Always positive: the transaction type determines the balance effect.
    public decimal Amount { get; }

    public string Currency { get; }

    public Guid? AccountId { get; }

    public Guid? SourceAccountId { get; }

    public Guid? DestinationAccountId { get; }

    public Guid? CategoryId { get; }

    public string? Note { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public static Transaction CreateIncome(
        Guid accountId,
        Guid categoryId,
        decimal amount,
        string currency,
        DateTimeOffset occurredAtUtc,
        string? note,
        DateTimeOffset createdAtUtc) =>
        CreateAccountTransaction(
            TransactionType.Income, accountId, categoryId, amount, currency, occurredAtUtc, note, createdAtUtc);

    public static Transaction CreateExpense(
        Guid accountId,
        Guid categoryId,
        decimal amount,
        string currency,
        DateTimeOffset occurredAtUtc,
        string? note,
        DateTimeOffset createdAtUtc) =>
        CreateAccountTransaction(
            TransactionType.Expense, accountId, categoryId, amount, currency, occurredAtUtc, note, createdAtUtc);

    public static Transaction CreateTransfer(
        Guid sourceAccountId,
        Guid destinationAccountId,
        decimal amount,
        string currency,
        DateTimeOffset occurredAtUtc,
        string? note,
        DateTimeOffset createdAtUtc)
    {
        EnsureNotEmpty(sourceAccountId, nameof(sourceAccountId));
        EnsureNotEmpty(destinationAccountId, nameof(destinationAccountId));

        if (sourceAccountId == destinationAccountId)
        {
            throw new ArgumentException(
                "The destination account must differ from the source account.",
                nameof(destinationAccountId));
        }

        return new Transaction(
            Guid.CreateVersion7(),
            TransactionType.Transfer,
            ValidateAmount(amount),
            NormalizeCurrency(currency),
            accountId: null,
            sourceAccountId,
            destinationAccountId,
            categoryId: null,
            NormalizeNote(note),
            occurredAtUtc.ToUniversalTime(),
            createdAtUtc.ToUniversalTime());
    }

    private static Transaction CreateAccountTransaction(
        TransactionType transactionType,
        Guid accountId,
        Guid categoryId,
        decimal amount,
        string currency,
        DateTimeOffset occurredAtUtc,
        string? note,
        DateTimeOffset createdAtUtc)
    {
        EnsureNotEmpty(accountId, nameof(accountId));
        EnsureNotEmpty(categoryId, nameof(categoryId));

        return new Transaction(
            Guid.CreateVersion7(),
            transactionType,
            ValidateAmount(amount),
            NormalizeCurrency(currency),
            accountId,
            sourceAccountId: null,
            destinationAccountId: null,
            categoryId,
            NormalizeNote(note),
            occurredAtUtc.ToUniversalTime(),
            createdAtUtc.ToUniversalTime());
    }

    private static void EnsureNotEmpty(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A valid id is required.", parameterName);
        }
    }

    private static decimal ValidateAmount(decimal amount)
    {
        if (amount <= 0 || amount > MaxAmount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                amount,
                $"Amount must be greater than 0 and at most {MaxAmount}.");
        }

        // Reject excess precision instead of letting the database round it.
        if (decimal.Round(amount, MaxDecimalPlaces) != amount)
        {
            throw new ArgumentException(
                $"Amount must have at most {MaxDecimalPlaces} decimal places.",
                nameof(amount));
        }

        return amount;
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

    private static string? NormalizeNote(string? note) =>
        string.IsNullOrWhiteSpace(note) ? null : note.Trim();
}
