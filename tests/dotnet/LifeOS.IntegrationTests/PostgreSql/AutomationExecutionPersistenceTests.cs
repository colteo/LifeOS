using LifeOS.Application.Automation;
using LifeOS.Domain.Automation;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// AUTO-001 WP2 against real PostgreSQL: the automation_executions schema, the store's atomic claims
// (unique occurrence, FOR UPDATE SKIP LOCKED), lease takeover, fenced completion and terminal states,
// and the tick engine end to end with test-only handlers. Every test uses its own automation type, so
// tests sharing the database never claim each other's rows.
[Collection(PostgreSqlCollection.Name)]
public class AutomationExecutionPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Due = new(2026, 10, 4, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Due.AddMinutes(3);
    private static readonly TimeSpan Lease = AutomationExecutionPolicy.Lease;
    private const int MaxAttempts = AutomationExecutionPolicy.MaxAttempts;

    private readonly string _type = $"Test-{Guid.NewGuid():N}";

    // ---- Schema ----

    [Fact]
    public async Task Migration_CreatesTheAutomationExecutionsTable()
    {
        await using var scope = fixture.CreateScope();
        var database = Database(scope);

        Assert.Contains(await database.GetAppliedMigrationsAsync(), id => id.EndsWith("_AddAutomationExecutions", StringComparison.Ordinal));
        Assert.False(database.HasPendingModelChanges());

        Assert.Equal(
            [
                "id uuid NOT NULL", "user_id uuid NOT NULL", "automation_type character varying(64) NOT NULL",
                "occurrence_key character varying(64) NOT NULL", "time_zone_id character varying(64) NOT NULL",
                "scheduled_for_utc timestamp with time zone NOT NULL", "expires_at_utc timestamp with time zone NOT NULL",
                "status character varying(16) NOT NULL", "attempt_count integer NOT NULL",
                "lease_expires_at_utc timestamp with time zone NULL", "next_attempt_at_utc timestamp with time zone NULL",
                "last_failure_code character varying(64) NULL", "result_id uuid NULL",
                "created_at_utc timestamp with time zone NOT NULL", "started_at_utc timestamp with time zone NOT NULL",
                "completed_at_utc timestamp with time zone NULL"
            ],
            await Strings(database,
                """
                SELECT attname || ' ' || format_type(atttypid, atttypmod) || CASE WHEN attnotnull THEN ' NOT NULL' ELSE ' NULL' END AS "Value"
                FROM pg_attribute WHERE attrelid = 'automation_executions'::regclass AND attnum > 0 AND NOT attisdropped ORDER BY attnum
                """));

        Assert.Equal(
            [
                "CREATE INDEX ix_automation_executions_retry ON public.automation_executions USING btree (next_attempt_at_utc) WHERE ((status)::text = 'FailedRetryable'::text)",
                "CREATE INDEX ix_automation_executions_stale ON public.automation_executions USING btree (lease_expires_at_utc) WHERE ((status)::text = 'Running'::text)",
                "CREATE UNIQUE INDEX ux_automation_executions_occurrence ON public.automation_executions USING btree (user_id, automation_type, occurrence_key)"
            ],
            await Strings(database,
                """
                SELECT indexdef AS "Value" FROM pg_indexes
                WHERE tablename = 'automation_executions' AND indexname <> 'PK_automation_executions' ORDER BY indexname
                """));

        // FK with ON DELETE CASCADE (confdeltype 'c') and the check constraints.
        Assert.Equal(
            ["FK_automation_executions_users_user_id c", "ck_automation_executions_attempt_count", "ck_automation_executions_state", "ck_automation_executions_status", "ck_automation_executions_window"],
            (await Strings(database,
                """
                SELECT conname::text || CASE WHEN contype = 'f' THEN ' ' || confdeltype::text ELSE '' END AS "Value" FROM pg_constraint
                WHERE conrelid = 'automation_executions'::regclass AND contype IN ('c', 'f')
                """)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task InvalidStates_AreRejectedByTheDatabase()
    {
        var execution = await ClaimedAsync();

        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_automation_executions_state", () =>
            Execute($"UPDATE automation_executions SET status = 'Succeeded' WHERE id = '{execution.Id}'"));
        await PostgresAssert.ViolatesOneOfAsync(PostgresAssert.CheckViolation, ["ck_automation_executions_status", "ck_automation_executions_state"], () =>
            Execute($"UPDATE automation_executions SET status = 'Pending' WHERE id = '{execution.Id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_automation_executions_attempt_count", () =>
            Execute($"UPDATE automation_executions SET attempt_count = 0 WHERE id = '{execution.Id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_automation_executions_state", () =>
            Execute($"UPDATE automation_executions SET status = 'FailedRetryable', lease_expires_at_utc = NULL, next_attempt_at_utc = now() WHERE id = '{execution.Id}'"));
    }

    // ---- Claim (insert) ----

    [Fact]
    public async Task Claim_IsIdempotentPerOccurrence()
    {
        var user = await NewUserAsync();
        var other = await NewUserAsync();

        Assert.True(await TryClaimAsync(Execution(user.Id, "2026-10-04")));
        Assert.False(await TryClaimAsync(Execution(user.Id, "2026-10-04")));
        Assert.True(await TryClaimAsync(Execution(user.Id, "2026-10-11")));
        Assert.True(await TryClaimAsync(Execution(other.Id, "2026-10-04")));

        var row = Assert.Single(await RowsAsync(), row => row.UserId == user.Id && row.OccurrenceKey == "2026-10-04");
        Assert.Equal((AutomationExecutionStatus.Running, 1, Now + Lease, Now, Now), (row.Status, row.AttemptCount, row.LeaseExpiresAtUtc!.Value, row.StartedAtUtc, row.CreatedAtUtc));
    }

    [Fact]
    public async Task ConcurrentClaims_OfOneOccurrence_HaveExactlyOneWinner()
    {
        var user = await NewUserAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => TryClaimAsync(Execution(user.Id, "2026-10-04")))));

        Assert.Equal(1, results.Count(claimed => claimed));
        Assert.Single(await RowsAsync());
    }

    [Fact]
    public async Task TheUniqueIndex_RejectsASecondRowForAnOccurrence()
    {
        var user = await NewUserAsync();
        await PostgresAssert.InsertAsync(fixture, Execution(user.Id, "2026-10-04"));

        await PostgresAssert.InsertViolatesAsync(fixture, PostgresAssert.UniqueViolation, "ux_automation_executions_occurrence", Execution(user.Id, "2026-10-04"));
    }

    [Fact]
    public async Task Claim_ForAMissingUser_IsNotClaimed()
    {
        Assert.False(await TryClaimAsync(Execution(Guid.CreateVersion7(), "2026-10-04")));
        Assert.Empty(await RowsAsync());
    }

    [Fact]
    public async Task DeletingTheUser_DeletesItsExecutions()
    {
        var execution = await ClaimedAsync();

        await Execute($"DELETE FROM users WHERE id = '{execution.UserId}'");

        Assert.Empty(await RowsAsync());
    }

    // ---- Retry claim and lease takeover ----

    [Fact]
    public async Task RetryClaim_TakesOnlyEligibleRows_OldestFirst()
    {
        var retryLater = await FailedRetryableAsync(nextAttemptAtUtc: Now.AddMinutes(30));
        var retryFirst = await FailedRetryableAsync(nextAttemptAtUtc: Now.AddMinutes(6));
        var runningFresh = await ClaimedAsync(Now.AddMinutes(30));      // lease until +35 min
        var runningStale = await ClaimedAsync();                         // lease until +5 min
        await SetAttemptsAsync(await ClaimedAsync(), MaxAttempts);       // stale but out of attempts
        var expired = await FailedRetryableAsync(nextAttemptAtUtc: Now.AddMinutes(10), expiresAtUtc: Now.AddMinutes(20));
        var at = Now.AddMinutes(40);

        Assert.Null(await ClaimNextAsync(Now.AddMinutes(4)));

        var claimed = new List<AutomationOccurrence>();

        for (var index = 0; index < 4; index++)
        {
            claimed.Add((await ClaimNextAsync(at))!);
        }

        // Oldest first: runningStale's lease expired at +5 min, retryFirst was due at +6 min,
        // retryLater at +30 min, runningFresh's lease expired at +35 min.
        Assert.Equal([runningStale.Id, retryFirst.Id, retryLater.Id, runningFresh.Id], claimed.Select(occurrence => occurrence.ExecutionId));
        Assert.All(claimed, occurrence => Assert.Equal(2, occurrence.Attempt));
        Assert.Null(await ClaimNextAsync(at));
        Assert.Equal(AutomationExecutionStatus.FailedRetryable, (await RowAsync(expired.Id)).Status);

        var row = await RowAsync(runningStale.Id);
        Assert.Equal((AutomationExecutionStatus.Running, 2, at, at + Lease), (row.Status, row.AttemptCount, row.StartedAtUtc, row.LeaseExpiresAtUtc!.Value));
        Assert.Null(row.NextAttemptAtUtc);
        Assert.Equal((runningStale.UserId, "2026-10-04", Due), (claimed[0].UserId, claimed[0].OccurrenceKey, claimed[0].ScheduledForUtc));
    }

    [Fact]
    public async Task RetryClaim_IgnoresOtherAutomationTypes()
    {
        await FailedRetryableAsync(nextAttemptAtUtc: Now);

        await using var scope = fixture.CreateScope();
        var claimed = await Store(scope).ClaimNextRetryAsync([$"Other-{Guid.NewGuid():N}"], Now.AddHours(1), Lease, MaxAttempts, default);

        Assert.Null(claimed);
    }

    [Fact]
    public async Task ConcurrentRetryClaims_EachRowHasOneClaimer()
    {
        var rows = new List<AutomationExecution>();

        for (var index = 0; index < 5; index++)
        {
            rows.Add(await FailedRetryableAsync(nextAttemptAtUtc: Now));
        }

        var claims = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => ClaimNextAsync(Now.AddMinutes(1)))));
        var claimed = claims.OfType<AutomationOccurrence>().ToList();

        Assert.Equal(rows.Select(row => row.Id).Order(), claimed.Select(occurrence => occurrence.ExecutionId).Order());
        Assert.All(claimed, occurrence => Assert.Equal(2, occurrence.Attempt));
        Assert.All(await RowsAsync(), row => Assert.Equal((AutomationExecutionStatus.Running, 2), (row.Status, row.AttemptCount)));
    }

    // ---- Fenced completion ----

    [Fact]
    public async Task Completion_IsFencedByTheAttempt_AfterATakeover()
    {
        var execution = await ClaimedAsync();
        var takeover = await ClaimNextAsync(Now + Lease);
        Assert.Equal(2, takeover!.Attempt);

        await using (var scope = fixture.CreateScope())
        {
            var store = Store(scope);

            Assert.False(await store.CompleteSucceededAsync(execution.Id, 1, Guid.CreateVersion7(), Now.AddMinutes(6), default));
            Assert.False(await store.CompleteRetryableAsync(execution.Id, 1, "Late", Now.AddMinutes(16), default));
            Assert.False(await store.CompleteFinalAsync(execution.Id, 1, "Late", Now.AddMinutes(6), default));
            Assert.Equal((AutomationExecutionStatus.Running, 2), ((await RowAsync(execution.Id)).Status, (await RowAsync(execution.Id)).AttemptCount));

            var resultId = Guid.CreateVersion7();
            Assert.True(await store.CompleteSucceededAsync(execution.Id, 2, resultId, Now.AddMinutes(7), default));
            Assert.False(await store.CompleteSucceededAsync(execution.Id, 2, resultId, Now.AddMinutes(8), default));

            var row = await RowAsync(execution.Id);
            Assert.Equal((AutomationExecutionStatus.Succeeded, resultId, Now.AddMinutes(7)), (row.Status, row.ResultId, row.CompletedAtUtc!.Value));
            Assert.Null(row.LeaseExpiresAtUtc);
            Assert.Null(row.NextAttemptAtUtc);
            Assert.Null(row.LastFailureCode);
        }
    }

    [Fact]
    public async Task ConcurrentCompletions_OfOneAttempt_HaveExactlyOneWinner()
    {
        var execution = await ClaimedAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(index => Task.Run(async () =>
        {
            await using var scope = fixture.CreateScope();
            var store = Store(scope);

            return index % 2 == 0
                ? await store.CompleteSucceededAsync(execution.Id, 1, null, Now.AddMinutes(1), default)
                : await store.CompleteFinalAsync(execution.Id, 1, "Permanent:Test", Now.AddMinutes(1), default);
        })));

        Assert.Equal(1, results.Count(won => won));
    }

    [Fact]
    public async Task RetryableAndFinalCompletion_RecordTheirStates()
    {
        var retryable = await ClaimedAsync();
        var final = await ClaimedAsync();

        await using (var scope = fixture.CreateScope())
        {
            Assert.True(await Store(scope).CompleteRetryableAsync(retryable.Id, 1, "Transient", Now.AddMinutes(10), default));
            Assert.True(await Store(scope).CompleteFinalAsync(final.Id, 1, "Permanent:ModuleDisabled", Now.AddMinutes(1), default));
            await Assert.ThrowsAsync<ArgumentException>(() => Store(scope).CompleteFinalAsync(final.Id, 1, "free text", Now, default));
        }

        var retryableRow = await RowAsync(retryable.Id);
        Assert.Equal((AutomationExecutionStatus.FailedRetryable, 1, "Transient", Now.AddMinutes(10)), (retryableRow.Status, retryableRow.AttemptCount, retryableRow.LastFailureCode, retryableRow.NextAttemptAtUtc!.Value));
        Assert.Null(retryableRow.LeaseExpiresAtUtc);
        Assert.Null(retryableRow.CompletedAtUtc);

        var finalRow = await RowAsync(final.Id);
        Assert.Equal((AutomationExecutionStatus.FailedFinal, "Permanent:ModuleDisabled", Now.AddMinutes(1)), (finalRow.Status, finalRow.LastFailureCode, finalRow.CompletedAtUtc!.Value));
        Assert.Null(finalRow.LeaseExpiresAtUtc);
        Assert.Null(finalRow.NextAttemptAtUtc);

        // Only the retryable row is claimed again; terminal rows never are.
        var retried = await ClaimNextAsync(Now.AddHours(2));
        Assert.Equal((retryable.Id, 2), (retried!.ExecutionId, retried.Attempt));
        Assert.Null(await ClaimNextAsync(Now.AddHours(2)));
    }

    // ---- Abandoned rows become terminal ----

    [Fact]
    public async Task FinalizeAbandoned_ExpiresAndExhaustsOnlyAbandonedRows()
    {
        var retryPastExpiry = await FailedRetryableAsync(nextAttemptAtUtc: Now.AddMinutes(30), expiresAtUtc: Now.AddMinutes(20));
        var staleLastAttempt = await ClaimedAsync();
        await SetAttemptsAsync(staleLastAttempt, MaxAttempts);
        var staleWithAttemptsLeft = await ClaimedAsync();
        var staleAndExpired = await ClaimedAsync(expiresAtUtc: Now.AddMinutes(10));
        var retryDue = await FailedRetryableAsync(nextAttemptAtUtc: Now.AddMinutes(10));
        var at = Now.AddMinutes(20);

        await using (var scope = fixture.CreateScope())
        {
            Assert.True(await Store(scope).FinalizeAbandonedAsync(at, MaxAttempts, 1000, default) >= 3);
        }

        Assert.Equal((AutomationExecutionStatus.FailedFinal, "Expired", at), Final(await RowAsync(retryPastExpiry.Id)));
        Assert.Equal((AutomationExecutionStatus.FailedFinal, "MaxAttemptsReached", at), Final(await RowAsync(staleLastAttempt.Id)));
        Assert.Equal((AutomationExecutionStatus.FailedFinal, "Expired", at), Final(await RowAsync(staleAndExpired.Id)));
        Assert.Equal(AutomationExecutionStatus.Running, (await RowAsync(staleWithAttemptsLeft.Id)).Status);
        Assert.Equal(AutomationExecutionStatus.FailedRetryable, (await RowAsync(retryDue.Id)).Status);

        static (AutomationExecutionStatus, string?, DateTimeOffset?) Final(AutomationExecution row) =>
            (row.Status, row.LastFailureCode, row.CompletedAtUtc);
    }

    // ---- The tick engine on PostgreSQL ----

    [Fact]
    public async Task ConcurrentTicksOnSeveralInstances_ExecuteEachOccurrenceOnce()
    {
        var handler = new TestAutomationHandler(_type);

        for (var index = 0; index < 8; index++)
        {
            handler.AddDue((await NewUserAsync()).Id, "2026-10-04", Due);
        }

        // Separate guards = separate API instances: only PostgreSQL keeps them apart.
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await using var scope = fixture.CreateScope();
            return await Tick(scope, handler, new FixedTimeProvider(Now)).RunAsync();
        })));

        Assert.Equal(8, results.Sum(result => result.Executions));
        Assert.Equal(8, handler.Executed.Count);
        Assert.Equal(8, handler.Executed.Select(occurrence => occurrence.ExecutionId).Distinct().Count());
        Assert.All(await RowsAsync(), row => Assert.Equal((AutomationExecutionStatus.Succeeded, 1), (row.Status, row.AttemptCount)));
    }

    [Fact]
    public async Task Tick_RetriesThenFinalizes_OnPostgreSql()
    {
        var handler = new TestAutomationHandler(_type);
        handler.AddDue((await NewUserAsync()).Id, "2026-10-04", Due);
        handler.Execute = _ => Task.FromResult(AutomationResult.RetryableFailure("Transient"));
        var clock = new ManualTimeProvider(Now);

        foreach (var advance in new[] { TimeSpan.Zero, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1) })
        {
            clock.Advance(advance);
            await using var scope = fixture.CreateScope();
            await Tick(scope, handler, clock).RunAsync();
        }

        var row = Assert.Single(await RowsAsync());
        Assert.Equal((AutomationExecutionStatus.FailedFinal, 3, "MaxAttemptsReached"), (row.Status, row.AttemptCount, row.LastFailureCode));
        Assert.Equal([1, 2, 3], handler.Executed.Select(occurrence => occurrence.Attempt));
    }

    // ---- Helpers ----

    private AutomationExecution Execution(Guid userId, string key, DateTimeOffset? claimedAtUtc = null, DateTimeOffset? expiresAtUtc = null) =>
        AutomationExecution.Claim(userId, _type, key, "Europe/Rome", Due, expiresAtUtc ?? Due.AddHours(24), claimedAtUtc ?? Now, Lease);

    private async Task<AutomationExecution> ClaimedAsync(DateTimeOffset? claimedAtUtc = null, DateTimeOffset? expiresAtUtc = null)
    {
        var execution = Execution((await NewUserAsync()).Id, "2026-10-04", claimedAtUtc, expiresAtUtc);
        Assert.True(await TryClaimAsync(execution));

        return execution;
    }

    private async Task<AutomationExecution> FailedRetryableAsync(DateTimeOffset nextAttemptAtUtc, DateTimeOffset? expiresAtUtc = null)
    {
        var execution = await ClaimedAsync(expiresAtUtc: expiresAtUtc);

        await using var scope = fixture.CreateScope();
        Assert.True(await Store(scope).CompleteRetryableAsync(execution.Id, 1, "Transient", nextAttemptAtUtc, default));

        return execution;
    }

    private Task SetAttemptsAsync(AutomationExecution execution, int attempts) =>
        Execute($"UPDATE automation_executions SET attempt_count = {attempts} WHERE id = '{execution.Id}'");

    private async Task<bool> TryClaimAsync(AutomationExecution execution)
    {
        await using var scope = fixture.CreateScope();
        return await Store(scope).TryClaimAsync(execution, default);
    }

    private async Task<AutomationOccurrence?> ClaimNextAsync(DateTimeOffset nowUtc)
    {
        await using var scope = fixture.CreateScope();
        return await Store(scope).ClaimNextRetryAsync([_type], nowUtc, Lease, MaxAttempts, default);
    }

    private async Task<List<AutomationExecution>> RowsAsync()
    {
        await using var scope = fixture.CreateScope();
        return await Db(scope).AutomationExecutions.AsNoTracking().Where(row => row.AutomationType == _type).ToListAsync();
    }

    private async Task<AutomationExecution> RowAsync(Guid id)
    {
        await using var scope = fixture.CreateScope();
        return await Db(scope).AutomationExecutions.AsNoTracking().SingleAsync(row => row.Id == id);
    }

    private async Task Execute(string sql)
    {
        await using var scope = fixture.CreateScope();
        await Database(scope).ExecuteSqlRawAsync(sql);
    }

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now);
        await PostgresAssert.InsertAsync(fixture, user);

        return user;
    }

    // The real scoped stores; no push sender, so Phase A is disabled as in production (WP3A).
    private static RunAutomationTick Tick(AsyncServiceScope scope, IAutomationHandler handler, TimeProvider time)
    {
        var services = scope.ServiceProvider;
        var deliveries = services.GetRequiredService<LifeOS.Application.Notifications.INotificationDeliveryStore>();
        var unitOfWork = services.GetRequiredService<LifeOS.Application.Persistence.IUnitOfWork>();
        var dispatcher = new LifeOS.Application.Notifications.NotificationDispatcher(
            deliveries, services.GetRequiredService<LifeOS.Application.Notifications.IDeviceRegistrationRepository>(),
            services.GetRequiredService<LifeOS.Application.Notifications.INotificationPreferencesRepository>(), unitOfWork, time);

        return new RunAutomationTick([handler], Store(scope), dispatcher, deliveries, unitOfWork, new AutomationTickGuard(), time);
    }

    private static IAutomationExecutionStore Store(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IAutomationExecutionStore>();

    private static LifeOSDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();

    private static Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade Database(AsyncServiceScope scope) => Db(scope).Database;

    private static Task<List<string>> Strings(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database, string sql) =>
        database.SqlQueryRaw<string>(sql).ToListAsync();
}
