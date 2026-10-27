using LifeOS.Domain.Assistant;
using LifeOS.Domain.Finance.Assistant;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.UnitTests.Fakes;
using FinanceOperation = LifeOS.Domain.Assistant.AssistantOperation<LifeOS.Domain.Finance.Assistant.FinanceTransactionDraft>;

namespace LifeOS.UnitTests.Assistant;

// AI-007 F2: the operation state machine — completeness drives Collecting ⇄ AwaitingConfirmation, versions
// move only on substantive revisions, the 24 h expiry and its exact boundary, terminal states.
public class AssistantOperationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Conversation = Guid.CreateVersion7();
    private static readonly TimeSpan Microsecond = TimeSpan.FromTicks(TimeSpan.TicksPerMicrosecond);

    [Fact]
    public void Create_StatusFollowsCompleteness_VersionOne_Expiring24hLater()
    {
        var incomplete = Create(Draft(withAccount: false));
        var complete = Create(Draft());

        Assert.Equal((AssistantOperationStatus.Collecting, 1), (incomplete.Status, incomplete.DraftVersion));
        Assert.Equal((AssistantOperationStatus.AwaitingConfirmation, 1), (complete.Status, complete.DraftVersion));
        Assert.Equal(7, complete.Id.Version);
        Assert.Equal((Now, Now, Now.AddHours(24)), (complete.CreatedAtUtc, complete.UpdatedAtUtc, complete.ExpiresAtUtc));
        Assert.Equal((AssistantOperationKinds.FinanceTransactionCreate, FinanceTransactionDraft.PayloadVersion),
            (complete.Kind, complete.PayloadVersion));
        Assert.Null(complete.ResultId);
        Assert.True(complete.IsOpen);
    }

    [Fact]
    public void Create_RequiresOwnerConversationAndAKnownKind()
    {
        Assert.Throws<ArgumentException>(() => FinanceOperation.Create(Guid.Empty, Conversation, AssistantOperationKinds.FinanceTransactionCreate, 1, Draft(), Now));
        Assert.Throws<ArgumentException>(() => FinanceOperation.Create(TestUsers.A, Guid.Empty, AssistantOperationKinds.FinanceTransactionCreate, 1, Draft(), Now));
        Assert.Throws<ArgumentException>(() => FinanceOperation.Create(TestUsers.A, Conversation, "finance.transfer.create", 1, Draft(), Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => FinanceOperation.Create(TestUsers.A, Conversation, AssistantOperationKinds.FinanceTransactionCreate, 0, Draft(), Now));
    }

    [Fact]
    public void ASubstantiveRevision_IsTheNextVersion_RenewsTheExpiry_AndRecomputesTheStatus()
    {
        var operation = Create(Draft());
        var later = Now.AddHours(5);

        Assert.True(operation.ReviseDraft(Draft(withAccount: false), later));

        // Removing the account makes the draft incomplete again.
        Assert.Equal((AssistantOperationStatus.Collecting, 2, later.AddHours(24), later), (operation.Status, operation.DraftVersion,
            operation.ExpiresAtUtc, operation.UpdatedAtUtc));
        Assert.Equal(Now, operation.CreatedAtUtc);

        Assert.True(operation.ReviseDraft(Draft(), later.AddMinutes(1)));
        Assert.Equal((AssistantOperationStatus.AwaitingConfirmation, 3), (operation.Status, operation.DraftVersion));
    }

    [Fact]
    public void ARevisionEqualToTheDraft_ChangesNothing()
    {
        var operation = Create(Draft());

        // A new instance with the same values (5 and 5.00 are the same amount).
        Assert.False(operation.ReviseDraft(Draft(amount: 5.00m), Now.AddHours(3)));

        Assert.Equal((1, Now.AddHours(24), Now), (operation.DraftVersion, operation.ExpiresAtUtc, operation.UpdatedAtUtc));
    }

    [Fact]
    public void Expiry_EndIsExclusive_AtExactly24Hours()
    {
        var justBefore = Now.AddHours(24) - Microsecond;
        var atExpiry = Now.AddHours(24);

        var operation = Create(Draft());
        Assert.False(operation.IsExpiredAt(justBefore));
        Assert.False(operation.ExpireIfDue(justBefore));
        operation.MarkExecuted(Guid.CreateVersion7(), justBefore);
        Assert.Equal(AssistantOperationStatus.Executed, operation.Status);

        var expiring = Create(Draft());
        Assert.True(expiring.IsExpiredAt(atExpiry));
        Assert.Throws<InvalidOperationException>(() => expiring.MarkExecuted(Guid.CreateVersion7(), atExpiry));
        Assert.Throws<InvalidOperationException>(() => expiring.ReviseDraft(Draft(amount: 7m), atExpiry));
        Assert.Throws<InvalidOperationException>(() => expiring.Cancel(atExpiry));
        Assert.True(expiring.ExpireIfDue(atExpiry));
        Assert.Equal((AssistantOperationStatus.Expired, 1, atExpiry), (expiring.Status, expiring.DraftVersion, expiring.UpdatedAtUtc));
        Assert.False(expiring.ExpireIfDue(atExpiry.AddDays(1)));
    }

    [Fact]
    public void MarkExecuted_RequiresACompleteDraftAwaitingConfirmation_AndRecordsTheTransaction()
    {
        var collecting = Create(Draft(withAccount: false));
        Assert.Throws<InvalidOperationException>(() => collecting.MarkExecuted(Guid.CreateVersion7(), Now));

        var operation = Create(Draft());
        Assert.Throws<ArgumentException>(() => operation.MarkExecuted(Guid.Empty, Now));
        var transactionId = Guid.CreateVersion7();
        operation.MarkExecuted(transactionId, Now.AddMinutes(1));

        Assert.Equal((AssistantOperationStatus.Executed, transactionId, Now.AddMinutes(1)), (operation.Status, operation.ResultId,
            operation.ExecutedAtUtc));
        Assert.Equal(1, operation.DraftVersion);
    }

    [Theory]
    [InlineData(AssistantOperationStatus.Executed)]
    [InlineData(AssistantOperationStatus.Cancelled)]
    [InlineData(AssistantOperationStatus.Expired)]
    public void ATerminalOperation_RefusesEveryChange(AssistantOperationStatus terminal)
    {
        var operation = Create(Draft());

        switch (terminal)
        {
            case AssistantOperationStatus.Executed: operation.MarkExecuted(Guid.CreateVersion7(), Now); break;
            case AssistantOperationStatus.Cancelled: operation.Cancel(Now); break;
            default: operation.ExpireIfDue(Now.AddDays(1)); break;
        }

        Assert.Equal(terminal, operation.Status);
        Assert.False(operation.IsOpen);
        Assert.Throws<InvalidOperationException>(() => operation.ReviseDraft(Draft(amount: 9m), Now));
        Assert.Throws<InvalidOperationException>(() => operation.MarkExecuted(Guid.CreateVersion7(), Now));
        Assert.Throws<InvalidOperationException>(() => operation.Cancel(Now));
        Assert.False(operation.ExpireIfDue(Now.AddDays(2)));
    }

    [Fact]
    public void Cancel_WorksFromBothOpenStates()
    {
        var collecting = Create(Draft(withAccount: false));
        var awaiting = Create(Draft());

        collecting.Cancel(Now.AddMinutes(1));
        awaiting.Cancel(Now.AddMinutes(1));

        Assert.Equal((AssistantOperationStatus.Cancelled, AssistantOperationStatus.Cancelled), (collecting.Status, awaiting.Status));
    }

    [Fact]
    public void Transitions_AreExactlyTheStateMachine()
    {
        AssistantOperationStatus[] open = [AssistantOperationStatus.Collecting, AssistantOperationStatus.AwaitingConfirmation];
        var allowed = new HashSet<(AssistantOperationStatus, AssistantOperationStatus)>
        {
            (AssistantOperationStatus.Collecting, AssistantOperationStatus.Collecting),
            (AssistantOperationStatus.Collecting, AssistantOperationStatus.AwaitingConfirmation),
            (AssistantOperationStatus.AwaitingConfirmation, AssistantOperationStatus.Collecting),
            (AssistantOperationStatus.AwaitingConfirmation, AssistantOperationStatus.AwaitingConfirmation),
            (AssistantOperationStatus.AwaitingConfirmation, AssistantOperationStatus.Executed),
        };

        foreach (var from in open)
        {
            allowed.Add((from, AssistantOperationStatus.Cancelled));
            allowed.Add((from, AssistantOperationStatus.Expired));
        }

        foreach (var from in Enum.GetValues<AssistantOperationStatus>())
        {
            foreach (var to in Enum.GetValues<AssistantOperationStatus>())
            {
                Assert.True(allowed.Contains((from, to)) == FinanceOperation.CanTransition(from, to), $"{from} → {to}");
            }
        }
    }

    [Fact]
    public void Restore_RefusesInconsistentStoredStates()
    {
        var id = Guid.CreateVersion7();

        FinanceOperation Restore(AssistantOperationStatus status, FinanceTransactionDraft draft, Guid? transactionId, DateTimeOffset? executedAt) =>
            FinanceOperation.Restore(id, TestUsers.A, Conversation, AssistantOperationKinds.FinanceTransactionCreate, status, 1, 1, draft,
                Now.AddHours(24), transactionId, Now, Now, executedAt);

        Assert.Equal(AssistantOperationStatus.Executed, Restore(AssistantOperationStatus.Executed, Draft(), Guid.CreateVersion7(), Now).Status);
        Assert.Throws<ArgumentException>(() => Restore(AssistantOperationStatus.Executed, Draft(), null, Now));
        Assert.Throws<ArgumentException>(() => Restore(AssistantOperationStatus.Cancelled, Draft(), Guid.CreateVersion7(), Now));
        Assert.Throws<ArgumentException>(() => Restore(AssistantOperationStatus.AwaitingConfirmation, Draft(withAccount: false), null, null));
    }

    private static FinanceOperation Create(FinanceTransactionDraft draft) =>
        FinanceOperation.Create(TestUsers.A, Conversation, AssistantOperationKinds.FinanceTransactionCreate, FinanceTransactionDraft.PayloadVersion, draft, Now);

    private static readonly Guid Account = Guid.CreateVersion7();
    private static readonly Guid Category = Guid.CreateVersion7();

    // A complete draft unless the account is left out.
    internal static FinanceTransactionDraft Draft(decimal amount = 5m, bool withAccount = true) =>
        FinanceTransactionDraft.Create(TransactionType.Expense, amount, null, withAccount ? Account : null, Category, FinanceDraftCategorySource.User,
            "colazione con Pamela", null, null,
            FinanceDraftOccurrence.Create(new DateOnly(2026, 10, 24), new TimeOnly(11, 0), FinanceDraftDateSource.Today,
                FinanceDraftTimeSource.CurrentTime, "Europe/Rome", Now));
}
