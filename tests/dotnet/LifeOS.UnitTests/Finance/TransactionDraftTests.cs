using System.Globalization;
using LifeOS.App.Services.Finance;
using LifeOS.Contracts.Finance.Accounts;
using LifeOS.Contracts.Finance.Transactions;

namespace LifeOS.UnitTests.Finance;

public class TransactionDraftTests
{
    private static readonly TimeZoneInfo UtcPlusTwo = TimeZoneInfo.CreateCustomTimeZone("Test/UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");
    private static readonly Guid Account = Guid.CreateVersion7();
    private static readonly Guid OtherAccount = Guid.CreateVersion7();
    private static readonly Guid Category = Guid.CreateVersion7();

    // ---- New (quick entry) ----

    [Fact]
    public void NewExpense_BuildsTheCreateRequest_InterpretingTheLocalTime()
    {
        var draft = TransactionDraft.ForNew(TransactionDraft.Expense, new DateTime(2026, 9, 30, 13, 10, 45));
        draft.Amount = "12,50";
        draft.AccountId = Account.ToString();
        draft.CategoryId = Category.ToString();
        draft.Note = "  Lunch  ";

        Assert.True(draft.TryBuild(UtcPlusTwo, out var values, out _));
        var request = draft.ToCreateRequest(values);

        Assert.Equal(
            new CreateTransactionRequest("Expense", 12.5m, Account, null, null, Category, new DateTimeOffset(2026, 9, 30, 11, 10, 0, TimeSpan.Zero), "Lunch"),
            request);
    }

    [Fact]
    public void MissingFields_GiveTheExistingMessages()
    {
        var expense = TransactionDraft.ForNew(TransactionDraft.Expense, new DateTime(2026, 9, 30, 13, 10, 0));
        var transfer = TransactionDraft.ForNew(TransactionDraft.Transfer, new DateTime(2026, 9, 30, 13, 10, 0));
        transfer.Amount = "0";

        Assert.False(expense.TryBuild(UtcPlusTwo, out _, out var expenseErrors));
        Assert.False(transfer.TryBuild(UtcPlusTwo, out _, out var transferErrors));

        Assert.Equal(["Enter an amount greater than zero, e.g. 12,50.", "Select an account.", "Select a category."], expenseErrors);
        Assert.Equal(
            ["Enter an amount greater than zero, e.g. 12,50.", "Select the account to transfer from.", "Select the account to transfer to."],
            transferErrors);
    }

    [Theory]
    [InlineData("2026-09-30T13:10:59.1234567", "2026-09-30T13:10")]
    [InlineData("2026-09-30T13:10:59", "2026-09-30T13:10")]
    [InlineData("not a date", "not a date")]
    public void PickerValues_AreStoredAtMinutePrecision(string raw, string stored)
    {
        var draft = TransactionDraft.ForNew(TransactionDraft.Expense, new DateTime(2026, 9, 30, 13, 10, 0));

        draft.SetOccurredAtFromPicker(raw);

        Assert.Equal(stored, draft.OccurredAtLocalText);
    }

    [Fact]
    public void ChangeType_ClearsTheCategoryAndTheOtherShapesAccounts()
    {
        var draft = TransactionDraft.ForNew(TransactionDraft.Expense, DateTime.Now);
        draft.AccountId = Account.ToString();
        draft.CategoryId = Category.ToString();

        draft.ChangeType(TransactionDraft.Transfer);
        Assert.Equal(("", ""), (draft.AccountId, draft.CategoryId));

        draft.SourceAccountId = Account.ToString();
        draft.DestinationAccountId = OtherAccount.ToString();
        draft.ChangeType(TransactionDraft.Income);
        Assert.Equal(("", ""), (draft.SourceAccountId, draft.DestinationAccountId));
    }

    [Fact]
    public void ClearForNextEntry_KeepsTypeAndAccount_ClearsTheRest()
    {
        var draft = TransactionDraft.ForNew(TransactionDraft.Expense, new DateTime(2026, 9, 30, 9, 0, 0));
        draft.Amount = "5";
        draft.AccountId = Account.ToString();
        draft.CategoryId = Category.ToString();
        draft.Note = "x";

        draft.ClearForNextEntry(new DateTime(2026, 9, 30, 13, 10, 0));

        Assert.Equal((TransactionDraft.Expense, Account.ToString(), "", "", "", "2026-09-30T13:10"),
            (draft.Type, draft.AccountId, draft.CategoryId, draft.Amount, draft.Note, draft.OccurredAtLocalText));
    }

