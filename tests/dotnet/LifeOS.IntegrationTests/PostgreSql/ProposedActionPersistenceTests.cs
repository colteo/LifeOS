using LifeOS.Application.ActionAgent;
using LifeOS.Application.Finance.Budgets;
using LifeOS.Application.Finance.Transactions;
using LifeOS.Application.Persistence;
using LifeOS.Application.WeeklyReviews;
using LifeOS.Domain.ActionAgent;
using LifeOS.Domain.Finance.Budgets;
using LifeOS.Domain.Users;
using LifeOS.Domain.WeeklyReviews;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// AI-002 against real PostgreSQL: the proposed_actions schema (columns, indexes, FKs, checks backing the
// state machine), the repository (one open proposal per review, conditional status moves that never touch
// the payload, owner-scoped reads, cascades), and the agent/approval use cases end to end with the real
// repositories, the real budget command, real transactions and a scripted model. Every assertion is scoped
// to this test's own users (the database is shared by the PostgreSQL collection).
[Collection(PostgreSqlCollection.Name)]
public class ProposedActionPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateOnly WeekEnd = new(2026, 10, 4);

    // Wednesday 7 October 2026 in Rome: proposals may target October or November 2026.
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);

    // ---- Schema ----

    [Fact]
    public async Task Migration_CreatesTheProposalsTable_WithItsInvariants()
    {
        await using var scope = fixture.CreateScope();
        var database = Db(scope).Database;

        Assert.Contains(await database.GetAppliedMigrationsAsync(), id => id.EndsWith("_AddProposedActions", StringComparison.Ordinal));
        Assert.False(database.HasPendingModelChanges());

        Assert.Equal(
            [
                "id uuid NOT NULL", "user_id uuid NOT NULL", "review_id uuid NOT NULL", "action_type character varying(64) NOT NULL",
                "status character varying(16) NOT NULL", "payload_version integer NOT NULL", "payload jsonb NOT NULL",
                "rationale character varying(300) NOT NULL", "provider character varying(100) NOT NULL", "model character varying(100) NOT NULL",
                "prompt_version character varying(100) NOT NULL", "tool_schema_version character varying(100) NOT NULL", "tool_calls text[] NOT NULL",
                "step_count integer NOT NULL", "created_at_utc timestamp with time zone NOT NULL", "decided_at_utc timestamp with time zone NULL",
                "executed_at_utc timestamp with time zone NULL", "failure_code character varying(64) NULL"
            ],
            await Strings(database,
                """
                SELECT attname || ' ' || format_type(atttypid, atttypmod) || CASE WHEN attnotnull THEN ' NOT NULL' ELSE ' NULL' END AS "Value"
                FROM pg_attribute WHERE attrelid = 'proposed_actions'::regclass AND attnum > 0 AND NOT attisdropped ORDER BY attnum
                """));
        Assert.Equal(
            [
                "CREATE INDEX ix_proposed_actions_review_created ON public.proposed_actions USING btree (review_id, created_at_utc)",
                "CREATE INDEX ix_proposed_actions_user ON public.proposed_actions USING btree (user_id)",
                "CREATE UNIQUE INDEX \"PK_proposed_actions\" ON public.proposed_actions USING btree (id)",
                "CREATE UNIQUE INDEX ux_proposed_actions_review_open ON public.proposed_actions USING btree (review_id) WHERE ((status)::text = ANY ((ARRAY['Pending'::character varying, 'Approved'::character varying])::text[]))"
            ],
            (await Strings(database, "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'proposed_actions'")).Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                "FK_proposed_actions_users_user_id c", "FK_proposed_actions_weekly_reviews_review_id c", "ck_proposed_actions_action_type",
                "ck_proposed_actions_payload", "ck_proposed_actions_rationale", "ck_proposed_actions_run", "ck_proposed_actions_state",
                "ck_proposed_actions_status"
            ],
            (await Strings(database,
                """
                SELECT conname::text || CASE WHEN contype = 'f' THEN ' ' || confdeltype::text ELSE '' END AS "Value" FROM pg_constraint
                WHERE conrelid = 'proposed_actions'::regclass AND contype IN ('c', 'f')
                """)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheDatabase_BacksTheStateMachineAndTheAllowlist()
    {
        var (user, review) = await UserWithReviewAsync();
        var proposal = Pending(user.Id, review.Id);
        Assert.True(await AddAsync(proposal));
        var id = proposal.Id;

        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_proposed_actions_state", () =>
            Execute($"UPDATE proposed_actions SET status = 'Executed' WHERE id = '{id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_proposed_actions_state", () =>
            Execute($"UPDATE proposed_actions SET status = 'Failed', decided_at_utc = now() WHERE id = '{id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_proposed_actions_state", () =>
            Execute($"UPDATE proposed_actions SET decided_at_utc = now() WHERE id = '{id}'"));
        // An unknown status breaks both the status list and the state shape; PostgreSQL may name either.
        await PostgresAssert.ViolatesOneOfAsync(PostgresAssert.CheckViolation, ["ck_proposed_actions_status", "ck_proposed_actions_state"], () =>
            Execute($"UPDATE proposed_actions SET status = 'Done' WHERE id = '{id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_proposed_actions_action_type", () =>
            Execute($"UPDATE proposed_actions SET action_type = 'DeleteAccount' WHERE id = '{id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_proposed_actions_payload", () =>
            Execute($"UPDATE proposed_actions SET payload = '[]'::jsonb WHERE id = '{id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_proposed_actions_rationale", () =>
            Execute($"UPDATE proposed_actions SET rationale = '  ' WHERE id = '{id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_proposed_actions_run", () =>
            Execute($"UPDATE proposed_actions SET step_count = 0 WHERE id = '{id}'"));
    }

    // ---- Repository ----

    [Fact]
    public async Task AReviewHasAtMostOneOpenProposal_ADecidedOneMakesRoomForANewOne()
    {
        var (user, review) = await UserWithReviewAsync();
        var first = Pending(user.Id, review.Id);

        Assert.True(await AddAsync(first));
        Assert.False(await AddAsync(Pending(user.Id, review.Id)));
        Assert.True(await UpdateAsync(first.Approve(Now), ProposedActionStatus.Pending));
        Assert.False(await AddAsync(Pending(user.Id, review.Id)));
        Assert.True(await UpdateAsync(first.Approve(Now).MarkExecuted(Now), ProposedActionStatus.Approved));

        var second = Pending(user.Id, review.Id, created: Now.AddMinutes(1));
        Assert.True(await AddAsync(second));

        await using var scope = fixture.CreateScope();
        Assert.Equal(second.Id, (await Repository(scope).GetLatestForReviewAsync(user.Id, review.Id, default))!.Id);
        Assert.Equal(2, await CountAsync(review.Id));
    }

    [Fact]
    public async Task ConcurrentInserts_StoreExactlyOneOpenProposal()
    {
        var (user, review) = await UserWithReviewAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => AddAsync(Pending(user.Id, review.Id))));

        Assert.Single(results, added => added);
        Assert.Equal(1, await CountAsync(review.Id));
    }

    [Fact]
    public async Task StatusMoves_AreConditional_AndNeverTouchThePayload()
    {
        var (user, review) = await UserWithReviewAsync();
        var proposal = Pending(user.Id, review.Id);
        Assert.True(await AddAsync(proposal));
        var payloadBefore = await PayloadAsync(proposal.Id);

        Assert.False(await UpdateAsync(proposal.Approve(Now).MarkExecuted(Now), ProposedActionStatus.Approved));
        Assert.True(await UpdateAsync(proposal.Reject(Now), ProposedActionStatus.Pending));
        Assert.False(await UpdateAsync(proposal.Approve(Now), ProposedActionStatus.Pending));

        await using var scope = fixture.CreateScope();
        var stored = (await Repository(scope).GetAsync(user.Id, proposal.Id, default))!;
        Assert.Equal((ProposedActionStatus.Rejected, Now), (stored.Status, stored.DecidedAtUtc!.Value));
        Assert.Equal(proposal.Payload, stored.Payload);
        Assert.Equal(proposal.Run.ToolCalls, stored.Run.ToolCalls);
        Assert.Equal(payloadBefore, await PayloadAsync(proposal.Id));
        Assert.Contains("\"proposedAmount\":500", payloadBefore.Replace(" ", ""));
    }

    [Fact]
    public async Task Reads_AreScopedToTheOwner_AndUnknownPayloadVersionsAreNeverGuessed()
    {
        var (user, review) = await UserWithReviewAsync();
        var other = await NewUserAsync();
        var proposal = Pending(user.Id, review.Id);
        Assert.True(await AddAsync(proposal));

        await using (var scope = fixture.CreateScope())
        {
            Assert.NotNull(await Repository(scope).GetAsync(user.Id, proposal.Id, default));
            Assert.Null(await Repository(scope).GetAsync(other.Id, proposal.Id, default));
            Assert.Null(await Repository(scope).GetLatestForReviewAsync(other.Id, review.Id, default));
            Assert.False(await Repository(scope).TryUpdateStatusAsync(
                ProposedAction.Restore(proposal.Id, other.Id, review.Id, proposal.ActionType, 1, proposal.Payload, proposal.Rationale, proposal.Run,
                    ProposedActionStatus.Rejected, Now, Now, null, null), ProposedActionStatus.Pending, default));
        }

        await Execute($"UPDATE proposed_actions SET payload_version = 99 WHERE id = '{proposal.Id}'");
        await using var later = fixture.CreateScope();
        await Assert.ThrowsAsync<NotSupportedException>(() => Repository(later).GetAsync(user.Id, proposal.Id, default));
    }

    [Fact]
    public async Task AProposalForAMissingReview_IsNotStored_AndDeletingTheUserDeletesItsProposals()
    {
        var (user, review) = await UserWithReviewAsync();
        Assert.False(await AddAsync(Pending(user.Id, Guid.CreateVersion7())));
        Assert.True(await AddAsync(Pending(user.Id, review.Id)));

        await Execute($"DELETE FROM users WHERE id = '{user.Id}'");

        Assert.Equal(0, await CountAsync(review.Id));
    }

    // ---- End to end (real repositories and transactions, scripted model) ----

    [Fact]
    public async Task TheAgent_StoresAPendingProposal_AndTheBudgetRowIsUntouchedUntilApproval()
    {
        var (user, review) = await UserWithReviewAsync();
        await SetBudgetAsync(user.Id, 400m);
        var before = await BudgetRowAsync(user.Id);
        var model = new FakeActionAgentModel();
        model.Script(FakeActionAgentModel.ReadReview(), FakeActionAgentModel.ReadBudget(), FakeActionAgentModel.Propose(500m));

        var run = await RunAsync(model, user.Id, review.Id);

        Assert.Equal(ActionAgentRunStatus.Proposed, run.Status);
        Assert.Equal(new MonthlyBudgetAdjustment(2026, 10, "EUR", 400m, 500m), run.Proposal!.Payload);
        Assert.Equal(before, await BudgetRowAsync(user.Id));

        var approved = await ApproveAsync(user.Id, run.Proposal.Id);

        Assert.Equal(ProposedActionStatus.Executed, approved.Proposal!.Status);
        Assert.Equal(500m, await BudgetAmountAsync(user.Id));
        Assert.Equal(before.Replace("400.0000", "500.0000"), await BudgetRowAsync(user.Id)); // same row (id kept), new amount
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AiFailures_NeverMutateFinance(bool unavailable)
    {
        var (user, review) = await UserWithReviewAsync();
        await SetBudgetAsync(user.Id, 400m);
        var before = await BudgetRowAsync(user.Id);
        var model = new FakeActionAgentModel();
        model.Script(FakeActionAgentModel.ReadBudget(), unavailable ? FakeActionAgentModel.Unavailable : FakeActionAgentModel.Call("set_monthly_budget"));

        var run = await RunAsync(model, user.Id, review.Id);

        Assert.NotEqual(ActionAgentRunStatus.Proposed, run.Status);
        Assert.Equal(before, await BudgetRowAsync(user.Id));
        Assert.Equal(0, await CountAsync(review.Id));
    }

    [Fact]
    public async Task ConcurrentApprovals_ExecuteExactlyOnce()
    {
        var (user, review) = await UserWithReviewAsync();
        await SetBudgetAsync(user.Id, 400m);
        var proposal = Pending(user.Id, review.Id);
        Assert.True(await AddAsync(proposal));

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ApproveAsync(user.Id, proposal.Id)));

        Assert.All(results, result => Assert.Equal(ProposedActionStatus.Executed, result.Proposal!.Status));
        Assert.Single(results.Select(result => result.Proposal!.ExecutedAtUtc).Distinct());
        Assert.Equal(500m, await BudgetAmountAsync(user.Id));
    }

    [Fact]
    public async Task AChangedBudget_FailsTheProposal_AndTheTransactionWritesNothing()
    {
        var (user, review) = await UserWithReviewAsync();
        await SetBudgetAsync(user.Id, 400m);
        var proposal = Pending(user.Id, review.Id);
        Assert.True(await AddAsync(proposal));
        await SetBudgetAsync(user.Id, 420m);
        var before = await BudgetRowAsync(user.Id);

        var result = await ApproveAsync(user.Id, proposal.Id);

        Assert.Equal((ProposedActionStatus.Failed, "budget_changed"), (result.Proposal!.Status, result.Proposal.FailureCode));
        Assert.Equal(before, await BudgetRowAsync(user.Id));
        Assert.Equal(ProposedActionStatus.Failed, (await ApproveAsync(user.Id, proposal.Id)).Proposal!.Status);
    }

    [Fact]
    public async Task ARejectedProposal_IsNeverExecuted()
    {
        var (user, review) = await UserWithReviewAsync();
        await SetBudgetAsync(user.Id, 400m);
        var proposal = Pending(user.Id, review.Id);
        Assert.True(await AddAsync(proposal));

        await using (var scope = fixture.CreateScope())
        {
            await new RejectProposedActionHandler(Repository(scope), new FixedTimeProvider(Now)).HandleAsync(user.Id, proposal.Id, default);
        }

        var approved = await ApproveAsync(user.Id, proposal.Id);

        Assert.Equal(ProposedActionResultStatus.Conflict, approved.Status);
        Assert.Equal(400m, await BudgetAmountAsync(user.Id));
    }

    // ---- Helpers ----

    private async Task<ActionAgentRunResult> RunAsync(FakeActionAgentModel model, Guid userId, Guid reviewId)
    {
        await using var scope = fixture.CreateScope();
        var services = scope.ServiceProvider;
        var clock = new FixedTimeProvider(Now);
        var budgetStatus = new GetMonthlyBudgetHandler(services.GetRequiredService<IMonthlyBudgetRepository>(),
            services.GetRequiredService<ITransactionRepository>(), clock, null, services.GetRequiredService<IFinancePlanningSnapshotRepository>());

        return await new RunActionAgentHandler(services.GetRequiredService<IWeeklyReviewRepository>(), Repository(scope),
            new ActionAgentTools(budgetStatus), model, clock).HandleAsync(userId, reviewId, default);
    }

    private async Task<ProposedActionResult> ApproveAsync(Guid userId, Guid proposalId)
    {
        await using var scope = fixture.CreateScope();
        var services = scope.ServiceProvider;
        var budgets = services.GetRequiredService<IMonthlyBudgetRepository>();

        return await new ApproveProposedActionHandler(Repository(scope), budgets, new SetMonthlyBudgetHandler(budgets),
            services.GetRequiredService<IUnitOfWork>(), new FixedTimeProvider(Now)).HandleAsync(userId, proposalId, default);
    }

    private static ProposedAction Pending(Guid userId, Guid reviewId, DateTimeOffset? created = null) =>
        ProposedAction.ProposeBudgetAdjustment(userId, reviewId, new MonthlyBudgetAdjustment(2026, 10, "EUR", 400m, 500m),
            "You spent 380 of 400 EUR.", new AgentRunIdentity("fake", "fake-model", "action-agent-v1", ActionAgentTools.ToolSchemaVersion,
                [ActionAgentTools.GetWeeklyReview, ActionAgentTools.GetBudgetStatus], 3), created ?? Now);

    private async Task<bool> AddAsync(ProposedAction proposal)
    {
        await using var scope = fixture.CreateScope();
        return await Repository(scope).TryAddAsync(proposal, default);
    }

    private async Task<bool> UpdateAsync(ProposedAction updated, ProposedActionStatus expected)
    {
        await using var scope = fixture.CreateScope();
        return await Repository(scope).TryUpdateStatusAsync(updated, expected, default);
    }

    private async Task SetBudgetAsync(Guid userId, decimal amount)
    {
        await using var scope = fixture.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMonthlyBudgetRepository>().SetAsync(MonthlyBudget.Create(userId, 2026, 10, "EUR", amount), default);
    }

    private async Task<decimal?> BudgetAmountAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IMonthlyBudgetRepository>().GetAsync(userId, 2026, 10, "EUR", default))?.Amount;
    }

    private async Task<string> BudgetRowAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();
        return await Db(scope).Database.SqlQuery<string>(
            $"SELECT row_to_json(b)::text AS \"Value\" FROM monthly_budgets AS b WHERE user_id = {userId} AND year = 2026 AND month = 10").SingleAsync();
    }

    private async Task<string> PayloadAsync(Guid proposalId)
    {
        await using var scope = fixture.CreateScope();
        return await Db(scope).Database.SqlQuery<string>($"SELECT payload::text AS \"Value\" FROM proposed_actions WHERE id = {proposalId}").SingleAsync();
    }

    private async Task<int> CountAsync(Guid reviewId)
    {
        await using var scope = fixture.CreateScope();
        return await Db(scope).Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM proposed_actions WHERE review_id = {reviewId}").SingleAsync();
    }

    private async Task<(User User, WeeklyReview Review)> UserWithReviewAsync()
    {
        var user = await NewUserAsync();
        var review = WeeklyReview.Create(user.Id, WeekEnd, "Europe/Rome", Now.AddDays(-3), new WeeklyReviewSnapshot(
            new WeeklyFinanceSummary([new WeeklyCurrencySummary("EUR", 380m, 0m, -380m, [new WeeklyExpenseCategory("Groceries", 380m)])]),
            new WeeklyGymSummary(0, 0, 0, 0, []),
            new WeeklyNutritionSummary(0, 0, 0, 0, 0m, 0m, 0m, 0m, [])));

        await using var scope = fixture.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IWeeklyReviewRepository>().TryAddAsync(review, default));

        return (user, review);
    }

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now.AddDays(-60));
        await PostgresAssert.InsertAsync(fixture, user);

        return user;
    }

    private async Task Execute(string sql)
    {
        await using var scope = fixture.CreateScope();
        await Db(scope).Database.ExecuteSqlRawAsync(sql);
    }

    private static IProposedActionRepository Repository(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IProposedActionRepository>();

    private static LifeOSDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();

    private static Task<List<string>> Strings(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database, string sql) =>
        database.SqlQueryRaw<string>(sql).ToListAsync();
}
