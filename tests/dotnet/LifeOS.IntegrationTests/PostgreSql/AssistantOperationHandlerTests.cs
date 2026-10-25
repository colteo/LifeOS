using LifeOS.Application.Assistant;
using LifeOS.Application.Finance.Accounts;
using LifeOS.Application.Finance.Assistant;
using LifeOS.Application.Finance.Categories;
using LifeOS.Application.Finance.Transactions;
using LifeOS.Application.Finance.Transactions.CreateTransaction;
using LifeOS.Application.Persistence;
using LifeOS.Application.Users;
using LifeOS.Domain.Assistant;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Assistant;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// AI-007 F2: the operation use cases on the real repositories and PostgreSQL. Every assertion about writes
// reads the database itself (transactions and assistant_operations rows), not only what a handler returned.
// Races are made deterministic: a confirmation is paused inside its database transaction, after its row lock,
// and the competing request is observed waiting on that lock in pg_stat_activity before the pause ends.
//
// The clock is manual (2026-10-24 09:00Z = 11:00 in Rome); users are in Europe/Rome unless stated.
[Collection(PostgreSqlCollection.Name)]
public class AssistantOperationHandlerTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Microsecond = TimeSpan.FromTicks(TimeSpan.TicksPerMicrosecond);

    private readonly ManualTimeProvider _clock = new(Now);

    // ---- Preparing a draft writes no Finance data ----

    [Fact]
    public async Task Create_StoresAnIncompleteDraft_NeverPreselectsTheAccount_AndWritesNoTransaction()
    {
        var owner = await NewOwnerAsync();
        await NewAccountAsync(owner.User); // the only account: still not chosen for the user
        var conversation = await NewConversationAsync(owner.User);

        var result = await CreateAsync(owner.User, conversation, new FinanceDraftInput(TransactionType.Expense, 5m, Note: "colazione con Pamela",
            CategoryId: owner.Expense));

        Assert.Equal(AssistantResultStatus.Ok, result.Status);
        var view = result.Value!.View;
        Assert.Equal((AssistantOperationStatus.Collecting, 1), (view.Operation.Status, view.Operation.DraftVersion));
        Assert.Equal(["accountId"], view.Operation.Draft.MissingFields);
        Assert.Null(view.Account);
        Assert.Equal(("Ristoranti", FinanceDraftCategorySource.User), (view.Category!.Name, view.Category.Source));
        Assert.False(view.CanConfirm);

        var stored = await StoredAsync(owner.User, view.Operation.Id);
        Assert.Equal(("colazione con Pamela", (Guid?)null, Now.AddHours(24)), (stored.Draft.Note, stored.Draft.AccountId, stored.ExpiresAtUtc));
        Assert.Equal(0, await TransactionCountAsync(owner.User));
    }

    [Fact]
    public async Task Create_RefusesReferencesTheUserDoesNotOwn_OrThatDoNotFit()
    {
        var owner = await NewOwnerAsync();
        var intruder = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var usd = await NewAccountAsync(owner.User, "USD");

        await AssertRejectedAsync(FinanceOperationErrors.AccountNotFound, "accountId",
            CreateAsync(owner.User, conversation, new FinanceDraftInput(AccountId: intruder.Account)));
        await AssertRejectedAsync(FinanceOperationErrors.CategoryNotFound, "categoryId",
            CreateAsync(owner.User, conversation, new FinanceDraftInput(CategoryId: intruder.Expense)));
        await AssertRejectedAsync(FinanceOperationErrors.CategoryIncompatible, "categoryId",
            CreateAsync(owner.User, conversation, new FinanceDraftInput(TransactionType.Expense, CategoryId: owner.Income)));
        // The user said euros: a USD account is not compatible.
        await AssertRejectedAsync(FinanceOperationErrors.CurrencyMismatch, "accountId",
            CreateAsync(owner.User, conversation, new FinanceDraftInput(Currency: "EUR", AccountId: usd)));

        // Another user's conversation is a missing one.
        Assert.Equal(AssistantResultStatus.NotFound, (await CreateAsync(intruder.User, conversation, new FinanceDraftInput())).Status);
        Assert.Equal(AssistantResultStatus.Invalid, (await CreateAsync(owner.User, conversation, new FinanceDraftInput(Amount: -1m))).Status);

        Assert.Empty(await RowsAsync(conversation));
    }

    [Fact]
    public async Task WithoutATimeZone_TheDateStaysMissing_UntilTheUserSetsOne()
    {
        var owner = await NewOwnerAsync(timeZone: null);
        var conversation = await NewConversationAsync(owner.User);

        var created = (await CreateAsync(owner.User, conversation, Complete(owner))).Value!.View;
        Assert.Equal(AssistantOperationStatus.Collecting, created.Operation.Status);
        Assert.Equal(["occurredAt"], created.Operation.Draft.MissingFields);
        Assert.Equal([FinanceOperationErrors.TimeZoneRequired], created.Issues);

        await Execute($"UPDATE users SET time_zone_id = 'Europe/Rome' WHERE id = '{owner.User.Id}'");
        var patched = (await PatchAsync(owner.User, created.Operation.Id, 1, new FinanceDraftPatch(Note: "pranzo"))).Value!.View;

        Assert.Equal((AssistantOperationStatus.AwaitingConfirmation, "Europe/Rome"), (patched.Operation.Status, patched.Operation.Draft.Occurrence!.TimeZoneId));
        Assert.True(patched.CanConfirm);
    }

    [Fact]
    public async Task OneOpenOperationPerConversation_UntilItCloses_OrExpires()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var first = (await CreateAsync(owner.User, conversation, new FinanceDraftInput(Amount: 5m))).Value!.View.Operation;

        var second = await CreateAsync(owner.User, conversation, new FinanceDraftInput(Amount: 7m));
        Assert.Equal((AssistantResultStatus.Conflict, AssistantErrors.OperationAlreadyOpen, first.Id),
            (second.Status, second.Code, second.Value!.View.Operation.Id));

        // Concurrent creates in a fresh conversation: exactly one is stored.
        var other = await NewConversationAsync(owner.User);
        var raced = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => CreateAsync(owner.User, other, new FinanceDraftInput(Amount: 1m)))));
        Assert.Single(raced, result => result.Status == AssistantResultStatus.Ok);
        Assert.All(raced.Where(result => result.Status != AssistantResultStatus.Ok), result => Assert.Equal(AssistantErrors.OperationAlreadyOpen, result.Code));
        Assert.Single(await RowsAsync(other));

        // 24 h later the first draft has expired: a new one can start, the old one stays as history.
        _clock.Advance(TimeSpan.FromHours(24));
        var third = await CreateAsync(owner.User, conversation, new FinanceDraftInput(Amount: 9m));
        Assert.Equal(AssistantResultStatus.Ok, third.Status);
        Assert.Equal(["Expired", "Collecting"], (await RowsAsync(conversation)).Select(row => row.Status));
    }

    // ---- Editing: versions, no-ops, expiry ----

    [Fact]
    public async Task Patch_IsVersioned_ANoOpChangesNothing_AndOnlySubstantiveChangesRenewTheExpiry()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var operation = (await CreateAsync(owner.User, conversation, Complete(owner))).Value!.View.Operation;
        Assert.Equal(AssistantOperationStatus.AwaitingConfirmation, operation.Status);

        // A read 23 h later neither versions nor extends.
        _clock.Advance(TimeSpan.FromHours(23));
        Assert.Equal(AssistantResultStatus.Ok, (await GetAsync(owner.User, operation.Id)).Status);
        Assert.Equal((1, Now.AddHours(24), Now), Single(await RowsAsync(conversation)));

        // The same values again: no new version, no new expiry.
        var noOp = await PatchAsync(owner.User, operation.Id, 1, new FinanceDraftPatch(Amount: 5.00m, Note: "colazione con Pamela", AccountId: owner.Account));
        Assert.Equal((AssistantResultStatus.Ok, false), (noOp.Status, noOp.Value!.Changed));
        Assert.Equal((1, Now.AddHours(24), Now), Single(await RowsAsync(conversation)));

        // A real change: version 2, expiry 24 h from now, and an incomplete draft goes back to Collecting.
        var changed = await PatchAsync(owner.User, operation.Id, 1, new FinanceDraftPatch(Clear: [FinanceDraftFields.Account]));
        Assert.Equal((AssistantResultStatus.Ok, true, AssistantOperationStatus.Collecting),
            (changed.Status, changed.Value!.Changed, changed.Value.View.Operation.Status));
        Assert.Equal((2, _clock.UtcNow.AddHours(24), _clock.UtcNow), Single(await RowsAsync(conversation)));

        // The client that still holds version 1 is refused, told the current version, and nothing changes.
        var stale = await PatchAsync(owner.User, operation.Id, 1, new FinanceDraftPatch(Amount: 99m));
        Assert.Equal((AssistantResultStatus.Conflict, AssistantErrors.VersionConflict, 2), (stale.Status, stale.Code, stale.Value!.View.Operation.DraftVersion));
        Assert.Equal(5m, (await StoredAsync(owner.User, operation.Id)).Draft.Amount);

        Assert.Equal(AssistantResultStatus.Invalid, (await PatchAsync(owner.User, operation.Id, 2,
            new FinanceDraftPatch(Amount: 1m, Clear: [FinanceDraftFields.Amount]))).Status);
        Assert.Equal(AssistantResultStatus.Invalid, (await PatchAsync(owner.User, operation.Id, 2, new FinanceDraftPatch(Clear: ["userId"]))).Status);
    }

    [Fact]
    public async Task ConcurrentPatches_OfTheSameVersion_ExactlyOneWins()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var operation = (await CreateAsync(owner.User, conversation, new FinanceDraftInput(Amount: 1m))).Value!.View.Operation;

        var results = await Task.WhenAll(Enumerable.Range(2, 4).Select(amount => Task.Run(() =>
            PatchAsync(owner.User, operation.Id, 1, new FinanceDraftPatch(Amount: amount)))));

        var winner = Assert.Single(results, result => result.Status == AssistantResultStatus.Ok);
        Assert.All(results.Where(result => result != winner), result => Assert.Equal(AssistantErrors.VersionConflict, result.Code));
        var stored = await StoredAsync(owner.User, operation.Id);
        Assert.Equal((2, winner.Value!.View.Operation.Draft.Amount), (stored.DraftVersion, stored.Draft.Amount));
    }

    [Fact]
    public async Task AnExpiredDraft_CanBeReadButNeitherEditedNorConfirmed()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var operation = (await CreateAsync(owner.User, conversation, Complete(owner))).Value!.View.Operation;

        _clock.Advance(TimeSpan.FromHours(24));

        Assert.Equal(AssistantErrors.OperationExpired, (await ConfirmAsync(owner.User, operation.Id, 1)).Code);
        Assert.Equal(AssistantErrors.OperationExpired, (await PatchAsync(owner.User, operation.Id, 1, new FinanceDraftPatch(Amount: 6m))).Code);
        Assert.Equal(AssistantErrors.OperationExpired, (await CancelAsync(owner.User, operation.Id, 1)).Code);

        var read = await GetAsync(owner.User, operation.Id);
        Assert.Equal((AssistantOperationStatus.Expired, false), (read.Value!.Operation.Status, read.Value.CanConfirm));
        await using (var scope = fixture.CreateScope())
        {
            // No longer the conversation's active operation.
            Assert.Null((await new GetActiveFinanceOperationHandler(Repository(scope), References(scope), _clock).HandleAsync(owner.User.Id, conversation, default)).Value);
        }

        Assert.Equal("Expired", Assert.Single(await RowsAsync(conversation)).Status);
        Assert.Equal(0, await TransactionCountAsync(owner.User));
    }

    [Fact]
    public async Task TheExpiryBoundary_IsExact()
    {
        var owner = await NewOwnerAsync();

        // One microsecond before the 24 h: still confirmable.
        var early = await NewConversationAsync(owner.User);
        var confirmable = (await CreateAsync(owner.User, early, Complete(owner))).Value!.View.Operation;
        _clock.UtcNow = confirmable.ExpiresAtUtc - Microsecond;
        Assert.Equal(AssistantResultStatus.Ok, (await ConfirmAsync(owner.User, confirmable.Id, 1)).Status);

        // At exactly 24 h: expired.
        _clock.UtcNow = Now;
        var late = await NewConversationAsync(owner.User);
        var expiring = (await CreateAsync(owner.User, late, Complete(owner))).Value!.View.Operation;
        _clock.UtcNow = expiring.ExpiresAtUtc;
        Assert.Equal(AssistantErrors.OperationExpired, (await ConfirmAsync(owner.User, expiring.Id, 1)).Code);

        Assert.Equal(1, await TransactionCountAsync(owner.User));
        Assert.Equal(["Executed"], (await RowsAsync(early)).Select(row => row.Status));
        Assert.Equal(["Expired"], (await RowsAsync(late)).Select(row => row.Status));
    }

    // ---- Confirmation ----

    [Fact]
    public async Task Confirm_WritesExactlyOneTransaction_ThroughFinance_AndAReplayReturnsTheSameOne()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var operation = (await CreateAsync(owner.User, conversation, Complete(owner) with { OccurredOn = new DateOnly(2026, 10, 23) })).Value!.View.Operation;

        var confirmed = await ConfirmAsync(owner.User, operation.Id, 1);

        Assert.Equal((AssistantResultStatus.Ok, true), (confirmed.Status, confirmed.Value!.Changed));
        var transactionId = confirmed.Value.View.Operation.ResultTransactionId!.Value;

        // What Finance stored: the draft's values, the account's currency, yesterday at 12:00 in Rome.
        await using (var scope = fixture.CreateScope())
        {
            var transaction = (await scope.ServiceProvider.GetRequiredService<ITransactionRepository>().GetByIdAsync(owner.User.Id, transactionId, default))!;
            Assert.Equal((TransactionType.Expense, 5m, "EUR", (Guid?)owner.Account, (Guid?)owner.Expense, "colazione con Pamela"),
                (transaction.TransactionType, transaction.Amount, transaction.Currency, transaction.AccountId, transaction.CategoryId, transaction.Note));
            Assert.Equal(new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero), transaction.OccurredAtUtc);
        }

        var row = Assert.Single(await RowsAsync(conversation));
        Assert.Equal(("Executed", (Guid?)transactionId, 1), (row.Status, row.ResultTransactionId, row.DraftVersion));

        // A retry (e.g. after a timeout), even with an older version: the same transaction, nothing new.
        foreach (var version in new[] { 1, 7 })
        {
            var replay = await ConfirmAsync(owner.User, operation.Id, version);
            Assert.Equal((AssistantResultStatus.Ok, false, (Guid?)transactionId),
                (replay.Status, replay.Value!.Changed, replay.Value.View.Operation.ResultTransactionId));
        }

        Assert.Equal(1, await TransactionCountAsync(owner.User));

        // An executed operation accepts no further change.
        Assert.Equal(AssistantErrors.OperationExecuted, (await PatchAsync(owner.User, operation.Id, 1, new FinanceDraftPatch(Amount: 6m))).Code);
        Assert.Equal(AssistantErrors.OperationExecuted, (await CancelAsync(owner.User, operation.Id, 1)).Code);
    }

    [Fact]
    public async Task Confirm_RefusesIncompleteDrafts_AndStaleVersions_WithoutWriting()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var operation = (await CreateAsync(owner.User, conversation, Complete(owner) with { AccountId = null })).Value!.View.Operation;

        var incomplete = await ConfirmAsync(owner.User, operation.Id, 1);
        Assert.Equal((AssistantResultStatus.Rejected, AssistantErrors.DraftIncomplete, "accountId"), (incomplete.Status, incomplete.Code, incomplete.Field));

        await PatchAsync(owner.User, operation.Id, 1, new FinanceDraftPatch(AccountId: owner.Account));

        // The user saw version 1; the draft is now version 2.
        var stale = await ConfirmAsync(owner.User, operation.Id, 1);
        Assert.Equal((AssistantResultStatus.Conflict, AssistantErrors.VersionConflict), (stale.Status, stale.Code));

        Assert.Equal(0, await TransactionCountAsync(owner.User));
        Assert.Equal("AwaitingConfirmation", Assert.Single(await RowsAsync(conversation)).Status);
        Assert.Equal(AssistantResultStatus.Ok, (await ConfirmAsync(owner.User, operation.Id, 2)).Status);
    }

    [Fact]
    public async Task TwoConcurrentConfirmations_ProduceOneTransaction_AndTheLoserReadsTheWinnersResult()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var operation = (await CreateAsync(owner.User, conversation, Complete(owner))).Value!.View.Operation;
        var paused = new Pause();

        // The first confirmation holds the row lock inside its transaction while the second one arrives.
        var first = Task.Run(() => ConfirmAsync(owner.User, operation.Id, 1, beforeInsert: paused.HoldAsync));
        var blocker = await paused.ReachedAsync();
        var second = Task.Run(() => ConfirmAsync(owner.User, operation.Id, 1));
        await WaitUntilBlockedByAsync(blocker);
        paused.Release();

        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(AssistantResultStatus.Ok, result.Status));
        Assert.Equal([true, false], results.Select(result => result.Value!.Changed));
        Assert.Single(results.Select(result => result.Value!.View.Operation.ResultTransactionId).Distinct());
        Assert.Equal(1, await TransactionCountAsync(owner.User));
    }

    [Fact]
    public async Task ManyConcurrentConfirmations_StillProduceOneTransaction()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var operation = (await CreateAsync(owner.User, conversation, Complete(owner))).Value!.View.Operation;

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => ConfirmAsync(owner.User, operation.Id, 1))));

        Assert.All(results, result => Assert.Equal(AssistantResultStatus.Ok, result.Status));
        Assert.Single(results, result => result.Value!.Changed);
        Assert.Single(results.Select(result => result.Value!.View.Operation.ResultTransactionId).Distinct());
        Assert.Equal(1, await TransactionCountAsync(owner.User));
        Assert.Equal(results[0].Value!.View.Operation.ResultTransactionId, Assert.Single(await RowsAsync(conversation)).ResultTransactionId);
    }

    // The transaction insert and the status change commit together: a failure after the insert leaves neither.
    [Fact]
    public async Task AFailureAfterTheInsert_RollsBackTheTransactionAndTheStatusTogether()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var operation = (await CreateAsync(owner.User, conversation, Complete(owner))).Value!.View.Operation;

        await Assert.ThrowsAsync<InvalidOperationException>(() => ConfirmAsync(owner.User, operation.Id, 1,
            afterInsert: () => throw new InvalidOperationException("simulated crash after the insert")));

        Assert.Equal(0, await TransactionCountAsync(owner.User));
        var row = Assert.Single(await RowsAsync(conversation));
        Assert.Equal(("AwaitingConfirmation", (Guid?)null, 1), (row.Status, row.ResultTransactionId, row.DraftVersion));

        // Nothing is left half-done: the same confirmation succeeds afterwards.
        Assert.Equal(AssistantResultStatus.Ok, (await ConfirmAsync(owner.User, operation.Id, 1)).Status);
        Assert.Equal(1, await TransactionCountAsync(owner.User));
    }

    // The status update itself fails in PostgreSQL (here: it would reuse another operation's result, which the
    // unique index refuses) after CreateTransactionHandler has inserted the transaction: the insert is undone.
    [Fact]
    public async Task AStatusUpdateRefusedByTheDatabase_RollsBackTheTransactionInsert()
    {
        var owner = await NewOwnerAsync();
        var executed = (await CreateAsync(owner.User, await NewConversationAsync(owner.User), Complete(owner))).Value!.View.Operation;
        var taken = (await ConfirmAsync(owner.User, executed.Id, 1)).Value!.View.Operation.ResultTransactionId!.Value;
        var conversation = await NewConversationAsync(owner.User);
        var operation = (await CreateAsync(owner.User, conversation, Complete(owner) with { Amount = 9m })).Value!.View.Operation;

        await PostgresAssert.ViolatesAsync(PostgresAssert.UniqueViolation, "ux_assistant_operations_result_transaction",
            () => ConfirmAsync(owner.User, operation.Id, 1, operations: inner => new HookedOperationRepository(inner,
                update: (current, status, version, ct) => inner.TryUpdateAsync(WithResult(current, taken), status, version, ct))));

        Assert.Equal(1, await TransactionCountAsync(owner.User));
        Assert.Equal(0, await TransactionCountAsync(owner.User, amount: 9m));
        Assert.Equal(("AwaitingConfirmation", (Guid?)null), Status(Assert.Single(await RowsAsync(conversation))));

        Assert.Equal(AssistantResultStatus.Ok, (await ConfirmAsync(owner.User, operation.Id, 1)).Status);
        Assert.Equal(1, await TransactionCountAsync(owner.User, amount: 9m));
    }

    // A status update that writes no row does not commit the transaction it would have recorded: every attempt
    // rolls back, and after the bounded retries the request is Busy with nothing stored.
    [Fact]
    public async Task AStatusUpdateThatWritesNothing_CommitsNoTransaction()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var operation = (await CreateAsync(owner.User, conversation, Complete(owner))).Value!.View.Operation;
        var attempts = 0;

        var result = await ConfirmAsync(owner.User, operation.Id, 1, operations: inner => new HookedOperationRepository(inner,
            update: (_, _, _, _) => Task.FromResult(Interlocked.Increment(ref attempts) < 0)));

        Assert.Equal(AssistantResultStatus.Busy, result.Status);
        Assert.Equal(AssistantTurnPolicy.MaxWriteAttempts, attempts);
        Assert.Equal(0, await TransactionCountAsync(owner.User));
        Assert.Equal(("AwaitingConfirmation", (Guid?)null), Status(Assert.Single(await RowsAsync(conversation))));
    }

    // An edit or a cancellation commits after the confirmation has read version 1 but before it locks the row:
    // the lock (status + version + expiry) finds nothing, the confirmation reads again and refuses; no transaction.
    [Fact]
    public async Task AChangeCommittedBetweenTheConfirmationsReadAndItsLock_IsNeverConfirmedOver()
    {
        var owner = await NewOwnerAsync();

        var edited = await NewConversationAsync(owner.User);
        var a = (await CreateAsync(owner.User, edited, Complete(owner))).Value!.View.Operation;
        var stale = await ConfirmAsync(owner.User, a.Id, 1, operations: inner => new HookedOperationRepository(inner,
            beforeLock: async () => Assert.Equal(AssistantResultStatus.Ok, (await PatchAsync(owner.User, a.Id, 1, new FinanceDraftPatch(Amount: 50m))).Status)));

        Assert.Equal((AssistantResultStatus.Conflict, AssistantErrors.VersionConflict, 2), (stale.Status, stale.Code, stale.Value!.View.Operation.DraftVersion));
        Assert.Equal(0, await TransactionCountAsync(owner.User));
        var editedRow = Assert.Single(await RowsAsync(edited));
        Assert.Equal(("AwaitingConfirmation", 2), (editedRow.Status, editedRow.DraftVersion));

        var cancelled = await NewConversationAsync(owner.User);
        var b = (await CreateAsync(owner.User, cancelled, Complete(owner))).Value!.View.Operation;
        var late = await ConfirmAsync(owner.User, b.Id, 1, operations: inner => new HookedOperationRepository(inner,
            beforeLock: async () => Assert.Equal(AssistantResultStatus.Ok, (await CancelAsync(owner.User, b.Id, 1)).Status)));

        Assert.Equal((AssistantResultStatus.Conflict, AssistantErrors.OperationCancelled), (late.Status, late.Code));
        Assert.Equal(0, await TransactionCountAsync(owner.User));
        Assert.Equal(("Cancelled", (Guid?)null), Status(Assert.Single(await RowsAsync(cancelled))));

        // The version the user now sees is confirmable, with its own values.
        Assert.Equal(AssistantResultStatus.Ok, (await ConfirmAsync(owner.User, a.Id, 2)).Status);
        Assert.Equal(1, await TransactionCountAsync(owner.User, amount: 50m));
        Assert.Equal(1, await TransactionCountAsync(owner.User));
    }

    [Fact]
    public async Task AnAccountOrCategoryDeletedAfterTheDraft_FailsTheConfirmation_WithoutAnyWrite()
    {
        var owner = await NewOwnerAsync();

        var accountConversation = await NewConversationAsync(owner.User);
        var spareAccount = await NewAccountAsync(owner.User);
        var withAccount = (await CreateAsync(owner.User, accountConversation, Complete(owner) with { AccountId = spareAccount })).Value!.View.Operation;
        await using (var scope = fixture.CreateScope())
        {
            Assert.Equal(AccountDeleteOutcome.Deleted, await scope.ServiceProvider.GetRequiredService<IAccountRepository>().DeleteAsync(owner.User.Id, spareAccount, default));
        }

        var categoryConversation = await NewConversationAsync(owner.User);
        var spareCategory = await NewCategoryAsync(owner.User, CategoryType.Expense, "Svago");
        var withCategory = (await CreateAsync(owner.User, categoryConversation, Complete(owner) with { CategoryId = spareCategory })).Value!.View.Operation;
        await using (var scope = fixture.CreateScope())
        {
            Assert.Equal(CategoryDeleteOutcome.Deleted, await scope.ServiceProvider.GetRequiredService<ICategoryRepository>().DeleteAsync(owner.User.Id, spareCategory, default));
        }

        var account = await ConfirmAsync(owner.User, withAccount.Id, 1);
        var category = await ConfirmAsync(owner.User, withCategory.Id, 1);

        Assert.Equal((AssistantResultStatus.Rejected, FinanceOperationErrors.AccountNotFound), (account.Status, account.Code));
        Assert.Equal((AssistantResultStatus.Rejected, FinanceOperationErrors.CategoryNotFound), (category.Status, category.Code));
        Assert.Equal(0, await TransactionCountAsync(owner.User));
        // Correctable: the operations stay open and unchanged, and report the problem.
        Assert.Equal(["AwaitingConfirmation"], (await RowsAsync(accountConversation)).Select(row => row.Status));
        Assert.Equal([FinanceOperationErrors.AccountNotFound], (await GetAsync(owner.User, withAccount.Id)).Value!.Issues);
        Assert.False((await GetAsync(owner.User, withCategory.Id)).Value!.CanConfirm);
    }

    // AI-007 R-2: the account disappears between CreateTransactionHandler's lookup and its insert, inside the
    // confirmation's transaction. The insert's foreign-key violation must not poison the transaction (EF Core
    // saves inside a savepoint), so the handler can still re-read and report NotFound, and everything rolls back.
    [Fact]
    public async Task AnAccountDeletedDuringTheConfirmation_RollsBackCleanly()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var spare = await NewAccountAsync(owner.User);
        var operation = (await CreateAsync(owner.User, conversation, Complete(owner) with { AccountId = spare })).Value!.View.Operation;

        var result = await ConfirmAsync(owner.User, operation.Id, 1, afterAccountLookup: async () =>
        {
            await using var other = fixture.CreateScope();
            Assert.Equal(AccountDeleteOutcome.Deleted, await other.ServiceProvider.GetRequiredService<IAccountRepository>().DeleteAsync(owner.User.Id, spare, default));
        });

        Assert.Equal((AssistantResultStatus.Rejected, FinanceOperationErrors.AccountNotFound), (result.Status, result.Code));
        Assert.Equal(0, await TransactionCountAsync(owner.User));
        Assert.Equal(("AwaitingConfirmation", (Guid?)null), Status(Assert.Single(await RowsAsync(conversation))));
    }

    [Fact]
    public async Task ConfirmAndCancel_WhicheverComesFirstWins()
    {
        var owner = await NewOwnerAsync();

        var cancelledFirst = await NewConversationAsync(owner.User);
        var a = (await CreateAsync(owner.User, cancelledFirst, Complete(owner))).Value!.View.Operation;
        Assert.Equal(AssistantResultStatus.Ok, (await CancelAsync(owner.User, a.Id, 1)).Status);
        Assert.Equal(AssistantErrors.OperationCancelled, (await ConfirmAsync(owner.User, a.Id, 1)).Code);
        // Cancelling again is idempotent.
        Assert.Equal((AssistantResultStatus.Ok, false), Changed(await CancelAsync(owner.User, a.Id, 1)));

        // A cancellation arriving while the confirmation holds the row waits, then finds it executed.
        var confirmedFirst = await NewConversationAsync(owner.User);
        var b = (await CreateAsync(owner.User, confirmedFirst, Complete(owner))).Value!.View.Operation;
        var paused = new Pause();
        var confirm = Task.Run(() => ConfirmAsync(owner.User, b.Id, 1, beforeInsert: paused.HoldAsync));
        var blocker = await paused.ReachedAsync();
        var cancel = Task.Run(() => CancelAsync(owner.User, b.Id, 1));
        await WaitUntilBlockedByAsync(blocker);
        paused.Release();

        Assert.Equal(AssistantResultStatus.Ok, (await confirm).Status);
        Assert.Equal(AssistantErrors.OperationExecuted, (await cancel).Code);
        Assert.Equal(1, await TransactionCountAsync(owner.User));
        Assert.Equal(["Executed"], (await RowsAsync(confirmedFirst)).Select(row => row.Status));
    }

    [Fact]
    public async Task AnEditArrivingDuringTheConfirmation_WaitsAndThenFindsItExecuted()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var operation = (await CreateAsync(owner.User, conversation, Complete(owner))).Value!.View.Operation;
        var paused = new Pause();

        var confirm = Task.Run(() => ConfirmAsync(owner.User, operation.Id, 1, beforeInsert: paused.HoldAsync));
        var blocker = await paused.ReachedAsync();
        var patch = Task.Run(() => PatchAsync(owner.User, operation.Id, 1, new FinanceDraftPatch(Amount: 50m)));
        await WaitUntilBlockedByAsync(blocker);
        paused.Release();

        Assert.Equal(AssistantResultStatus.Ok, (await confirm).Status);
        Assert.Equal(AssistantErrors.OperationExecuted, (await patch).Code);

        await using var scope = fixture.CreateScope();
        var stored = (await scope.ServiceProvider.GetRequiredService<ITransactionRepository>().GetRecentAsync(owner.User.Id, 10, default)).Single();
        Assert.Equal(5m, stored.Amount); // the confirmed version, not the late edit
    }

    // Deleting the conversation while its confirmation runs: the delete waits for the confirmation; the
    // transaction is a Finance record and survives the conversation and its operations.
    [Fact]
    public async Task DeletingTheConversationDuringTheConfirmation_KeepsTheTransaction()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var operation = (await CreateAsync(owner.User, conversation, Complete(owner))).Value!.View.Operation;
        var paused = new Pause();

        var confirm = Task.Run(() => ConfirmAsync(owner.User, operation.Id, 1, beforeInsert: paused.HoldAsync));
        var blocker = await paused.ReachedAsync();
        var delete = Task.Run(async () =>
        {
            await using var scope = fixture.CreateScope();
            return await new DeleteAssistantConversationHandler(scope.ServiceProvider.GetRequiredService<IAssistantConversationRepository>())
                .HandleAsync(owner.User.Id, conversation, default);
        });
        await WaitUntilBlockedByAsync(blocker);
        paused.Release();

        var confirmed = await confirm;
        Assert.Equal(AssistantResultStatus.Ok, confirmed.Status);
        Assert.Equal(AssistantResultStatus.Ok, await delete);

        Assert.Empty(await RowsAsync(conversation));
        Assert.Equal(1, await TransactionCountAsync(owner.User));

        // Deleting first: the confirmation finds nothing and writes nothing.
        var other = await NewConversationAsync(owner.User);
        var orphan = (await CreateAsync(owner.User, other, Complete(owner))).Value!.View.Operation;
        await using (var scope = fixture.CreateScope())
        {
            await new DeleteAssistantConversationHandler(scope.ServiceProvider.GetRequiredService<IAssistantConversationRepository>())
                .HandleAsync(owner.User.Id, other, default);
        }

        Assert.Equal(AssistantResultStatus.NotFound, (await ConfirmAsync(owner.User, orphan.Id, 1)).Status);
        Assert.Equal(1, await TransactionCountAsync(owner.User));
    }

    [Fact]
    public async Task AnotherUsersOperation_IsIndistinguishableFromAMissingOne()
    {
        var owner = await NewOwnerAsync();
        var intruder = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var operation = (await CreateAsync(owner.User, conversation, Complete(owner))).Value!.View.Operation;

        foreach (var id in new[] { operation.Id, Guid.CreateVersion7() })
        {
            Assert.Equal(AssistantResultStatus.NotFound, (await GetAsync(intruder.User, id)).Status);
            Assert.Equal(AssistantResultStatus.NotFound, (await PatchAsync(intruder.User, id, 1, new FinanceDraftPatch(Amount: 1m))).Status);
            Assert.Equal(AssistantResultStatus.NotFound, (await ConfirmAsync(intruder.User, id, 1)).Status);
            Assert.Equal(AssistantResultStatus.NotFound, (await CancelAsync(intruder.User, id, 1)).Status);
        }

        Assert.Equal(("AwaitingConfirmation", (Guid?)null), Status(Assert.Single(await RowsAsync(conversation))));
        Assert.Equal(0, await TransactionCountAsync(owner.User) + await TransactionCountAsync(intruder.User));
    }

    // ---- Helpers ----

    private sealed record Owner(User User, Guid Account, Guid Expense, Guid Income);

    private sealed record OperationRow(Guid Id, string Status, int DraftVersion, Guid? ResultTransactionId, DateTimeOffset ExpiresAtUtc, DateTimeOffset UpdatedAtUtc);

    private static (int, DateTimeOffset, DateTimeOffset) Single(List<OperationRow> rows)
    {
        var row = Assert.Single(rows);
        return (row.DraftVersion, row.ExpiresAtUtc, row.UpdatedAtUtc);
    }

    private static (string, Guid?) Status(OperationRow row) => (row.Status, row.ResultTransactionId);

    private static (AssistantResultStatus, bool?) Changed(AssistantResult<FinanceOperationOutcome> result) => (result.Status, result.Value?.Changed);

    // A complete expense: 5 EUR, the owner's account and expense category, today.
    private static FinanceDraftInput Complete(Owner owner) =>
        new(TransactionType.Expense, 5m, null, owner.Account, owner.Expense, "colazione con Pamela");

    private async Task<AssistantResult<FinanceOperationOutcome>> CreateAsync(User user, Guid conversationId, FinanceDraftInput input)
    {
        await using var scope = fixture.CreateScope();
        return await new CreateFinanceOperationHandler(Repository(scope), scope.ServiceProvider.GetRequiredService<IAssistantConversationRepository>(),
            References(scope), scope.ServiceProvider.GetRequiredService<IUserRepository>(), _clock).HandleAsync(user.Id, conversationId, input, default);
    }

    private async Task<AssistantResult<FinanceOperationView>> GetAsync(User user, Guid operationId)
    {
        await using var scope = fixture.CreateScope();
        return await new GetFinanceOperationHandler(Repository(scope), References(scope), _clock).HandleAsync(user.Id, operationId, default);
    }

    private async Task<AssistantResult<FinanceOperationOutcome>> PatchAsync(User user, Guid operationId, int version, FinanceDraftPatch patch)
    {
        await using var scope = fixture.CreateScope();
        return await new UpdateFinanceDraftHandler(Repository(scope), References(scope), scope.ServiceProvider.GetRequiredService<IUserRepository>(), _clock)
            .HandleAsync(user.Id, operationId, version, patch, default);
    }

    private async Task<AssistantResult<FinanceOperationOutcome>> CancelAsync(User user, Guid operationId, int version)
    {
        await using var scope = fixture.CreateScope();
        return await new CancelFinanceOperationHandler(Repository(scope), References(scope), _clock).HandleAsync(user.Id, operationId, version, default);
    }

    // The real handler; the hooks run inside CreateTransactionHandler (so inside the confirmation's transaction):
    // after its account lookup, right before its insert, right after its insert.
    // `operations` wraps the real operation repository (e.g. HookedOperationRepository).
    private async Task<AssistantResult<FinanceOperationOutcome>> ConfirmAsync(User user, Guid operationId, int version,
        Func<LifeOSDbContext, Task>? beforeInsert = null, Func<Task>? afterInsert = null, Func<Task>? afterAccountLookup = null,
        Func<IFinanceOperationRepository, IFinanceOperationRepository>? operations = null)
    {
        await using var scope = fixture.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<LifeOSDbContext>();
        var accounts = new HookedAccountRepository(services.GetRequiredService<IAccountRepository>(), afterAccountLookup);
        var transactions = new HookedTransactionRepository(services.GetRequiredService<ITransactionRepository>(),
            beforeInsert is null ? null : () => beforeInsert(db), afterInsert);
        var create = new CreateTransactionHandler(accounts, services.GetRequiredService<ICategoryRepository>(), transactions, _clock);
        var repository = operations is null ? Repository(scope) : operations(Repository(scope));

        return await new ConfirmFinanceOperationHandler(repository, References(scope), create, services.GetRequiredService<IUnitOfWork>(), _clock)
            .HandleAsync(user.Id, operationId, version, default);
    }

    // The same operation, claiming another result transaction.
    private static AssistantOperation<FinanceTransactionDraft> WithResult(AssistantOperation<FinanceTransactionDraft> operation, Guid transactionId) =>
        AssistantOperation<FinanceTransactionDraft>.Restore(operation.Id, operation.UserId, operation.ConversationId, operation.Kind, operation.Status,
            operation.DraftVersion, operation.PayloadVersion, operation.Draft, operation.ExpiresAtUtc, transactionId, operation.CreatedAtUtc,
            operation.UpdatedAtUtc, operation.ExecutedAtUtc);

    private static IFinanceOperationRepository Repository(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IFinanceOperationRepository>();

    private static FinanceDraftReferences References(AsyncServiceScope scope) =>
        new(scope.ServiceProvider.GetRequiredService<IAccountRepository>(), scope.ServiceProvider.GetRequiredService<ICategoryRepository>());

    private async Task<AssistantOperation<FinanceTransactionDraft>> StoredAsync(User user, Guid operationId)
    {
        await using var scope = fixture.CreateScope();
        return (await Repository(scope).GetAsync(user.Id, operationId, default))!;
    }

    // What assistant_operations holds for the conversation, oldest first.
    private Task<List<OperationRow>> RowsAsync(Guid conversationId) => QueryRowsAsync($"conversation_id = '{conversationId}'");

    private async Task<List<OperationRow>> QueryRowsAsync(string where)
    {
        await using var scope = fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.SqlQueryRaw<OperationRow>(
            $"""
            SELECT id AS "Id", status AS "Status", draft_version AS "DraftVersion", result_transaction_id AS "ResultTransactionId",
                   expires_at_utc AS "ExpiresAtUtc", updated_at_utc AS "UpdatedAtUtc"
            FROM assistant_operations WHERE {where} ORDER BY created_at_utc, id
            """).ToListAsync();
    }

    private async Task<int> TransactionCountAsync(User user, decimal? amount = null)
    {
        await using var scope = fixture.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database;

        return amount is { } value
            ? await database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM transactions WHERE user_id = {user.Id} AND amount = {value}").SingleAsync()
            : await database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM transactions WHERE user_id = {user.Id}").SingleAsync();
    }

    private async Task<Owner> NewOwnerAsync(string? timeZone = "Europe/Rome")
    {
        var user = User.CreateFromExternalIdentity(null, null, Now.AddDays(-30));

        if (timeZone is not null)
        {
            user.SetTimeZone(timeZone);
        }

        await PostgresAssert.InsertAsync(fixture, user);

        return new Owner(user, await NewAccountAsync(user), await NewCategoryAsync(user, CategoryType.Expense, "Ristoranti"),
            await NewCategoryAsync(user, CategoryType.Income, "Stipendio"));
    }

    private async Task<Guid> NewAccountAsync(User user, string currency = "EUR")
    {
        var account = Account.Create(user.Id, "Revolut", AccountType.BankAccount, currency, Now.AddDays(-1));
        await PostgresAssert.InsertAsync(fixture, account);

        return account.Id;
    }

    private async Task<Guid> NewCategoryAsync(User user, CategoryType type, string name)
    {
        var category = Category.Create(user.Id, name, type, null, Now.AddDays(-1));
        await PostgresAssert.InsertAsync(fixture, category);

        return category.Id;
    }

    private async Task<Guid> NewConversationAsync(User user)
    {
        var conversation = AssistantConversation.Create(user.Id, Guid.CreateVersion7(), Now);
        await PostgresAssert.InsertAsync(fixture, conversation);

        return conversation.Id;
    }

    private async Task Execute(string sql)
    {
        await using var scope = fixture.CreateScope();
        await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.ExecuteSqlRawAsync(sql);
    }

    private static async Task AssertRejectedAsync(string code, string field, Task<AssistantResult<FinanceOperationOutcome>> request)
    {
        var result = await request;
        Assert.Equal((AssistantResultStatus.Rejected, code, field), (result.Status, result.Code, result.Field));
    }

    // Polls PostgreSQL's own lock state until some backend waits on a lock held by `blocker`.
    private async Task WaitUntilBlockedByAsync(int blocker)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        while (true)
        {
            await using (var scope = fixture.CreateScope())
            {
                if (await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.SqlQuery<int>(
                        $"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE {blocker} = ANY(pg_blocking_pids(pid))")
                        .SingleAsync(timeout.Token) > 0)
                {
                    return;
                }
            }

            await Task.Delay(10, timeout.Token);
        }
    }

    // Holds a confirmation inside its transaction: ReachedAsync returns that connection's backend pid once it
    // holds the operation's row lock; Release lets it continue.
    private sealed class Pause
    {
        private readonly TaskCompletionSource<int> _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task HoldAsync(LifeOSDbContext db)
        {
            _reached.TrySetResult(await db.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync());
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }

        public Task<int> ReachedAsync() => _reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        public void Release() => _release.TrySetResult();
    }

    private sealed class HookedAccountRepository(IAccountRepository inner, Func<Task>? afterLookup) : IAccountRepository
    {
        private Func<Task>? _afterLookup = afterLookup;

        public Task AddAsync(Account account, OpeningBalance? openingBalance, CancellationToken cancellationToken) =>
            inner.AddAsync(account, openingBalance, cancellationToken);

        public async Task<Account?> GetByIdAsync(Guid userId, Guid id, CancellationToken cancellationToken)
        {
            var account = await inner.GetByIdAsync(userId, id, cancellationToken);

            if (Interlocked.Exchange(ref _afterLookup, null) is { } hook)
            {
                await hook();
            }

            return account;
        }

        public Task<IReadOnlyList<Account>> GetAllAsync(Guid userId, CancellationToken cancellationToken) => inner.GetAllAsync(userId, cancellationToken);

        public Task<bool> AnyAsync(Guid userId, CancellationToken cancellationToken) => inner.AnyAsync(userId, cancellationToken);

        public Task<bool> TryUpdateAsync(Account account, CancellationToken cancellationToken) => inner.TryUpdateAsync(account, cancellationToken);

        public Task<AccountDeleteOutcome> DeleteAsync(Guid userId, Guid accountId, CancellationToken cancellationToken) =>
            inner.DeleteAsync(userId, accountId, cancellationToken);
    }

    // beforeLock runs once, inside the confirmation's database transaction but before its row lock; update
    // replaces every conditional update.
    private sealed class HookedOperationRepository(
        IFinanceOperationRepository inner,
        Func<Task>? beforeLock = null,
        Func<AssistantOperation<FinanceTransactionDraft>, AssistantOperationStatus, int, CancellationToken, Task<bool>>? update = null)
        : IFinanceOperationRepository
    {
        private Func<Task>? _beforeLock = beforeLock;

        public Task<FinanceOperationAddOutcome> TryAddAsync(AssistantOperation<FinanceTransactionDraft> operation, CancellationToken cancellationToken) =>
            inner.TryAddAsync(operation, cancellationToken);

        public Task<AssistantOperation<FinanceTransactionDraft>?> GetAsync(Guid userId, Guid operationId, CancellationToken cancellationToken) =>
            inner.GetAsync(userId, operationId, cancellationToken);

        public Task<AssistantOperation<FinanceTransactionDraft>?> GetOpenAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken) =>
            inner.GetOpenAsync(userId, conversationId, cancellationToken);

        public Task<bool> TryUpdateAsync(AssistantOperation<FinanceTransactionDraft> operation, AssistantOperationStatus expectedStatus,
            int expectedDraftVersion, CancellationToken cancellationToken) =>
            update is null
                ? inner.TryUpdateAsync(operation, expectedStatus, expectedDraftVersion, cancellationToken)
                : update(operation, expectedStatus, expectedDraftVersion, cancellationToken);

        public async Task<bool> TryLockForConfirmationAsync(Guid userId, Guid operationId, int expectedDraftVersion, DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _beforeLock, null) is { } hook)
            {
                await hook();
            }

            return await inner.TryLockForConfirmationAsync(userId, operationId, expectedDraftVersion, now, cancellationToken);
        }
    }

    private sealed class HookedTransactionRepository(ITransactionRepository inner, Func<Task>? beforeAdd, Func<Task>? afterAdd) : ITransactionRepository
    {
        public async Task<bool> TryAddAsync(Transaction transaction, CancellationToken cancellationToken)
        {
            if (beforeAdd is not null)
            {
                await beforeAdd();
            }

            var added = await inner.TryAddAsync(transaction, cancellationToken);

            if (afterAdd is not null)
            {
                await afterAdd();
            }

            return added;
        }

        public Task<Transaction?> GetByIdAsync(Guid userId, Guid id, CancellationToken cancellationToken) => inner.GetByIdAsync(userId, id, cancellationToken);

        public Task<TransactionUpdateOutcome> TryUpdateAsync(Transaction transaction, CancellationToken cancellationToken) =>
            inner.TryUpdateAsync(transaction, cancellationToken);

        public Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken cancellationToken) => inner.DeleteAsync(userId, id, cancellationToken);

        public Task<bool> AnyReferencingAccountAsync(Guid userId, Guid accountId, CancellationToken cancellationToken) =>
            inner.AnyReferencingAccountAsync(userId, accountId, cancellationToken);

        public Task<bool> AnyReferencingCategoryAsync(Guid userId, Guid categoryId, CancellationToken cancellationToken) =>
            inner.AnyReferencingCategoryAsync(userId, categoryId, cancellationToken);

        public Task<IReadOnlyList<Transaction>> GetRecentAsync(Guid userId, int limit, CancellationToken cancellationToken) =>
            inner.GetRecentAsync(userId, limit, cancellationToken);

        public Task<IReadOnlyList<Transaction>> GetOccurredBeforeAsync(Guid userId, DateTimeOffset beforeUtc, CancellationToken cancellationToken) =>
            inner.GetOccurredBeforeAsync(userId, beforeUtc, cancellationToken);

        public Task<IReadOnlyList<Transaction>> GetByOccurredRangeAsync(Guid userId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
            CancellationToken cancellationToken) =>
            inner.GetByOccurredRangeAsync(userId, fromUtc, toUtc, cancellationToken);
    }
}