    [Fact]
    public void CurrencyMismatch_OnlyForTransfersBetweenDifferentCurrencies()
    {
        AccountResponse[] accounts =
        [
            new(Account, "Checking", "BankAccount", "EUR", DateTimeOffset.UtcNow),
            new(OtherAccount, "Dollars", "BankAccount", "USD", DateTimeOffset.UtcNow)
        ];
        var draft = TransactionDraft.ForNew(TransactionDraft.Transfer, DateTime.Now);
        draft.SourceAccountId = Account.ToString();
        draft.DestinationAccountId = OtherAccount.ToString();

        Assert.True(draft.HasCurrencyMismatch(accounts));

        draft.ChangeType(TransactionDraft.Expense);
        Assert.False(draft.HasCurrencyMismatch(accounts));
    }

    // ---- Edit ----

    [Fact]
    public void From_PrefillsTheStoredTransaction_InTheDeviceCultureAndLocalTime()
    {
        var stored = Expense(1234.5m, new DateTimeOffset(2026, 9, 30, 11, 10, 42, TimeSpan.Zero).AddTicks(1230), "Lunch");

        var draft = TransactionDraft.From(stored, UtcPlusTwo, Italian);

        Assert.Equal(("Expense", "1234,50", Account.ToString(), Category.ToString(), "Lunch", "2026-09-30T13:10"),
            (draft.Type, draft.Amount, draft.AccountId, draft.CategoryId, draft.Note, draft.OccurredAtLocalText));
    }

    [Fact]
    public void UntouchedDate_SendsTheStoredInstantExactly()
    {
        var instant = new DateTimeOffset(2026, 9, 30, 11, 10, 42, TimeSpan.Zero).AddTicks(1230);
        var draft = TransactionDraft.From(Expense(18.5m, instant, null), UtcPlusTwo, Italian);
        draft.Amount = "20";

        Assert.True(draft.TryBuild(UtcPlusTwo, out var values, out _));

        Assert.Equal(instant, values.OccurredAtUtc);
    }

    [Fact]
    public void ChangedDate_IsParsedAgain_AtMinutePrecision()
    {
        var instant = new DateTimeOffset(2026, 9, 30, 11, 10, 42, TimeSpan.Zero);
        var draft = TransactionDraft.From(Expense(18.5m, instant, null), UtcPlusTwo, Italian);

        draft.SetOccurredAtFromPicker("2026-09-29T20:00");

        Assert.True(draft.TryBuild(UtcPlusTwo, out var values, out _));
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 18, 0, 0, TimeSpan.Zero), values.OccurredAtUtc);
    }

    [Fact]
    public void UpdateRequest_HasExactlyTheBranchOfTheStoredType()
    {
        var expense = TransactionDraft.From(Expense(18.5m, DateTimeOffset.UtcNow, "Lunch"), UtcPlusTwo, Italian);
        var transfer = TransactionDraft.From(
            new TransactionResponse(Guid.CreateVersion7(), "Transfer", 100m, "EUR", null, Account, OtherAccount, null, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow),
            UtcPlusTwo,
            Italian);

        Assert.True(expense.TryBuild(UtcPlusTwo, out var expenseValues, out _));
        Assert.True(transfer.TryBuild(UtcPlusTwo, out var transferValues, out _));
        var expenseRequest = expense.ToUpdateRequest(expenseValues);
        var transferRequest = transfer.ToUpdateRequest(transferValues);

        Assert.Equal(new AccountTransactionUpdate(Account, Category), expenseRequest.AccountTransaction);
        Assert.Null(expenseRequest.Transfer);
        Assert.Equal(("Lunch", 18.5m), (expenseRequest.Note, expenseRequest.Amount));
        Assert.Equal(new TransferUpdate(Account, OtherAccount), transferRequest.Transfer);
        Assert.Null(transferRequest.AccountTransaction);
    }

    private static TransactionResponse Expense(decimal amount, DateTimeOffset occurredAtUtc, string? note) =>
        new(Guid.CreateVersion7(), "Expense", amount, "EUR", Account, null, null, Category, occurredAtUtc, note, DateTimeOffset.UtcNow);
}
