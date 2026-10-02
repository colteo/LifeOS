using LifeOS.Application.Finance.PlannedExpenses;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.PlannedExpenses;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.PlannedExpenses;

public class PlannedExpenseHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Date = new(2026, 10, 10);
    private static (Memory Repository, PlannedExpenseHandler Handler, SavePlannedExpense Input) Setup()
    {
        var repo = new Memory();
        return (repo, new(repo, new FixedTimeProvider(Now)), new(" Visa ", repo.Account.Id, repo.Category.Id, 20, Date, " note "));
    }

    [Fact]
    public async Task CRUD_Ownership_AndBoundedQueries()
    {
        var (repo, h, input) = Setup(); var saved = await h.SaveAsync(TestUsers.A, null, input, default);
        Assert.Equal(PlannedExpenseResultStatus.Ok, saved.Status); var id = saved.Item!.Id;
        Assert.Equal("Visa", (await h.GetAsync(TestUsers.A, id, 0, default)).Items.Single().Name);
        Assert.Empty((await h.QueryAsync(TestUsers.B, Date, Date, 0, default)).Items);
        Assert.Empty((await h.QueryAsync(TestUsers.A, Date.AddDays(1), Date.AddDays(2), 0, default)).Items);
        Assert.Single((await h.QueryAsync(TestUsers.A, Date, Date, 0, default)).Items);
        Assert.Equal(PlannedExpenseResultStatus.Invalid, (await h.QueryAsync(TestUsers.A, Date, Date.AddDays(-1), 0, default)).Status);
        Assert.Equal(PlannedExpenseResultStatus.Invalid, (await h.QueryAsync(TestUsers.A, Date, Date.AddYears(11), 0, default)).Status);
        Assert.Equal(PlannedExpenseResultStatus.Invalid, (await h.GetAsync(TestUsers.A, id, -841, default)).Status);
        Assert.Equal(PlannedExpenseResultStatus.NotFound, (await h.GetAsync(TestUsers.B, id, 0, default)).Status);
        Assert.Equal(PlannedExpenseResultStatus.NotFound, (await h.SaveAsync(TestUsers.B, id, input, default)).Status);
        Assert.Equal(PlannedExpenseResultStatus.Invalid, (await h.SaveAsync(TestUsers.B, null, input, default)).Status);
        Assert.Equal(PlannedExpenseResultStatus.Invalid, (await h.SaveAsync(TestUsers.A, null, input with { CategoryId = Guid.NewGuid() }, default)).Status);
        Assert.Equal(PlannedExpenseResultStatus.Ok, (await h.SaveAsync(TestUsers.A, id, input with { Name = "Edited", ExpectedAmount = 25 }, default)).Status);
        Assert.Equal(25, (await h.GetAsync(TestUsers.A, id, 0, default)).Items.Single().ExpectedAmount);
        Assert.Equal(PlannedExpenseResultStatus.NotFound, (await h.DeleteAsync(TestUsers.B, id, default)).Status);
        Assert.Equal(PlannedExpenseResultStatus.Ok, (await h.DeleteAsync(TestUsers.A, id, default)).Status);
        Assert.Empty(repo.Items);
    }

    [Fact]
    public async Task Confirmation_Overrides_Retry_Concurrency_AndPlanningDeletionPreservesHistory()
    {
        var (repo, h, input) = Setup(); var id = (await h.SaveAsync(TestUsers.A, null, input, default)).Item!.Id;
        Assert.Equal(PlannedExpenseResultStatus.Invalid, (await h.ActAsync(TestUsers.A, id, 0, "confirm", new(0, null, Now), default)).Status);
        Assert.Empty(repo.Transactions); Assert.Empty(repo.States);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => h.ActAsync(TestUsers.A, id, 0, "confirm", new(25, "actual", Now.AddMonths(1)), default)));
        var tx = Assert.Single(repo.Transactions); Assert.Equal(25, tx.Amount); Assert.Equal("actual", tx.Note); Assert.Equal(Now.AddMonths(1), tx.OccurredAtUtc);
        Assert.All(results, r => Assert.Equal(tx.Id, r.Transaction!.Id));
        Assert.Equal(tx.Id, (await h.ActAsync(TestUsers.A, id, 0, "confirm", new(99, null, Now), default)).Transaction!.Id);
        Assert.Equal(PlannedExpenseResultStatus.Conflict, (await h.SaveAsync(TestUsers.A, id, input, default)).Status);
        Assert.Equal(PlannedExpenseResultStatus.Conflict, (await h.ActAsync(TestUsers.A, id, 0, "restore", null, default)).Status);
        await h.DeleteAsync(TestUsers.A, id, default); Assert.Single(repo.Transactions); Assert.Empty(repo.States);
    }

    [Fact]
    public async Task FutureRejection_CancelRestore_EditAfterRestore()
    {
        var (repo, h, input) = Setup(); input = input with { ScheduledDate = Date.AddDays(1) };
        var id = (await h.SaveAsync(TestUsers.A, null, input, default)).Item!.Id;
        Assert.Equal(PlannedExpenseResultStatus.Conflict, (await h.ActAsync(TestUsers.A, id, 0, "confirm", new(20, null, Now), default)).Status);
        Assert.Equal(PlannedExpenseResultStatus.NotFound, (await h.ActAsync(TestUsers.B, id, 0, "cancel", null, default)).Status);
        await h.ActAsync(TestUsers.A, id, 0, "cancel", null, default);
        Assert.Equal(PlannedExpenseStatus.Cancelled, (await h.GetAsync(TestUsers.A, id, 0, default)).Items.Single().Status);
        Assert.Equal(PlannedExpenseResultStatus.Conflict, (await h.SaveAsync(TestUsers.A, id, input, default)).Status);
        await h.ActAsync(TestUsers.A, id, 0, "restore", null, default);
        Assert.Equal(PlannedExpenseStatus.Projected, (await h.GetAsync(TestUsers.A, id, 0, default)).Items.Single().Status);
        await h.SaveAsync(TestUsers.A, id, input with { ScheduledDate = Date }, default);
        Assert.Equal(PlannedExpenseStatus.Due, (await h.GetAsync(TestUsers.A, id, 0, default)).Items.Single().Status);
        Assert.Empty(repo.Transactions);
    }

    // Focused in-memory adapter; PostgreSQL tests exercise real locking/FKs/rollback independently.
    private sealed class Memory : IPlannedExpenseRepository
    {
        public Account Account { get; } = Account.Create(TestUsers.A, "Cash", AccountType.Cash, "EUR", Now);
        public Category Category { get; } = Category.Create(TestUsers.A, "Fees", CategoryType.Expense, null, Now);
        public List<PlannedExpense> Items { get; } = [];
        public List<PlannedExpenseState> States { get; } = [];
        public List<Transaction> Transactions { get; } = [];
        private readonly SemaphoreSlim gate = new(1);
        private PlannedExpenseSnapshot Snapshot(Guid user, Guid? id, Guid? account = null, Guid? category = null)
        {
            var item = Items.SingleOrDefault(i => i.UserId == user && i.Id == id);
            var state = States.SingleOrDefault(s => s.UserId == user && s.PlannedExpenseId == id);
            return new(item, Account.UserId == user && Account.Id == (account ?? item?.AccountId) ? Account : null,
                Category.UserId == user && Category.Id == (category ?? item?.CategoryId) ? Category : null, state,
                Transactions.SingleOrDefault(t => t.UserId == user && t.Id == state?.TransactionId));
        }
        public Task<PlannedExpenseSnapshot> GetAsync(Guid user, Guid id, CancellationToken ct) => Task.FromResult(Snapshot(user, id));
        public Task<PlannedExpenseRead> ReadAsync(Guid user, DateOnly from, DateOnly to, CancellationToken ct) => Task.FromResult(new PlannedExpenseRead(
            Items.Where(i => i.UserId == user && i.ScheduledDate >= from && i.ScheduledDate <= to).ToList(), States.Where(s => s.UserId == user).ToList(), Account.UserId == user ? [Account] : []));
        public async Task<PlannedExpenseResult> ExecuteAsync(Guid user, Guid? id, Guid? account, Guid? category,
            Func<PlannedExpenseSnapshot, PlannedExpenseResult> decide, CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try
            {
                var r = decide(Snapshot(user, id, account, category));
                if (r.Status != PlannedExpenseResultStatus.Ok) return r;
                switch (r.Change)
                {
                    case PlannedExpenseChange.Save: if (!Items.Contains(r.Item!)) Items.Add(r.Item!); break;
                    case PlannedExpenseChange.Process: States.Add(r.State!); if (r.Transaction is not null) Transactions.Add(r.Transaction); break;
                    case PlannedExpenseChange.Delete: Items.Remove(r.Item!); States.RemoveAll(s => s.PlannedExpenseId == id); break;
                    case PlannedExpenseChange.Restore: States.RemoveAll(s => s.PlannedExpenseId == id); break;
                }
                return r;
            }
            finally { gate.Release(); }
        }
    }
}
