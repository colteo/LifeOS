using LifeOS.Domain.Finance.Transactions;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Transactions;

public class TransactionTests
{
    private static readonly DateTimeOffset OccurredAtUtc = new(2026, 9, 29, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 9, 29, 18, 5, 0, TimeSpan.Zero);

    private static readonly Guid AccountId = Guid.CreateVersion7();
    private static readonly Guid SourceAccountId = Guid.CreateVersion7();
    private static readonly Guid DestinationAccountId = Guid.CreateVersion7();
    private static readonly Guid CategoryId = Guid.CreateVersion7();

    // Ownership

    [Fact]
    public void AllFactories_SetOwningUser()
    {
        Assert.Equal(TestUsers.A, CreateAccountTransaction(TransactionType.Income, AccountId, CategoryId, 1m, "EUR").UserId);
        Assert.Equal(TestUsers.A, CreateAccountTransaction(TransactionType.Expense, AccountId, CategoryId, 1m, "EUR").UserId);
        Assert.Equal(
            TestUsers.A,
            Transaction.CreateTransfer(TestUsers.A, SourceAccountId, DestinationAccountId, 1m, "EUR", OccurredAtUtc, null, CreatedAtUtc).UserId);
    }

    [Fact]
    public void CreateIncome_WithEmptyUserId_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => Transaction.CreateIncome(
            Guid.Empty, AccountId, CategoryId, 1m, "EUR", OccurredAtUtc, null, CreatedAtUtc));

        Assert.Equal("userId", exception.ParamName);
    }

    [Fact]
    public void CreateExpense_WithEmptyUserId_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => Transaction.CreateExpense(
            Guid.Empty, AccountId, CategoryId, 1m, "EUR", OccurredAtUtc, null, CreatedAtUtc));

        Assert.Equal("userId", exception.ParamName);
    }

    [Fact]
    public void CreateTransfer_WithEmptyUserId_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => Transaction.CreateTransfer(
            Guid.Empty, SourceAccountId, DestinationAccountId, 1m, "EUR", OccurredAtUtc, null, CreatedAtUtc));

        Assert.Equal("userId", exception.ParamName);
    }

    // Income and Expense

    [Theory]
    [InlineData(TransactionType.Income)]
    [InlineData(TransactionType.Expense)]
    public void CreateAccountTransaction_WithValidInput_ReturnsTransaction(TransactionType type)
    {
        var transaction = CreateAccountTransaction(type, AccountId, CategoryId, 18.50m, "EUR", "Aperitivo");

        Assert.NotEqual(Guid.Empty, transaction.Id);
        Assert.Equal(type, transaction.TransactionType);
        Assert.Equal(18.50m, transaction.Amount);
        Assert.Equal("EUR", transaction.Currency);
        Assert.Equal(AccountId, transaction.AccountId);
        Assert.Equal(CategoryId, transaction.CategoryId);
        Assert.Null(transaction.SourceAccountId);
        Assert.Null(transaction.DestinationAccountId);
        Assert.Equal("Aperitivo", transaction.Note);
        Assert.Equal(OccurredAtUtc, transaction.OccurredAtUtc);
        Assert.Equal(CreatedAtUtc, transaction.CreatedAtUtc);
    }

    [Theory]
    [InlineData(TransactionType.Income, 0)]
    [InlineData(TransactionType.Income, -1)]
    [InlineData(TransactionType.Expense, 0)]
    [InlineData(TransactionType.Expense, -0.01)]
    public void CreateAccountTransaction_WithNonPositiveAmount_Throws(TransactionType type, double amount)
    {
        var exception = Assert.ThrowsAny<ArgumentException>(
            () => CreateAccountTransaction(type, AccountId, CategoryId, (decimal)amount));

        Assert.Equal("amount", exception.ParamName);
    }

    [Theory]
    [InlineData(TransactionType.Income)]
    [InlineData(TransactionType.Expense)]
    public void CreateAccountTransaction_WithEmptyAccountId_Throws(TransactionType type)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CreateAccountTransaction(type, Guid.Empty, CategoryId));

        Assert.Equal("accountId", exception.ParamName);
    }

    [Theory]
    [InlineData(TransactionType.Income)]
    [InlineData(TransactionType.Expense)]
    public void CreateAccountTransaction_WithEmptyCategoryId_Throws(TransactionType type)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CreateAccountTransaction(type, AccountId, Guid.Empty));

        Assert.Equal("categoryId", exception.ParamName);
    }

    // Transfer

    [Fact]
    public void CreateTransfer_WithValidInput_ReturnsSingleTransferTransaction()
    {
        var transaction = Transaction.CreateTransfer(
            TestUsers.A,
            SourceAccountId, DestinationAccountId, 100m, "EUR", OccurredAtUtc, "Move to savings", CreatedAtUtc);

        Assert.Equal(TransactionType.Transfer, transaction.TransactionType);
        Assert.Equal(100m, transaction.Amount);
        Assert.Equal(SourceAccountId, transaction.SourceAccountId);
        Assert.Equal(DestinationAccountId, transaction.DestinationAccountId);
        Assert.Null(transaction.AccountId);
        Assert.Null(transaction.CategoryId);
        Assert.Equal("Move to savings", transaction.Note);
    }

    [Fact]
    public void CreateTransfer_WithEmptySource_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => Transaction.CreateTransfer(
            TestUsers.A,
            Guid.Empty, DestinationAccountId, 100m, "EUR", OccurredAtUtc, null, CreatedAtUtc));

        Assert.Equal("sourceAccountId", exception.ParamName);
    }

    [Fact]
    public void CreateTransfer_WithEmptyDestination_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => Transaction.CreateTransfer(
            TestUsers.A,
            SourceAccountId, Guid.Empty, 100m, "EUR", OccurredAtUtc, null, CreatedAtUtc));

        Assert.Equal("destinationAccountId", exception.ParamName);
    }

    [Fact]
    public void CreateTransfer_WithSameSourceAndDestination_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => Transaction.CreateTransfer(
            TestUsers.A,
            SourceAccountId, SourceAccountId, 100m, "EUR", OccurredAtUtc, null, CreatedAtUtc));

        Assert.Equal("destinationAccountId", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void CreateTransfer_WithNonPositiveAmount_Throws(double amount)
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() => Transaction.CreateTransfer(
            TestUsers.A,
            SourceAccountId, DestinationAccountId, (decimal)amount, "EUR", OccurredAtUtc, null, CreatedAtUtc));

        Assert.Equal("amount", exception.ParamName);
    }

    // Common rules

    [Theory]
    [InlineData("0.0001")]
    [InlineData("25.50")]
    [InlineData("25.5000")]
    [InlineData("999999999999999.9999")]
    public void Create_WithSupportedAmount_Succeeds(string amount)
    {
        var value = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);

        var transaction = CreateAccountTransaction(TransactionType.Expense, AccountId, CategoryId, value);

        Assert.Equal(value, transaction.Amount);
    }

    [Theory]
    [InlineData("0.00001")]
    [InlineData("25.12345")]
    public void Create_WithMoreThanFourDecimalPlaces_Throws(string amount)
    {
        var value = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);

        var exception = Assert.Throws<ArgumentException>(
            () => CreateAccountTransaction(TransactionType.Expense, AccountId, CategoryId, value));

        Assert.Equal("amount", exception.ParamName);
    }

    [Fact]
    public void Create_WithAmountAboveSupportedRange_Throws()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => CreateAccountTransaction(
            TransactionType.Expense, AccountId, CategoryId, Transaction.MaxAmount + 0.0001m));

        Assert.Equal("amount", exception.ParamName);
    }

    [Fact]
    public void Create_NormalizesCurrency()
    {
        var transaction = CreateAccountTransaction(TransactionType.Income, AccountId, CategoryId, currency: " eur ");

        Assert.Equal("EUR", transaction.Currency);
    }

    [Theory]
    [InlineData("")]
    [InlineData("EU")]
    [InlineData("EURO")]
    [InlineData("E1R")]
    public void Create_WithInvalidCurrency_Throws(string currency)
    {
        var exception = Assert.ThrowsAny<ArgumentException>(
            () => CreateAccountTransaction(TransactionType.Income, AccountId, CategoryId, currency: currency));

        Assert.Equal("currency", exception.ParamName);
    }

    [Fact]
    public void Create_NormalizesTimestampsToUtc()
    {
        var occurredAt = new DateTimeOffset(2026, 9, 29, 20, 0, 0, TimeSpan.FromHours(2));
        var createdAt = new DateTimeOffset(2026, 9, 29, 20, 5, 0, TimeSpan.FromHours(2));

        var transaction = Transaction.CreateTransfer(
            TestUsers.A,
            SourceAccountId, DestinationAccountId, 100m, "EUR", occurredAt, null, createdAt);

        Assert.Equal(TimeSpan.Zero, transaction.OccurredAtUtc.Offset);
        Assert.Equal(occurredAt.UtcTicks, transaction.OccurredAtUtc.UtcTicks);
        Assert.Equal(TimeSpan.Zero, transaction.CreatedAtUtc.Offset);
        Assert.Equal(createdAt.UtcTicks, transaction.CreatedAtUtc.UtcTicks);
    }

    [Fact]
    public void Create_TrimsNote()
    {
        var transaction = CreateAccountTransaction(TransactionType.Expense, AccountId, CategoryId, note: "  Aperitivo  ");

        Assert.Equal("Aperitivo", transaction.Note);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankNote_StoresNull(string? note)
    {
        var transaction = CreateAccountTransaction(TransactionType.Expense, AccountId, CategoryId, note: note);

        Assert.Null(transaction.Note);
    }

    // ---- Editing: the type and the shape never change ----

    [Theory]
    [InlineData(TransactionType.Expense)]
    [InlineData(TransactionType.Income)]
    public void UpdateAccountTransaction_ChangesTheEditableFields_KeepingTypeIdOwnerAndCreation(TransactionType type)
    {
        var transaction = CreateAccountTransaction(type, AccountId, CategoryId, note: "Lunch");
        var (id, newAccount, newCategory) = (transaction.Id, Guid.CreateVersion7(), Guid.CreateVersion7());
        var newTime = new DateTimeOffset(2026, 9, 1, 9, 30, 0, TimeSpan.FromHours(2));

        transaction.UpdateAccountTransaction(newAccount, newCategory, 20.25m, "usd", newTime, "  Dinner  ");

        Assert.Equal((id, TestUsers.A, type, CreatedAtUtc), (transaction.Id, transaction.UserId, transaction.TransactionType, transaction.CreatedAtUtc));
        Assert.Equal((newAccount, newCategory), (transaction.AccountId!.Value, transaction.CategoryId!.Value));
        Assert.Null(transaction.SourceAccountId);
        Assert.Null(transaction.DestinationAccountId);
        Assert.Equal((20.25m, "USD", "Dinner"), (transaction.Amount, transaction.Currency, transaction.Note));
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 7, 30, 0, TimeSpan.Zero), transaction.OccurredAtUtc);
        Assert.Equal(TimeSpan.Zero, transaction.OccurredAtUtc.Offset);
    }

    [Fact]
    public void UpdateTransfer_ChangesTheEditableFields_KeepingTheTransferShape()
    {
        var transfer = Transaction.CreateTransfer(TestUsers.A, SourceAccountId, DestinationAccountId, 100m, "EUR", OccurredAtUtc, null, CreatedAtUtc);
        var (newSource, newDestination) = (Guid.CreateVersion7(), Guid.CreateVersion7());

        transfer.UpdateTransfer(newSource, newDestination, 75m, "EUR", OccurredAtUtc.AddDays(-1), "   ");

        Assert.Equal(TransactionType.Transfer, transfer.TransactionType);
        Assert.Equal((newSource, newDestination), (transfer.SourceAccountId!.Value, transfer.DestinationAccountId!.Value));
        Assert.Null(transfer.AccountId);
        Assert.Null(transfer.CategoryId);
        Assert.Equal((75m, OccurredAtUtc.AddDays(-1)), (transfer.Amount, transfer.OccurredAtUtc));
        Assert.Null(transfer.Note);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.00001)]
    public void Update_WithAnInvalidAmount_ThrowsAndChangesNothing(decimal amount)
    {
        var expense = CreateAccountTransaction(TransactionType.Expense, AccountId, CategoryId, note: "Lunch");
        var transfer = Transaction.CreateTransfer(TestUsers.A, SourceAccountId, DestinationAccountId, 100m, "EUR", OccurredAtUtc, null, CreatedAtUtc);

        Assert.ThrowsAny<ArgumentException>(() => expense.UpdateAccountTransaction(Guid.CreateVersion7(), Guid.CreateVersion7(), amount, "USD", CreatedAtUtc, "x"));
        Assert.ThrowsAny<ArgumentException>(() => transfer.UpdateTransfer(Guid.CreateVersion7(), Guid.CreateVersion7(), amount, "EUR", CreatedAtUtc, "x"));

        Assert.Equal((AccountId, 18.50m, "EUR", "Lunch"), (expense.AccountId!.Value, expense.Amount, expense.Currency, expense.Note));
        Assert.Equal((SourceAccountId, 100m), (transfer.SourceAccountId!.Value, transfer.Amount));
    }

    [Fact]
    public void UpdateTransfer_ToTheSameAccount_ThrowsAndChangesNothing()
    {
        var transfer = Transaction.CreateTransfer(TestUsers.A, SourceAccountId, DestinationAccountId, 100m, "EUR", OccurredAtUtc, null, CreatedAtUtc);
        var same = Guid.CreateVersion7();

        var exception = Assert.Throws<ArgumentException>(() => transfer.UpdateTransfer(same, same, 5m, "EUR", OccurredAtUtc, null));

        Assert.Equal("destinationAccountId", exception.ParamName);
        Assert.Equal(DestinationAccountId, transfer.DestinationAccountId);
    }

    [Fact]
    public void TheWrongUpdateForTheType_IsRejected_SoTheShapeCannotChange()
    {
        var expense = CreateAccountTransaction(TransactionType.Expense, AccountId, CategoryId);
        var transfer = Transaction.CreateTransfer(TestUsers.A, SourceAccountId, DestinationAccountId, 100m, "EUR", OccurredAtUtc, null, CreatedAtUtc);

        Assert.Throws<InvalidOperationException>(() => expense.UpdateTransfer(SourceAccountId, DestinationAccountId, 5m, "EUR", OccurredAtUtc, null));
        Assert.Throws<InvalidOperationException>(() => transfer.UpdateAccountTransaction(AccountId, CategoryId, 5m, "EUR", OccurredAtUtc, null));

        Assert.Equal((TransactionType.Expense, (Guid?)null), (expense.TransactionType, expense.SourceAccountId));
        Assert.Equal((TransactionType.Transfer, (Guid?)null), (transfer.TransactionType, transfer.CategoryId));
    }

    private static Transaction CreateAccountTransaction(
        TransactionType type,
        Guid accountId,
        Guid categoryId,
        decimal amount = 18.50m,
        string currency = "EUR",
        string? note = null) =>
        type == TransactionType.Income
            ? Transaction.CreateIncome(TestUsers.A, accountId, categoryId, amount, currency, OccurredAtUtc, note, CreatedAtUtc)
            : Transaction.CreateExpense(TestUsers.A, accountId, categoryId, amount, currency, OccurredAtUtc, note, CreatedAtUtc);
}
