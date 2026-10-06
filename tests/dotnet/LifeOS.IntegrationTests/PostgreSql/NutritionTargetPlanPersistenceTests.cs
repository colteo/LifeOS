using LifeOS.Application.Nutrition;
using LifeOS.Domain.Nutrition;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LifeOS.IntegrationTests.PostgreSql;

// NUT-003 against real PostgreSQL: the target-plan schema from its migration, the database backstops
// (no overlapping plans per user under concurrency, rule/override checks, cascades), round trips,
// lookups, override upserts and user isolation. Final state is always read from a fresh scope.
[Collection(PostgreSqlCollection.Name)]
public class NutritionTargetPlanPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 10, 0, 0, TimeSpan.Zero);
    private const string NoOverlap = "ex_nutrition_target_plans_no_overlap";

    private static DateOnly D(int month, int day) => new(month == 1 ? 2027 : 2026, month, day);

    // ---- Schema ----

    [Fact]
    public async Task Migration_CreatesTheThreeTargetTables_AfterTheNut002Migration()
    {
        await using var scope = fixture.CreateScope();
        var database = Database(scope);
        var applied = (await database.GetAppliedMigrationsAsync()).ToList();

        // Later migrations (AUTO-001) may follow; NUT-003's must directly follow NUT-002's.
        var targetPlans = applied.FindIndex(id => id.EndsWith("_AddNutritionTargetPlans", StringComparison.Ordinal));
        Assert.True(targetPlans > 0);
        Assert.EndsWith("_AddMealNutritionSnapshots", applied[targetPlans - 1]);
        Assert.DoesNotContain(applied, id => id.EndsWith("_AddNutritionTargets", StringComparison.Ordinal));
        Assert.False(database.HasPendingModelChanges());

        Assert.Equal(
            [
                "id uuid NOT NULL", "user_id uuid NOT NULL", "starts_on date NOT NULL", "ends_on date NOT NULL",
                "default_calories_kcal numeric(6,1) NULL", "default_protein_grams numeric(5,1) NULL",
                "default_carbs_grams numeric(5,1) NULL", "default_fat_grams numeric(5,1) NULL",
                "created_at_utc timestamp with time zone NOT NULL", "updated_at_utc timestamp with time zone NOT NULL"
            ],
            await Columns(database, "nutrition_target_plans"));
        Assert.Equal(
            [
                "weekday smallint NOT NULL", "plan_id uuid NOT NULL", "mode character varying(16) NOT NULL",
                "calories_kcal numeric(6,1) NULL", "protein_grams numeric(5,1) NULL", "carbs_grams numeric(5,1) NULL", "fat_grams numeric(5,1) NULL"
            ],
            await Columns(database, "nutrition_target_plan_day_rules"));
        Assert.Equal(
            [
                "id uuid NOT NULL", "user_id uuid NOT NULL", "plan_id uuid NOT NULL", "date date NOT NULL", "mode character varying(16) NOT NULL",
                "calories_kcal numeric(6,1) NULL", "protein_grams numeric(5,1) NULL", "carbs_grams numeric(5,1) NULL", "fat_grams numeric(5,1) NULL",
                "created_at_utc timestamp with time zone NOT NULL", "updated_at_utc timestamp with time zone NOT NULL"
            ],
            await Columns(database, "nutrition_target_overrides"));

        Assert.Equal(
            [
                "FK_nutrition_target_overrides_nutrition_target_plans_plan_id nutrition_target_plans c",
                "FK_nutrition_target_overrides_users_user_id users r",
                "FK_nutrition_target_plan_day_rules_plan_id nutrition_target_plans c",
                "FK_nutrition_target_plans_users_user_id users r"
            ],
            await Strings(database,
                """
                SELECT conname || ' ' || confrelid::regclass::text || ' ' || confdeltype::text AS "Value"
                FROM pg_constraint WHERE contype = 'f' AND conrelid::regclass::text LIKE 'nutrition_target%' ORDER BY 1
                """));

        Assert.Equal(
            [
                "ck_nutrition_target_overrides_mode", "ck_nutrition_target_overrides_values",
                "ck_nutrition_target_plan_day_rules_mode", "ck_nutrition_target_plan_day_rules_values", "ck_nutrition_target_plan_day_rules_weekday",
                "ck_nutrition_target_plans_default", "ck_nutrition_target_plans_period"
            ],
            await Strings(database,
                "SELECT conname AS \"Value\" FROM pg_constraint WHERE contype = 'c' AND conrelid::regclass::text LIKE 'nutrition_target%' ORDER BY 1"));

        Assert.Contains("PRIMARY KEY (plan_id, weekday)", await Scalar<string>(database,
            "SELECT pg_get_constraintdef(oid) AS \"Value\" FROM pg_constraint WHERE conname = 'PK_nutrition_target_plan_day_rules'"));
        Assert.Contains("UNIQUE INDEX ux_nutrition_target_overrides_user_date ON public.nutrition_target_overrides USING btree (user_id, date)",
            await Scalar<string>(database, "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'ux_nutrition_target_overrides_user_date'"));

        // The overlap trigger, and no extension of any kind.
        Assert.Equal("trg_nutrition_target_plans_no_overlap", await Scalar<string>(database,
            "SELECT tgname AS \"Value\" FROM pg_trigger WHERE tgrelid = 'nutrition_target_plans'::regclass AND NOT tgisinternal"));
        Assert.Equal(["plpgsql"], await Strings(database, "SELECT extname AS \"Value\" FROM pg_extension ORDER BY 1"));

        // NUT-001/002 tables unchanged; the rejected NUT-003 table does not exist.
        Assert.Equal(9, await Scalar<int>(database,
            "SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'meal_entries'"));
        Assert.Equal(9, await Scalar<int>(database,
            "SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'meal_nutrition_snapshots'"));
        Assert.Equal(0, await Scalar<int>(database,
            "SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_name = 'nutrition_targets'"));
    }

    [Fact]
    public async Task Migration_DownAndUp_DropsAndRecreatesOnlyTheTargetTablesAndTrigger()
    {
        await using var scope = fixture.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();
        var migrator = dbContext.GetService<IMigrator>();
        var applied = (await dbContext.Database.GetAppliedMigrationsAsync()).ToList();
        var tablesBefore = await TableCountAsync(dbContext.Database);

        // The three target tables, plus AUTO-001's automation_executions, device_registrations and
        // notification_deliveries, AUTO-002's weekly_reviews and weekly_review_settings, and AUTO-003A's
        // notification_preferences (later migrations, whose Down drops them).
        await migrator.MigrateAsync(applied[applied.FindIndex(id => id.EndsWith("_AddNutritionTargetPlans", StringComparison.Ordinal)) - 1]);

        Assert.Equal(tablesBefore - 9, await TableCountAsync(dbContext.Database));
        Assert.Equal(0, await Scalar<int>(dbContext.Database,
            "SELECT count(*)::int AS \"Value\" FROM pg_proc WHERE proname = 'nutrition_target_plans_prevent_overlap'"));
        Assert.Equal(1, await Scalar<int>(dbContext.Database,
            "SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_name = 'meal_nutrition_snapshots'"));

        await migrator.MigrateAsync();

        Assert.Equal(applied, await dbContext.Database.GetAppliedMigrationsAsync());
        Assert.Equal(tablesBefore, await TableCountAsync(dbContext.Database));
        Assert.Equal(1, await Scalar<int>(dbContext.Database,
            "SELECT count(*)::int AS \"Value\" FROM pg_trigger WHERE tgname = 'trg_nutrition_target_plans_no_overlap'"));
    }

    // ---- Round trip and lookups ----

    [Fact]
    public async Task RoundTrip_KeepsThePeriodDefaultAndAllSevenRules()
    {
        var user = await NewUserAsync();
        var plan = Plan(user.Id, D(10, 7), D(11, 3), TrainingWeek());

        Assert.Equal(NutritionTargetPlanSaveStatus.Saved, (await AddAsync(plan)).Status);

        var stored = (await GetAsync(user.Id, plan.Id))!;
        Assert.Equal((D(10, 7), D(11, 3)), (stored.StartsOn, stored.EndsOn));
        Assert.Equal(NutritionTargetValues.Create(2200, 160, 240, 70), stored.DefaultTarget);
        Assert.Equal(NutritionTargetPlan.Week, stored.Rules.Select(rule => rule.Weekday));
        Assert.Equal(2500m, stored.Rules.Single(rule => rule.Weekday == DayOfWeek.Monday).Target!.CaloriesKcal);
        Assert.Equal(NutritionTargetDayMode.NoTarget, stored.Rules.Single(rule => rule.Weekday == DayOfWeek.Sunday).Mode);
        Assert.Equal((Now, Now), (stored.CreatedAtUtc, stored.UpdatedAtUtc));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], await Ints($"SELECT weekday::int AS \"Value\" FROM nutrition_target_plan_day_rules WHERE plan_id = '{plan.Id}' ORDER BY 1"));
        Assert.Equal("Custom", await ScalarFreshAsync<string>(
            $"SELECT mode AS \"Value\" FROM nutrition_target_plan_day_rules WHERE plan_id = '{plan.Id}' AND weekday = 1"));
    }

    [Fact]
    public async Task Lookup_FindsTheCoveringPlan_AtBothBoundaries_AndNothingInGaps()
    {
        var user = await NewUserAsync();
        var first = Plan(user.Id, D(10, 7), D(11, 3));
        var second = Plan(user.Id, D(11, 10), D(12, 1));
        await AddAsync(first);
        await AddAsync(second);

        Assert.Equal(first.Id, (await DayAsync(user.Id, D(10, 7))).Plan!.Id);
        Assert.Equal(first.Id, (await DayAsync(user.Id, D(11, 3))).Plan!.Id);
        Assert.Null((await DayAsync(user.Id, D(11, 5))).Plan);
        Assert.Equal(second.Id, (await DayAsync(user.Id, D(11, 10))).Plan!.Id);
        Assert.Null((await DayAsync(user.Id, D(10, 6))).Plan);
    }

    [Fact]
    public async Task Update_PersistsTheNewPatternInPlace()
    {
        var user = await NewUserAsync();
        var plan = Plan(user.Id, D(10, 7), D(11, 3));
        await AddAsync(plan);

        await using (var scope = fixture.CreateScope())
        {
            var tracked = (await Plans(scope).GetAsync(user.Id, plan.Id, CancellationToken.None))!;
            tracked.Update(D(10, 7), D(11, 10), null, TrainingWeek(withoutDefault: true), Now.AddHours(1));
            Assert.Equal(NutritionTargetPlanSaveStatus.Saved, (await Plans(scope).UpdateAsync(tracked, CancellationToken.None)).Status);
        }

        var stored = (await GetAsync(user.Id, plan.Id))!;
        Assert.Equal(D(11, 10), stored.EndsOn);
        Assert.Null(stored.DefaultTarget);
        Assert.Equal(NutritionTargetDayMode.NoTarget, stored.Rules.Single(rule => rule.Weekday == DayOfWeek.Tuesday).Mode);
        Assert.Equal(Now.AddHours(1), stored.UpdatedAtUtc);
        Assert.Equal(7, await ScalarFreshAsync<int>(
            $"SELECT count(*)::int AS \"Value\" FROM nutrition_target_plan_day_rules WHERE plan_id = '{plan.Id}'"));
    }

    // ---- Overlap: application path and database ----

    [Fact]
    public async Task AdjacentPlans_AreAccepted_AndOverlapsAreRejected()
    {
        var user = await NewUserAsync();
        var first = Plan(user.Id, D(10, 7), D(11, 3));

        Assert.Equal(NutritionTargetPlanSaveStatus.Saved, (await AddAsync(first)).Status);
        Assert.Equal(NutritionTargetPlanSaveStatus.Saved, (await AddAsync(Plan(user.Id, D(11, 4), D(12, 1)))).Status);
        Assert.Equal(NutritionTargetPlanSaveStatus.Saved, (await AddAsync(Plan(user.Id, D(12, 2), D(1, 5)))).Status);

        var overlap = await AddAsync(Plan(user.Id, D(10, 28), D(11, 30)));

        Assert.Equal(NutritionTargetPlanSaveStatus.Overlap, overlap.Status);
        Assert.Equal(first.Id, overlap.Conflict!.Id);
        Assert.Equal(3, await PlanCountAsync(user.Id));
    }

    [Fact]
    public async Task EditingIntoAnotherPlan_IsRejected_AndNothingChanges()
    {
        var user = await NewUserAsync();
        var a = Plan(user.Id, D(10, 1), D(10, 31));
        var b = Plan(user.Id, D(11, 1), D(11, 30));
        await AddAsync(a);
        await AddAsync(b);

        await using (var scope = fixture.CreateScope())
        {
            var tracked = (await Plans(scope).GetAsync(user.Id, a.Id, CancellationToken.None))!;
            tracked.Update(D(10, 1), D(11, 10), NutritionTargetValues.Create(2200, null, null, null), AllDefault(), Now);
            var result = await Plans(scope).UpdateAsync(tracked, CancellationToken.None);

            Assert.Equal(NutritionTargetPlanSaveStatus.Overlap, result.Status);
            Assert.Equal(b.Id, result.Conflict!.Id);
        }

        Assert.Equal(D(10, 31), (await GetAsync(user.Id, a.Id))!.EndsOn);
    }

    [Fact]
    public async Task TheDatabaseRejectsOverlapsWrittenOutsideTheApplication()
    {
        var user = await NewUserAsync();
        await InsertRawPlanAsync(user.Id, D(10, 7), D(11, 3));

        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.ExclusionViolation, NoOverlap,
            () => InsertRawPlanAsync(user.Id, D(11, 3), D(11, 30)));

        var later = await InsertRawPlanAsync(user.Id, D(12, 1), D(12, 31));
        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.ExclusionViolation, NoOverlap, async () =>
        {
            await using var scope = fixture.CreateScope();
            await Database(scope).ExecuteSqlInterpolatedAsync($"UPDATE nutrition_target_plans SET starts_on = {D(10, 20)} WHERE id = {later}");
        });

        // Another user's identical period is fine.
        await InsertRawPlanAsync((await NewUserAsync()).Id, D(10, 7), D(11, 3));
    }

    [Fact]
    public async Task WritesUnderAnotherIsolationLevel_AreRefused()
    {
        var user = await NewUserAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var scope = fixture.CreateScope();
            var database = Database(scope);
            await using var transaction = await database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
            await database.ExecuteSqlRawAsync(RawPlanSql(Guid.CreateVersion7(), user.Id, D(10, 7), D(11, 3)));
        });

        Assert.Equal(PostgresErrorCodes.InvalidTransactionState, exception.SqlState);
    }

    // ---- Concurrency ----

    [Fact]
    public async Task ConcurrentOverlappingPlans_ExactlyOneIsSaved()
    {
        for (var round = 0; round < 5; round++)
        {
            var user = await NewUserAsync();

            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(attempt =>
                AddAsync(Plan(user.Id, D(10, 1).AddDays(attempt), D(10, 20).AddDays(attempt)))));

            Assert.Equal(1, results.Count(result => result.Status == NutritionTargetPlanSaveStatus.Saved));
            Assert.Equal(7, results.Count(result => result.Status == NutritionTargetPlanSaveStatus.Overlap));
            Assert.Equal(1, await PlanCountAsync(user.Id));
        }
    }

    [Fact]
    public async Task ConcurrentNonOverlappingPlans_AreAllSaved()
    {
        var user = await NewUserAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(week =>
            AddAsync(Plan(user.Id, D(10, 1).AddDays(7 * week), D(10, 7).AddDays(7 * week)))));

        Assert.All(results, result => Assert.Equal(NutritionTargetPlanSaveStatus.Saved, result.Status));
        Assert.Equal(6, await PlanCountAsync(user.Id));
    }

    [Fact]
    public async Task ConcurrentIdenticalPeriodsOfDifferentUsers_AreAllSaved()
    {
        var users = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => NewUserAsync()));

        var results = await Task.WhenAll(users.Select(user => AddAsync(Plan(user.Id, D(10, 7), D(11, 3)))));

        Assert.All(results, result => Assert.Equal(NutritionTargetPlanSaveStatus.Saved, result.Status));
    }

    [Fact]
    public async Task ConcurrentEditsThatOverlapEachOther_AtMostOneIsSaved()
    {
        for (var round = 0; round < 5; round++)
        {
            var user = await NewUserAsync();
            var a = Plan(user.Id, D(10, 1), D(10, 10));
            var b = Plan(user.Id, D(10, 21), D(10, 30));
            await AddAsync(a);
            await AddAsync(b);

            // Each edit is fine against the other's current period, but not against its edited one.
            var results = await Task.WhenAll(
                EditAsync(user.Id, a.Id, D(10, 1), D(10, 15)),
                EditAsync(user.Id, b.Id, D(10, 12), D(10, 30)));

            Assert.Equal(1, results.Count(status => status == NutritionTargetPlanSaveStatus.Saved));
            var stored = await ListAsync(user.Id);
            Assert.False(stored[0].Overlaps(stored[1].StartsOn, stored[1].EndsOn));
        }
    }

    [Fact]
    public async Task ConcurrentOverrideSaves_LeaveOneRowPerDate()
    {
        var user = await NewUserAsync();
        var plan = Plan(user.Id, D(10, 7), D(11, 3));
        await AddAsync(plan);

        var saved = await Task.WhenAll(Enumerable.Range(0, 8).Select(attempt => SetOverrideAsync(attempt % 2 == 0
            ? NutritionTargetOverride.Create(plan, D(10, 23), NutritionTargetOverrideMode.Custom,
                NutritionTargetValues.Create(2000 + attempt, null, null, null), Now)
            : NutritionTargetOverride.Create(plan, D(10, 23), NutritionTargetOverrideMode.NoTarget, null, Now))));

        Assert.All(saved, Assert.True);
        Assert.Equal(1, await OverrideCountAsync(user.Id));
    }

    // ---- Overrides ----

    [Fact]
    public async Task AnOverride_IsStoredOnlyInsideItsPlan_AndReplacedInPlace()
    {
        var user = await NewUserAsync();
        var plan = Plan(user.Id, D(10, 7), D(11, 3));
        await AddAsync(plan);

        Assert.True(await SetOverrideAsync(NutritionTargetOverride.Create(plan, D(10, 23), NutritionTargetOverrideMode.NoTarget, null, Now)));
        Assert.True(await SetOverrideAsync(NutritionTargetOverride.Create(plan, D(10, 23), NutritionTargetOverrideMode.Custom,
            NutritionTargetValues.Create(3000, null, null, null), Now.AddHours(1))));

        var day = await DayAsync(user.Id, D(10, 23));
        Assert.Equal(NutritionTargetOverrideMode.Custom, day.Override!.Mode);
        Assert.Equal(3000m, day.Override.Target!.CaloriesKcal);
        Assert.Equal(1, await OverrideCountAsync(user.Id));

        // The plan moved away meanwhile: the insert finds no covering plan and writes nothing.
        var stale = NutritionTargetOverride.Create(plan, D(11, 1), NutritionTargetOverrideMode.NoTarget, null, Now);
        await EditAsync(user.Id, plan.Id, D(10, 7), D(10, 25));
        Assert.False(await SetOverrideAsync(stale));
    }

    [Fact]
    public async Task ShorteningAPlan_DeletesItsOverridesOutsideTheNewPeriod()
    {
        var user = await NewUserAsync();
        var plan = Plan(user.Id, D(10, 7), D(11, 3));
        await AddAsync(plan);
        await SetOverrideAsync(NutritionTargetOverride.Create(plan, D(10, 10), NutritionTargetOverrideMode.NoTarget, null, Now));
        await SetOverrideAsync(NutritionTargetOverride.Create(plan, D(11, 1), NutritionTargetOverrideMode.NoTarget, null, Now));

        Assert.Equal(NutritionTargetPlanSaveStatus.Saved, await EditAsync(user.Id, plan.Id, D(10, 7), D(10, 20)));

        Assert.Equal([D(10, 10)], (await OverrideDatesAsync(user.Id)));
    }

    [Fact]
    public async Task DeletingAPlan_CascadesToItsRulesAndOverrides_Only()
    {
        var user = await NewUserAsync();
        var first = Plan(user.Id, D(10, 7), D(11, 3));
        var second = Plan(user.Id, D(11, 4), D(12, 1));
        await AddAsync(first);
        await AddAsync(second);
        await SetOverrideAsync(NutritionTargetOverride.Create(first, D(10, 23), NutritionTargetOverrideMode.NoTarget, null, Now));
        await SetOverrideAsync(NutritionTargetOverride.Create(second, D(11, 10), NutritionTargetOverrideMode.NoTarget, null, Now));

        await using (var scope = fixture.CreateScope())
        {
            Assert.True(await Plans(scope).DeleteAsync(user.Id, first.Id, CancellationToken.None));
            Assert.False(await Plans(scope).DeleteAsync(user.Id, first.Id, CancellationToken.None));
        }

        Assert.Equal([second.Id], (await ListAsync(user.Id)).Select(plan => plan.Id));
        Assert.Equal([D(11, 10)], await OverrideDatesAsync(user.Id));
        Assert.Equal(0, await ScalarFreshAsync<int>(
            $"SELECT count(*)::int AS \"Value\" FROM nutrition_target_plan_day_rules WHERE plan_id = '{first.Id}'"));
    }

    [Fact]
    public async Task EverythingIsUserScoped()
    {
        var owner = await NewUserAsync();
        var other = await NewUserAsync();
        var plan = Plan(owner.Id, D(10, 7), D(11, 3));
        await AddAsync(plan);

        Assert.Null(await GetAsync(other.Id, plan.Id));
        Assert.Empty(await ListAsync(other.Id));
        Assert.Null((await DayAsync(other.Id, D(10, 10))).Plan);

        await using (var scope = fixture.CreateScope())
        {
            Assert.False(await Plans(scope).DeleteAsync(other.Id, plan.Id, CancellationToken.None));
            await Plans(scope).RemoveOverrideAsync(other.Id, D(10, 10), CancellationToken.None);
        }

        Assert.NotNull(await GetAsync(owner.Id, plan.Id));
    }

    // ---- Check constraints ----

    [Theory]
    [InlineData("ck_nutrition_target_plan_day_rules_values", "Custom", "NULL")]
    [InlineData("ck_nutrition_target_plan_day_rules_values", "Default", "2500")]
    [InlineData("ck_nutrition_target_plan_day_rules_values", "NoTarget", "2500")]
    [InlineData("ck_nutrition_target_plan_day_rules_values", "Custom", "0")]
    [InlineData("ck_nutrition_target_plan_day_rules_mode", "Rest", "NULL")]
    public async Task RuleChecks_RejectInconsistentRows(string constraint, string mode, string calories)
    {
        var user = await NewUserAsync();
        var plan = Plan(user.Id, D(10, 7), D(11, 3));
        await AddAsync(plan);

        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.CheckViolation, constraint, async () =>
        {
            await using var scope = fixture.CreateScope();
            await Database(scope).ExecuteSqlRawAsync(
                $"UPDATE nutrition_target_plan_day_rules SET mode = '{mode}', calories_kcal = {calories} WHERE plan_id = '{plan.Id}' AND weekday = 1");
        });
    }

    [Fact]
    public async Task PeriodAndOverrideChecks_RejectInvalidRows()
    {
        var user = await NewUserAsync();
        var plan = Plan(user.Id, D(10, 7), D(11, 3));
        await AddAsync(plan);

        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.CheckViolation, "ck_nutrition_target_plans_period",
            () => InsertRawPlanAsync(user.Id, D(12, 10), D(12, 1)));
        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.CheckViolation, "ck_nutrition_target_overrides_values",
            () => InsertRawOverrideAsync(user.Id, plan.Id, D(10, 23), "NoTarget", "2000"));
        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.CheckViolation, "ck_nutrition_target_overrides_mode",
            () => InsertRawOverrideAsync(user.Id, plan.Id, D(10, 23), "Default", "NULL"));
        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.UniqueViolation, "PK_nutrition_target_plan_day_rules", async () =>
        {
            await using var scope = fixture.CreateScope();
            await Database(scope).ExecuteSqlRawAsync(
                $"INSERT INTO nutrition_target_plan_day_rules (plan_id, weekday, mode) VALUES ('{plan.Id}', 1, 'Default')");
        });
    }

    // ---- Helpers ----

    private static NutritionTargetPlan Plan(Guid userId, DateOnly startsOn, DateOnly endsOn, List<NutritionTargetDayRuleSpec>? rules = null) =>
        NutritionTargetPlan.Create(userId, startsOn, endsOn, NutritionTargetValues.Create(2200, 160, 240, 70), rules ?? AllDefault(), Now);

    private static List<NutritionTargetDayRuleSpec> AllDefault() =>
        NutritionTargetPlan.Week.Select(day => new NutritionTargetDayRuleSpec(day, NutritionTargetDayMode.Default)).ToList();

    private static List<NutritionTargetDayRuleSpec> TrainingWeek(bool withoutDefault = false) =>
        NutritionTargetPlan.Week.Select(day => day switch
        {
            DayOfWeek.Monday or DayOfWeek.Wednesday or DayOfWeek.Friday =>
                new NutritionTargetDayRuleSpec(day, NutritionTargetDayMode.Custom, NutritionTargetValues.Create(2500, null, null, null)),
            DayOfWeek.Sunday => new NutritionTargetDayRuleSpec(day, NutritionTargetDayMode.NoTarget),
            _ => new NutritionTargetDayRuleSpec(day, withoutDefault ? NutritionTargetDayMode.NoTarget : NutritionTargetDayMode.Default)
        }).ToList();

    private async Task<NutritionTargetPlanSave> AddAsync(NutritionTargetPlan plan)
    {
        await using var scope = fixture.CreateScope();
        return await Plans(scope).AddAsync(plan, CancellationToken.None);
    }

    private async Task<NutritionTargetPlanSaveStatus> EditAsync(Guid userId, Guid planId, DateOnly startsOn, DateOnly endsOn)
    {
        await using var scope = fixture.CreateScope();
        var tracked = (await Plans(scope).GetAsync(userId, planId, CancellationToken.None))!;
        tracked.Update(startsOn, endsOn, tracked.DefaultTarget, AllDefault(), Now);
        return (await Plans(scope).UpdateAsync(tracked, CancellationToken.None)).Status;
    }

    private async Task<NutritionTargetPlan?> GetAsync(Guid userId, Guid planId)
    {
        await using var scope = fixture.CreateScope();
        return await Plans(scope).GetAsync(userId, planId, CancellationToken.None);
    }

    private async Task<IReadOnlyList<NutritionTargetPlan>> ListAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();
        return await Plans(scope).ListAsync(userId, CancellationToken.None);
    }

    private async Task<NutritionTargetDay> DayAsync(Guid userId, DateOnly date)
    {
        await using var scope = fixture.CreateScope();
        return await Plans(scope).GetDayAsync(userId, date, CancellationToken.None);
    }

    private async Task<bool> SetOverrideAsync(NutritionTargetOverride item)
    {
        await using var scope = fixture.CreateScope();
        return await Plans(scope).SetOverrideAsync(item, CancellationToken.None);
    }

    private async Task<int> PlanCountAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().NutritionTargetPlans.CountAsync(plan => plan.UserId == userId);
    }

    private async Task<int> OverrideCountAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().NutritionTargetOverrides.CountAsync(item => item.UserId == userId);
    }

    private async Task<List<DateOnly>> OverrideDatesAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().NutritionTargetOverrides
            .Where(item => item.UserId == userId).OrderBy(item => item.Date).Select(item => item.Date).ToListAsync();
    }

    private static string RawPlanSql(Guid id, Guid userId, DateOnly startsOn, DateOnly endsOn) =>
        $"""
        INSERT INTO nutrition_target_plans (id, user_id, starts_on, ends_on, default_calories_kcal, created_at_utc, updated_at_utc)
        VALUES ('{id}', '{userId}', DATE '{startsOn:yyyy-MM-dd}', DATE '{endsOn:yyyy-MM-dd}', 2200, now(), now())
        """;

    private async Task<Guid> InsertRawPlanAsync(Guid userId, DateOnly startsOn, DateOnly endsOn)
    {
        var id = Guid.CreateVersion7();
        await using var scope = fixture.CreateScope();
        await Database(scope).ExecuteSqlRawAsync(RawPlanSql(id, userId, startsOn, endsOn));
        return id;
    }

    private async Task InsertRawOverrideAsync(Guid userId, Guid planId, DateOnly date, string mode, string calories)
    {
        await using var scope = fixture.CreateScope();
        await Database(scope).ExecuteSqlRawAsync(
            $"""
            INSERT INTO nutrition_target_overrides (id, user_id, plan_id, date, mode, calories_kcal, created_at_utc, updated_at_utc)
            VALUES ('{Guid.CreateVersion7()}', '{userId}', '{planId}', DATE '{date:yyyy-MM-dd}', '{mode}', {calories}, now(), now())
            """);
    }

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now);
        await PostgresAssert.InsertAsync(fixture, user);
        return user;
    }

    private async Task<List<int>> Ints(string sql)
    {
        await using var scope = fixture.CreateScope();
        return await Database(scope).SqlQueryRaw<int>(sql).ToListAsync();
    }

    private async Task<T> ScalarFreshAsync<T>(string sql)
    {
        await using var scope = fixture.CreateScope();
        return await Scalar<T>(Database(scope), sql);
    }

    private static INutritionTargetPlanRepository Plans(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<INutritionTargetPlanRepository>();

    private static DatabaseFacade Database(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database;

    private static Task<List<string>> Columns(DatabaseFacade database, string table) => Strings(database,
        $"""
        SELECT attname || ' ' || format_type(atttypid, atttypmod) || CASE WHEN attnotnull THEN ' NOT NULL' ELSE ' NULL' END AS "Value"
        FROM pg_attribute WHERE attrelid = '{table}'::regclass AND attnum > 0 AND NOT attisdropped ORDER BY attnum
        """);

    private static Task<int> TableCountAsync(DatabaseFacade database) => Scalar<int>(database,
        "SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'");

    private static Task<T> Scalar<T>(DatabaseFacade database, string sql) =>
        database.SqlQueryRaw<T>(sql).SingleAsync();

    private static Task<List<string>> Strings(DatabaseFacade database, string sql) =>
        database.SqlQueryRaw<string>(sql).ToListAsync();
}
