namespace LifeOS.Domain.Finance.Transactions;

public sealed class Transaction
{
    // The monetary range supported by LifeOS (persisted as numeric(19,4)).
    public const decimal MaxAmount = 999_999_999_999_999.9999m;
    public const int MaxDecimalPlaces = 4;

    private const int CurrencyCodeLength = 3;

    private Transaction(
        Guid id,
        Guid userId,
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
        UserId = userId;
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

    // The owning LifeOS user. Referenced accounts and categories must belong to the same user;
    // Application verifies that through user-scoped lookups.
    public Guid UserId { get; }

    // Immutable: changing the type means deleting the transaction and creating another one.
    public TransactionType TransactionType { get; }

    // Always positive: the transaction type determines the balance effect.
    public decimal Amount { get; private set; }

    public string Currency { get; private set; }

    public Guid? AccountId { get; private set; }

    public Guid? SourceAccountId { get; private set; }

    public Guid? DestinationAccountId { get; private set; }

    public Guid? CategoryId { get; private set; }

    public string? Note { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    // The effect of this transaction on the balance of the given account (ADR-007):
    // income adds and expense subtracts on its account; a transfer subtracts from the source
    // and adds to the destination; any other account is unaffected.
    public decimal EffectOn(Guid accountId) => TransactionType switch
    {
        TransactionType.Income when AccountId == accountId => Amount,
        TransactionType.Expense when AccountId == accountId => -Amount,
        TransactionType.Transfer when DestinationAccountId == accountId => Amount,
        TransactionType.Transfer when SourceAccountId == accountId => -Amount,
        _ => 0m
    };

    public static Transaction CreateIncome(
        Guid userId,
        Guid accountId,
        Guid categoryId,
        decimal amount,
        string currency,
        DateTimeOffset occurredAtUtc,
        string? note,
        DateTimeOffset createdAtUtc) =>
        CreateAccountTransaction(
            userId, TransactionType.Income, accountId, categoryId, amount, currency, occurredAtUtc, note, createdAtUtc);

    public static Transaction CreateExpense(
        Guid userId,
        Guid accountId,
        Guid categoryId,
        decimal amount,
        string currency,
        DateTimeOffset occurredAtUtc,
        string? note,
        DateTimeOffset createdAtUtc) =>
        CreateAccountTransaction(
            userId, TransactionType.Expense, accountId, categoryId, amount, currency, occurredAtUtc, note, createdAtUtc);

    public static Transaction CreateTransfer(
        Guid userId,
        Guid sourceAccountId,
        Guid destinationAccountId,
        decimal amount,
        string currency,
        DateTimeOffset occurredAtUtc,
        string? note,
        DateTimeOffset createdAtUtc)
    {
        EnsureNotEmpty(userId, nameof(userId));
        EnsureNotEmpty(sourceAccountId, nameof(sourceAccountId));
        EnsureNotEmpty(destinationAccountId, nameof(destinationAccountId));

        EnsureDifferentAccounts(sourceAccountId, destinationAccountId);

        return new Transaction(
            Guid.CreateVersion7(),
            userId,
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

    // Edits an Income or Expense, keeping its type and shape (account + category). Every value is
    // validated before anything changes. The caller resolves the account (and so the currency) and
    // a category of the matching type, as for creation.
    public void UpdateAccountTransaction(
        Guid accountId,
        Guid categoryId,
        decimal amount,
        string currency,
        DateTimeOffset occurredAtUtc,
        string? note)
    {
        if (TransactionType == TransactionType.Transfer)
        {
            throw new InvalidOperationException("A transfer is edited with UpdateTransfer.");
        }

        EnsureNotEmpty(accountId, nameof(accountId));
        EnsureNotEmpty(categoryId, nameof(categoryId));
        var validatedAmount = ValidateAmount(amount);
        var normalizedCurrency = NormalizeCurrency(currency);

        AccountId = accountId;
        CategoryId = categoryId;
        Amount = validatedAmount;
        Currency = normalizedCurrency;
        OccurredAtUtc = occurredAtUtc.ToUniversalTime();
        Note = NormalizeNote(note);
    }

    // Edits a Transfer, keeping its type and shape (source + destination, no category).
    public void UpdateTransfer(
        Guid sourceAccountId,
        Guid destinationAccountId,
        decimal amount,
        string currency,
        DateTimeOffset occurredAtUtc,
        string? note)
    {
        if (TransactionType != TransactionType.Transfer)
        {
            throw new InvalidOperationException("Income and expenses are edited with UpdateAccountTransaction.");
        }

        EnsureNotEmpty(sourceAccountId, nameof(sourceAccountId));
        EnsureNotEmpty(destinationAccountId, nameof(destinationAccountId));
        EnsureDifferentAccounts(sourceAccountId, destinationAccountId);
        var validatedAmount = ValidateAmount(amount);
        var normalizedCurrency = NormalizeCurrency(currency);

        SourceAccountId = sourceAccountId;
        DestinationAccountId = destinationAccountId;
        Amount = validatedAmount;
        Currency = normalizedCurrency;
        OccurredAtUtc = occurredAtUtc.ToUniversalTime();
        Note = NormalizeNote(note);
    }

    private static Transaction CreateAccountTransaction(
        Guid userId,
        TransactionType transactionType,
        Guid accountId,
        Guid categoryId,
        decimal amount,
        string currency,
        DateTimeOffset occurredAtUtc,
        string? note,
        DateTimeOffset createdAtUtc)
    {
        EnsureNotEmpty(userId, nameof(userId));
        EnsureNotEmpty(accountId, nameof(accountId));
        EnsureNotEmpty(categoryId, nameof(categoryId));

        return new Transaction(
            Guid.CreateVersion7(),
            userId,
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

    private static void EnsureDifferentAccounts(Guid sourceAccountId, Guid destinationAccountId)
    {
        if (sourceAccountId == destinationAccountId)
        {
            throw new ArgumentException(
                "The destination account must differ from the source account.",
                nameof(destinationAccountId));
        }
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
