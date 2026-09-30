using LifeOS.Application.Finance.Transactions.GetTransactions;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Transactions;

public class GetTransactionsHandlerTests
{
    private static readonly DateTimeOffset FromUtc = new(2026, 8, 31, 22, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ToUtc = new(2026, 9, 30, 22, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid AccountId = Guid.CreateVersion7();
    private static readonly Guid CategoryId = Guid.CreateVersion7();

    private readonly InMemoryTransactionRepository _repository = new();
    private readonly GetTransactionsHandler _handler;

    public GetTransactionsHandlerTests()
    {
        _handler = new GetTransactionsHandler(_repository);
    }

    [Fact]
    public async Task HandleAsync_IncludesFromUtcAndExcludesToUtc()
    {
        var atFrom = Add(FromUtc);
        Add(ToUtc);

        var result = await Handle();

        Assert.Equal(GetTransactionsStatus.Ok, result.Status);
        Assert.Equal(atFrom.Id, Assert.Single(result.Transactions).Id);
    }

    [Fact]
    public async Task HandleAsync_ExcludesTransactionsOutsideRange()
    {
        Add(FromUtc.AddTicks(-1));
        var inside = Add(FromUtc.AddDays(10));
        Add(ToUtc.AddTicks(1));

        var result = await Handle();

        Assert.Equal(inside.Id, Assert.Single(result.Transactions).Id);
    }

    [Fact]
    public async Task HandleAsync_WithNoTransactionsInRange_ReturnsEmptyList()
    {
        Add(FromUtc.AddDays(-5));

        var result = await Handle();

        Assert.Equal(GetTransactionsStatus.Ok, result.Status);
        Assert.Empty(result.Transactions);
    }

    [Fact]
    public async Task HandleAsync_OrdersByOccurredThenCreatedThenIdDescending()
    {
        var day10 = FromUtc.AddDays(10);
        var day20 = FromUtc.AddDays(20);

        var olderOccurrence = Add(day10);
        var sameOccurrenceCreatedEarlier = Add(day20, createdAtUtc: CreatedAtUtc);
        var sameOccurrenceCreatedLater = Add(day20, createdAtUtc: CreatedAtUtc.AddMinutes(5));
        var tieA = Add(day20, createdAtUtc: CreatedAtUtc);
        var tieB = Add(day20, createdAtUtc: CreatedAtUtc);

        var ties = new[] { sameOccurrenceCreatedEarlier, tieA, tieB }
            .OrderByDescending(transaction => transaction.Id)
            .Select(transaction => transaction.Id);

        var result = await Handle();

        Assert.Equal(
            new[] { sameOccurrenceCreatedLater.Id }.Concat(ties).Append(olderOccurrence.Id),
            result.Transactions.Select(transaction => transaction.Id));
    }

    [Fact]
    public async Task HandleAsync_MapsTransactionFields()
    {
        var transfer = Transaction.CreateTransfer(
            Guid.CreateVersion7(), Guid.CreateVersion7(), 100m, "EUR", FromUtc.AddDays(1), "Move to savings", CreatedAtUtc);
        _repository.Transactions.Add(transfer);

        var result = await Handle();

        var summary = Assert.Single(result.Transactions);
        Assert.Equal(TransactionType.Transfer, summary.TransactionType);
        Assert.Equal(100m, summary.Amount);
        Assert.Equal("EUR", summary.Currency);
        Assert.Equal(transfer.SourceAccountId, summary.SourceAccountId);
        Assert.Equal(transfer.DestinationAccountId, summary.DestinationAccountId);
        Assert.Null(summary.AccountId);
        Assert.Null(summary.CategoryId);
        Assert.Equal("Move to savings", summary.Note);
        Assert.Equal(transfer.OccurredAtUtc, summary.OccurredAtUtc);
        Assert.Equal(CreatedAtUtc, summary.CreatedAtUtc);
    }

    [Fact]
    public async Task HandleAsync_WithEqualBounds_ReturnsInvalid()
    {
        var result = await _handler.HandleAsync(new GetTransactionsQuery(FromUtc, FromUtc), CancellationToken.None);

        Assert.Equal(GetTransactionsStatus.Invalid, result.Status);
        Assert.Equal("toUtc", result.Field);
    }

    [Fact]
    public async Task HandleAsync_WithFromAfterTo_ReturnsInvalid()
    {
        var result = await _handler.HandleAsync(new GetTransactionsQuery(ToUtc, FromUtc), CancellationToken.None);

        Assert.Equal(GetTransactionsStatus.Invalid, result.Status);
        Assert.Equal("toUtc", result.Field);
    }

    [Fact]
    public async Task HandleAsync_WithNonUtcFrom_ReturnsInvalid()
    {
        var fromRome = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(2));

        var result = await _handler.HandleAsync(new GetTransactionsQuery(fromRome, ToUtc), CancellationToken.None);

        Assert.Equal(GetTransactionsStatus.Invalid, result.Status);
        Assert.Equal("fromUtc", result.Field);
    }

    [Fact]
    public async Task HandleAsync_WithNonUtcTo_ReturnsInvalid()
    {
        var toRome = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(2));

        var result = await _handler.HandleAsync(new GetTransactionsQuery(FromUtc, toRome), CancellationToken.None);

        Assert.Equal(GetTransactionsStatus.Invalid, result.Status);
        Assert.Equal("toUtc", result.Field);
    }

    private Task<GetTransactionsResult> Handle() =>
        _handler.HandleAsync(new GetTransactionsQuery(FromUtc, ToUtc), CancellationToken.None);

    private Transaction Add(DateTimeOffset occurredAtUtc, DateTimeOffset? createdAtUtc = null)
    {
        var transaction = Transaction.CreateExpense(
            AccountId, CategoryId, 10m, "EUR", occurredAtUtc, null, createdAtUtc ?? CreatedAtUtc);
        _repository.Transactions.Add(transaction);

        return transaction;
    }
}
