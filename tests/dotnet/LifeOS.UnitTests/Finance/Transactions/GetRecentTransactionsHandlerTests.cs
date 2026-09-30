using LifeOS.Application.Finance.Transactions.GetRecentTransactions;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Transactions;

public class GetRecentTransactionsHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 18, 0, 0, TimeSpan.Zero);

    private readonly InMemoryTransactionRepository _transactions = new();
    private readonly GetRecentTransactionsHandler _handler;
    private readonly Guid _accountA = Guid.CreateVersion7();
    private readonly Guid _accountB = Guid.CreateVersion7();
    private readonly Guid _category = Guid.CreateVersion7();

    public GetRecentTransactionsHandlerTests()
    {
        _handler = new GetRecentTransactionsHandler(_transactions);
    }

    [Fact]
    public async Task ReturnsOnlyTheCallersTransactions()
    {
        var ofA = Add(TestUsers.A, _accountA, Now);
        Add(TestUsers.B, _accountB, Now.AddMinutes(1));

        var result = await RecentAsync(TestUsers.A, 5);

        Assert.Equal(ofA.Id, Assert.Single(result.Transactions).Id);
    }

    [Fact]
    public async Task WithoutTransactions_ReturnsEmpty()
    {
        var result = await RecentAsync(TestUsers.A, 5);

        Assert.Equal(GetRecentTransactionsStatus.Ok, result.Status);
        Assert.Empty(result.Transactions);
    }

    [Fact]
    public async Task ReturnsTheNewestUpToTheLimit()
    {
        for (var hours = 0; hours < 7; hours++)
        {
            Add(TestUsers.A, _accountA, Now.AddHours(-hours));
        }

        var result = await RecentAsync(TestUsers.A, 5);

        Assert.Equal(5, result.Transactions.Count);
        Assert.Equal(Now, result.Transactions[0].OccurredAtUtc);
        Assert.Equal(Now.AddHours(-4), result.Transactions[^1].OccurredAtUtc);
    }

    [Fact]
    public async Task OrdersByOccurredThenCreatedThenId_AllDescending()
    {
        var older = Add(TestUsers.A, _accountA, Now.AddHours(-1), createdAt: Now);
        var sameTimeCreatedEarlier = Add(TestUsers.A, _accountA, Now, createdAt: Now.AddMinutes(1));
        var sameTimeCreatedLater = Add(TestUsers.A, _accountA, Now, createdAt: Now.AddMinutes(2));
        var tieA = Add(TestUsers.A, _accountA, Now.AddHours(-2), createdAt: Now);
        var tieB = Add(TestUsers.A, _accountA, Now.AddHours(-2), createdAt: Now);
        var (higherId, lowerId) = tieA.Id.CompareTo(tieB.Id) > 0 ? (tieA, tieB) : (tieB, tieA);

        var result = await RecentAsync(TestUsers.A, 10);

        Assert.Equal(
            [sameTimeCreatedLater.Id, sameTimeCreatedEarlier.Id, older.Id, higherId.Id, lowerId.Id],
            result.Transactions.Select(transaction => transaction.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(21)]
    public async Task LimitOutsideOneToTwenty_IsInvalid(int limit)
    {
        var result = await RecentAsync(TestUsers.A, limit);

        Assert.Equal(GetRecentTransactionsStatus.Invalid, result.Status);
        Assert.Equal("limit", result.Field);
    }

    private Task<GetRecentTransactionsResult> RecentAsync(Guid userId, int limit) =>
        _handler.HandleAsync(userId, new GetRecentTransactionsQuery(limit), CancellationToken.None);

    private Transaction Add(Guid userId, Guid accountId, DateTimeOffset occurredAt, DateTimeOffset? createdAt = null)
    {
        var transaction = Transaction.CreateExpense(userId, accountId, _category, 10m, "EUR", occurredAt, null, createdAt ?? Now);
        _transactions.Transactions.Add(transaction);

        return transaction;
    }
}
