using LifeOS.Application.Automation;
using LifeOS.Application.Finance.Analytics.GetMonthlyAnalytics;
using LifeOS.Application.Finance.Categories;
using LifeOS.Application.Finance.Transactions;
using LifeOS.Application.Gym.History;
using LifeOS.Application.Gym.Programs;
using LifeOS.Application.Gym.Sessions;
using LifeOS.Application.Notifications;
using LifeOS.Application.Nutrition;
using LifeOS.Application.Persistence;
using LifeOS.Application.WeeklyReviews;
using LifeOS.Domain.Automation;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.Domain.Gym.Exercises;
using LifeOS.Domain.Gym.Programs;
using LifeOS.Domain.Gym.Sessions;
using LifeOS.Domain.Notifications;
using LifeOS.Domain.Nutrition;
using LifeOS.Domain.Users;
using LifeOS.Domain.WeeklyReviews;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// AUTO-002 against real PostgreSQL: the weekly_reviews / weekly_review_settings schema, the
// repository (insert-once, settings upsert, user-scoped reads, keyset pages, discovery SQL), the
// jsonb snapshot round trip, user deletion, and the weekly review automation end to end through the
// real tick engine, stores and unit of work.
//
// The database is shared by the whole PostgreSQL collection, so every assertion is scoped to this
// test's own users. End-to-end ticks use Asia/Kathmandu at an instant when no zone used by other
// tests (Europe/Rome, America/New_York) has an open weekly occurrence.
[Collection(PostgreSqlCollection.Name)]
public class WeeklyReviewPersistenceTests(PostgreSqlFixture fixture)
{
    private const string Zone = "Asia/Kathmandu";

    // Kathmandu is UTC+05:45: Sunday 2026-10-04 20:00 local = 14:15Z.
    private static readonly DateOnly WeekEnd = new(2026, 10, 4);
    private static readonly DateTimeOffset Due = new(2026, 10, 4, 14, 15, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WeekStartUtc = new(2026, 9, 27, 18, 15, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Due.AddMinutes(5);

    // ---- Schema ----

    [Fact]
    public async Task Migration_CreatesTheWeeklyReviewTables()
    {
        await using var scope = fixture.CreateScope();
        var database = Db(scope).Database;

        Assert.Contains(await database.GetAppliedMigrationsAsync(), id => id.EndsWith("_AddWeeklyReviews", StringComparison.Ordinal));
        Assert.False(database.HasPendingModelChanges());

        Assert.Equal(
            [
                "id uuid NOT NULL", "user_id uuid NOT NULL", "week_start_date date NOT NULL", "week_end_date date NOT NULL",
                "time_zone_id character varying(64) NOT NULL", "generated_at_utc timestamp with time zone NOT NULL",
                "data_version integer NOT NULL", "snapshot jsonb NOT NULL"
            ],
            await Columns(database, "weekly_reviews"));
        Assert.Equal(
            ["user_id uuid NOT NULL", "enabled boolean NOT NULL", "updated_at_utc timestamp with time zone NOT NULL"],
            await Columns(database, "weekly_review_settings"));

        Assert.Equal(
            [
                "CREATE UNIQUE INDEX \"PK_weekly_reviews\" ON public.weekly_reviews USING btree (id)",
                "CREATE UNIQUE INDEX ux_weekly_reviews_user_week_end ON public.weekly_reviews USING btree (user_id, week_end_date)"
            ],
            await Strings(database, "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'weekly_reviews' ORDER BY indexname"));
        Assert.Equal(
            ["CREATE UNIQUE INDEX \"PK_weekly_review_settings\" ON public.weekly_review_settings USING btree (user_id)"],
            await Strings(database, "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'weekly_review_settings'"));

        // FKs with ON DELETE CASCADE (confdeltype 'c') and the check constraints.
        Assert.Equal(
            ["FK_weekly_reviews_users_user_id c", "ck_weekly_reviews_data_version", "ck_weekly_reviews_snapshot", "ck_weekly_reviews_week"],
            await Constraints(database, "weekly_reviews"));
        Assert.Equal(["FK_weekly_review_settings_users_user_id c"], await Constraints(database, "weekly_review_settings"));
    }

    [Fact]
    public async Task InvalidRows_AreRejectedByTheDatabase()
    {
        var user = await NewUserAsync();
        var review = await AddReviewAsync(user.Id, WeekEnd);

        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_weekly_reviews_week", () =>
            Execute($"UPDATE weekly_reviews SET week_end_date = '2026-10-03', week_start_date = '2026-09-27' WHERE id = '{review.Id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_weekly_reviews_week", () =>
            Execute($"UPDATE weekly_reviews SET week_start_date = '2026-09-29' WHERE id = '{review.Id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_weekly_reviews_data_version", () =>
            Execute($"UPDATE weekly_reviews SET data_version = 0 WHERE id = '{review.Id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_weekly_reviews_snapshot", () =>
            Execute($"UPDATE weekly_reviews SET snapshot = '[]'::jsonb WHERE id = '{review.Id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.UniqueViolation, "ux_weekly_reviews_user_week_end", () =>
            Execute($"INSERT INTO weekly_reviews SELECT gen_random_uuid(), user_id, week_start_date, week_end_date, time_zone_id, generated_at_utc, data_version, snapshot FROM weekly_reviews WHERE id = '{review.Id}'"));
    }

    // ---- Repository ----

    [Fact]
    public async Task TryAdd_IsOncePerUserAndWeek()
    {
        var user = await NewUserAsync();
        var other = await NewUserAsync();

        Assert.True(await AddAsync(Review(user.Id, WeekEnd)));
        Assert.False(await AddAsync(Review(user.Id, WeekEnd)));
        Assert.True(await AddAsync(Review(user.Id, WeekEnd.AddDays(7))));
        Assert.True(await AddAsync(Review(other.Id, WeekEnd)));

        await using var scope = fixture.CreateScope();
        Assert.Equal(2, await Db(scope).Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM weekly_reviews WHERE user_id = {user.Id}").SingleAsync());
    }

    [Fact]
    public async Task Snapshot_RoundTrips_AsAVersionedJsonbDocument()
    {
        var user = await NewUserAsync();
        var saved = Review(user.Id, WeekEnd);
        Assert.True(await AddAsync(saved));

        await using var scope = fixture.CreateScope();
        var read = (await Reviews(scope).GetAsync(user.Id, saved.Id, default))!;

        Assert.Equal((saved.Id, saved.UserId, saved.WeekStartDate, saved.WeekEndDate, saved.TimeZoneId, saved.GeneratedAtUtc, 1),
            (read.Id, read.UserId, read.WeekStartDate, read.WeekEndDate, read.TimeZoneId, read.GeneratedAtUtc, read.DataVersion));
        AssertSameSnapshot(saved.Snapshot, read.Snapshot);

        var document = await Db(scope).Database.SqlQuery<string>($"SELECT snapshot::text AS \"Value\" FROM weekly_reviews WHERE id = {saved.Id}").SingleAsync();
        Assert.StartsWith("{", document);
        Assert.Contains("\"finance\":", document);
        Assert.Contains("\"date\": \"2026-09-29\"", document);
        Assert.Contains("\"expenses\": 42.50", document);
        Assert.DoesNotContain("generatedAt", document);
    }

    [Fact]
    public async Task AnUnknownDataVersion_IsNeverGuessed()
    {
        var user = await NewUserAsync();
        var saved = await AddReviewAsync(user.Id, WeekEnd);
        await Execute($"UPDATE weekly_reviews SET data_version = 99 WHERE id = '{saved.Id}'");

        await using var scope = fixture.CreateScope();
        await Assert.ThrowsAsync<NotSupportedException>(() => Reviews(scope).GetAsync(user.Id, saved.Id, default));
    }

    [Fact]
    public async Task Reads_AreScopedToTheOwner_AndPagedNewestWeekFirst()
    {
        var user = await NewUserAsync();
        var other = await NewUserAsync();
        var weeks = new List<WeeklyReview>();

        for (var index = 0; index < 3; index++)
        {
            weeks.Add(await AddReviewAsync(user.Id, WeekEnd.AddDays(-7 * index)));
        }

        var others = await AddReviewAsync(other.Id, WeekEnd);

        await using var scope = fixture.CreateScope();
        var reviews = Reviews(scope);

        Assert.Equal([weeks[0].Id, weeks[1].Id], (await reviews.GetPageAsync(user.Id, null, 2, default)).Select(item => item.Id));
        Assert.Equal([weeks[2].Id], (await reviews.GetPageAsync(user.Id, weeks[1].WeekEndDate, 2, default)).Select(item => item.Id));
        Assert.Null(await reviews.GetAsync(user.Id, others.Id, default));
        Assert.Equal(others.Id, (await reviews.GetAsync(other.Id, others.Id, default))!.Id);
        Assert.Equal(weeks[1].Id, await reviews.FindIdAsync(user.Id, weeks[1].WeekEndDate, default));
        Assert.Null(await reviews.FindIdAsync(user.Id, WeekEnd.AddDays(7), default));

        var item = (await reviews.GetPageAsync(user.Id, null, 1, default)).Single();
        Assert.Equal((new DateOnly(2026, 9, 28), WeekEnd, weeks[0].GeneratedAtUtc), (item.WeekStartDate, item.WeekEndDate, item.GeneratedAtUtc));
    }

    [Fact]
    public async Task Settings_AreEnabledByDefault_AndUpserted()
    {
        var user = await NewUserAsync();

        await using var scope = fixture.CreateScope();
        var reviews = Reviews(scope);

        Assert.True(await reviews.IsEnabledAsync(user.Id, default));
        Assert.True(await reviews.SetSettingsAsync(WeeklyReviewSettings.Create(user.Id, false, Now), default));
        Assert.False(await reviews.IsEnabledAsync(user.Id, default));
        Assert.True(await reviews.SetSettingsAsync(WeeklyReviewSettings.Create(user.Id, true, Now.AddMinutes(1)), default));
        Assert.True(await reviews.IsEnabledAsync(user.Id, default));
        Assert.Equal(1, await Db(scope).Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM weekly_review_settings WHERE user_id = {user.Id}").SingleAsync());
        Assert.False(await reviews.SetSettingsAsync(WeeklyReviewSettings.Create(Guid.CreateVersion7(), false, Now), default));
    }

    [Fact]
    public async Task DeletingTheUser_DeletesReviewsAndSettings()
    {
        var user = await NewUserAsync();
        await AddReviewAsync(user.Id, WeekEnd);

        await using (var scope = fixture.CreateScope())
        {
            Assert.True(await Reviews(scope).SetSettingsAsync(WeeklyReviewSettings.Create(user.Id, false, Now), default));
        }

        await Execute($"DELETE FROM users WHERE id = '{user.Id}'");

        await using var check = fixture.CreateScope();
        Assert.Equal(0, await Db(check).Database.SqlQuery<int>(
            $"SELECT ((SELECT count(*) FROM weekly_reviews WHERE user_id = {user.Id}) + (SELECT count(*) FROM weekly_review_settings WHERE user_id = {user.Id}))::int AS \"Value\"").SingleAsync());
    }

    [Fact]
    public async Task FindDueUsers_IsOpenZoneEnabledAndNotYetDone()
    {
        var due = await NewUserAsync(Zone);
        var disabled = await NewUserAsync(Zone);
        var executed = await NewUserAsync(Zone);
        var reviewed = await NewUserAsync(Zone);
        var otherZone = await NewUserAsync("Asia/Tokyo");
        var noZone = await NewUserAsync();
        var openZones = new[] { new WeeklyReviewOpenZone(Zone, "2026-10-04", WeekEnd) };

        await using (var scope = fixture.CreateScope())
        {
            await Reviews(scope).SetSettingsAsync(WeeklyReviewSettings.Create(disabled.Id, false, Now), default);
            Assert.True(await scope.ServiceProvider.GetRequiredService<IAutomationExecutionStore>().TryClaimAsync(
                AutomationExecution.Claim(executed.Id, WeeklyReviewAutomationHandler.Type, "2026-10-04", Zone, Due, Due.AddHours(24), Now, AutomationExecutionPolicy.Lease), default));
        }

        await AddReviewAsync(reviewed.Id, WeekEnd);
        var mine = new[] { due.Id, disabled.Id, executed.Id, reviewed.Id, otherZone.Id, noZone.Id };

        await using var read = fixture.CreateScope();
        var found = (await Reviews(read).FindDueUsersAsync(WeeklyReviewAutomationHandler.Type, openZones, 10_000, default))
            .Where(user => mine.Contains(user.UserId))
            .ToList();

        Assert.Equal([new WeeklyReviewDueUser(due.Id, Zone)], found);
        Assert.Contains(Zone, await Reviews(read).GetUserTimeZoneIdsAsync(default));
        Assert.Empty(await Reviews(read).FindDueUsersAsync(WeeklyReviewAutomationHandler.Type, [], 10, default));

        // Another automation type's execution does not count as done.
        Assert.Contains(
            await Reviews(read).FindDueUsersAsync("OtherAutomation", openZones, 10_000, default),
            user => user.UserId == executed.Id);
    }

    [Fact]
    public async Task FindDueUsers_IsOrderedById_AndLimited()
    {
        var users = new[] { await NewUserAsync("Pacific/Chatham"), await NewUserAsync("Pacific/Chatham"), await NewUserAsync("Pacific/Chatham") };

        await using var scope = fixture.CreateScope();
        var found = await Reviews(scope).FindDueUsersAsync(
            WeeklyReviewAutomationHandler.Type, [new WeeklyReviewOpenZone("Pacific/Chatham", "2026-10-04", WeekEnd)], 2, default);

        Assert.Equal(users.Select(user => user.Id).Order().Take(2), found.Select(user => user.UserId));
    }

    // ---- Nutrition range read ----

    [Fact]
    public async Task MealDays_AreReadInOneRange_WithTheirSnapshots()
    {
        var user = await NewUserAsync();
        var monday = new DateOnly(2026, 9, 28);
        var analyzed = await MealAsync(user.Id, monday, "Pasta", analyzedKcal: 600m);
        await MealAsync(user.Id, monday.AddDays(6), "Soup");
        await MealAsync(user.Id, monday.AddDays(7), "Next week");
        await MealAsync(user.Id, monday.AddDays(-1), "Last week");

        await using var scope = fixture.CreateScope();
        var days = await scope.ServiceProvider.GetRequiredService<IMealNutritionRepository>().GetDaysAsync(user.Id, monday, monday.AddDays(6), default);

        Assert.Equal(["Pasta", "Soup"], days.Select(meal => meal.Meal.Description));
        Assert.Equal(600m, days.Single(meal => meal.Meal.Id == analyzed.Id).Nutrition!.CaloriesKcal);
        Assert.Null(days.Single(meal => meal.Meal.Description == "Soup").Nutrition);
    }

    // ---- End to end ----

    [Fact]
    public async Task Tick_SavesTheReview_CompletesTheExecution_AndEnqueuesOneDeliveryPerActiveDevice()
    {
        var user = await NewUserAsync(Zone);
        var device = await DeviceAsync(user.Id);
        await SeedWeekAsync(user);

        await TickAsync(Now);

        await using var scope = fixture.CreateScope();
        var db = Db(scope);
        var execution = await db.AutomationExecutions.AsNoTracking().SingleAsync(row => row.UserId == user.Id);
        var review = (await Reviews(scope).GetAsync(user.Id, execution.ResultId!.Value, default))!;
        var delivery = await db.NotificationDeliveries.AsNoTracking().SingleAsync(row => row.UserId == user.Id);

        Assert.Equal((AutomationExecutionStatus.Succeeded, "WeeklyReview", "2026-10-04", Zone, Due, 1),
            (execution.Status, execution.AutomationType, execution.OccurrenceKey, execution.TimeZoneId, execution.ScheduledForUtc, execution.AttemptCount));
        Assert.Equal((WeekEnd, Zone, Now, 1), (review.WeekEndDate, review.TimeZoneId, review.GeneratedAtUtc, review.DataVersion));
        Assert.Equal((device.Id, NotificationType.WeeklyReviewReady, $"automation:{execution.Id:D}", "weekly_review", (Guid?)review.Id, NotificationDeliveryStatus.Pending),
            (delivery.DeviceRegistrationId, delivery.NotificationType, delivery.NotificationKey, delivery.ResourceType, delivery.ResourceId, delivery.Status));

        var eur = Assert.Single(review.Snapshot.Finance.Currencies);
        Assert.Equal((12.50m, 12.50m), (eur.Expenses, Assert.Single(eur.ExpenseCategories).Amount));
        Assert.Equal((1, 45L * 60), (review.Snapshot.Gym.CompletedWorkouts, review.Snapshot.Gym.TotalDurationSeconds));
        Assert.Equal((2, 1, 600m), (review.Snapshot.Nutrition.MealCount, review.Snapshot.Nutrition.AnalyzedMealCount, review.Snapshot.Nutrition.AnalyzedCaloriesKcal));
    }

    [Fact]
    public async Task RepeatedAndLateTicks_NeverDuplicateTheReviewOrTheDeliveries()
    {
        var user = await NewUserAsync(Zone);
        await DeviceAsync(user.Id);

        await TickAsync(Now);
        await TickAsync(Now.AddMinutes(10));
        await TickAsync(Now.AddHours(20));

        await using var scope = fixture.CreateScope();
        var db = Db(scope);
        Assert.Single(await db.AutomationExecutions.AsNoTracking().Where(row => row.UserId == user.Id).ToListAsync());
        Assert.Single(await db.NotificationDeliveries.AsNoTracking().Where(row => row.UserId == user.Id).ToListAsync());
        Assert.Single(await Reviews(scope).GetPageAsync(user.Id, null, 10, default));
    }

    [Fact]
    public async Task AnExpiredOccurrence_CreatesNothing()
    {
        var user = await NewUserAsync(Zone);

        await TickAsync(Due.AddHours(24));

        await using var scope = fixture.CreateScope();
        Assert.Empty(await Reviews(scope).GetPageAsync(user.Id, null, 10, default));
        Assert.False(await Db(scope).AutomationExecutions.AsNoTracking().AnyAsync(row => row.UserId == user.Id));
    }

    // AUTO-002 D-6: the artifact commits with the fenced completion or not at all. A conflicting
    // review rolls back the completion and the deliveries; the attempt stays Running for takeover.
    [Fact]
    public async Task AConflictingArtifact_RollsBackTheCompletionAndTheDeliveries()
    {
        var user = await NewUserAsync(Zone);
        await DeviceAsync(user.Id);
        await AddReviewAsync(user.Id, WeekEnd);
        var conflicting = Review(user.Id, WeekEnd);
        var handler = new TestAutomationHandler($"Test-{Guid.NewGuid():N}");
        handler.AddDue(user.Id, "2026-10-04", Due, Zone);

        await using (var scope = fixture.CreateScope())
        {
            var reviews = Reviews(scope);
            handler.Execute = _ => Task.FromResult(AutomationResult.Succeeded(
                conflicting.Id,
                new AutomationNotification(NotificationType.WeeklyReviewReady, "weekly_review", conflicting.Id),
                saveArtifact: transaction => reviews.TryAddAsync(conflicting, transaction)));

            await Tick(scope, handler, Now).RunAsync();
        }

        await using var check = fixture.CreateScope();
        var execution = await Db(check).AutomationExecutions.AsNoTracking().SingleAsync(row => row.AutomationType == handler.AutomationType);
        Assert.Equal((AutomationExecutionStatus.Running, (Guid?)null), (execution.Status, execution.ResultId));
        Assert.False(await Db(check).NotificationDeliveries.AsNoTracking().AnyAsync(row => row.SourceExecutionId == execution.Id));
        Assert.Null(await Reviews(check).GetAsync(user.Id, conflicting.Id, default));
    }

    [Fact]
    public async Task Generation_DoesNotMutateTheSourceModules()
    {
        var user = await NewUserAsync(Zone);
        await SeedWeekAsync(user);
        var before = await SourceFingerprintAsync(user.Id);

        await TickAsync(Now);

        Assert.Equal(before, await SourceFingerprintAsync(user.Id));
    }

    // ---- Helpers ----

    private async Task TickAsync(DateTimeOffset now)
    {
        await using var scope = fixture.CreateScope();
        await Tick(scope, Handler(scope, new FixedTimeProvider(now)), now).RunAsync();
    }

    private static RunAutomationTick Tick(AsyncServiceScope scope, IAutomationHandler handler, DateTimeOffset now)
    {
        var services = scope.ServiceProvider;
        var time = new FixedTimeProvider(now);
        var deliveries = services.GetRequiredService<INotificationDeliveryStore>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        var dispatcher = new NotificationDispatcher(
            deliveries, services.GetRequiredService<IDeviceRegistrationRepository>(), services.GetRequiredService<INotificationPreferencesRepository>(), unitOfWork, time);

        return new RunAutomationTick([handler], services.GetRequiredService<IAutomationExecutionStore>(), dispatcher, deliveries, unitOfWork, new AutomationTickGuard(), time);
    }

    // The production wiring, from the scope's real repositories.
    private static WeeklyReviewAutomationHandler Handler(AsyncServiceScope scope, TimeProvider time)
    {
        var services = scope.ServiceProvider;

        return new WeeklyReviewAutomationHandler(
            Reviews(scope),
            new WeeklyReviewSnapshotBuilder(
                new GetMonthlyAnalyticsHandler(services.GetRequiredService<ITransactionRepository>(), services.GetRequiredService<ICategoryRepository>()),
                new GetWorkoutHistoryHandler(services.GetRequiredService<IWorkoutSessionRepository>()),
                services.GetRequiredService<IMealNutritionRepository>()),
            time);
    }

    // One expense in the week and one before it, one completed workout, an analyzed and an unanalyzed meal.
    private async Task SeedWeekAsync(User user)
    {
        var account = Account.Create(user.Id, "Bank", AccountType.BankAccount, "EUR", Now.AddDays(-30));
        var food = Category.Create(user.Id, "Food", CategoryType.Expense, parent: null, Now.AddDays(-30));
        await PostgresAssert.InsertAsync(fixture, account, food);
        await PostgresAssert.InsertAsync(fixture,
            Transaction.CreateExpense(user.Id, account.Id, food.Id, 12.50m, "EUR", WeekStartUtc.AddHours(1), null, Now.AddDays(-1)),
            Transaction.CreateExpense(user.Id, account.Id, food.Id, 80m, "EUR", WeekStartUtc.AddMinutes(-1), null, Now.AddDays(-1)));

        var exercise = Exercise.Create(user.Id, "Squat", Now.AddDays(-30));
        await PostgresAssert.InsertAsync(fixture, exercise);
        var program = WorkoutProgram.Create(user.Id, "Base", Now.AddDays(-30));
        var workout = program.AddWorkout("Lower");
        workout.AddBlock(WorkoutBlockKind.Single, 90, [new ExercisePrescription(exercise, null, Enumerable.Repeat(new RepRange(5, 5), 3).ToList())]);

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWorkoutProgramRepository>().AddAsync(program, default);
        }

        var started = WeekStartUtc.AddDays(2);
        var session = WorkoutSession.Start(program, workout, started);
        session.RecordSet(session.ExecutionOrder[0].Id, 5, 100m, started.AddMinutes(5));
        session.Finish(started.AddMinutes(45));

        await using (var scope = fixture.CreateScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<IWorkoutSessionRepository>().TryAddAsync(session, default));
        }

        await MealAsync(user.Id, new DateOnly(2026, 9, 29), "Pasta", analyzedKcal: 600m);
        await MealAsync(user.Id, new DateOnly(2026, 9, 30), "Soup");
    }

    private async Task<MealEntry> MealAsync(Guid userId, DateOnly date, string description, decimal? analyzedKcal = null)
    {
        var meal = MealEntry.Create(userId, description, MealType.Lunch, date, new TimeOnly(12, 0), 345, Now.AddDays(-7));
        await PostgresAssert.InsertAsync(fixture, meal);

        if (analyzedKcal is { } kcal)
        {
            await PostgresAssert.InsertAsync(fixture, MealNutritionSnapshot.Create(meal.Id, NutritionValues.Create(kcal, 20m, 80m, 15m), NutritionSource.AiConfirmed, Now.AddDays(-7)));
        }

        return meal;
    }

    // Row counts and the latest change instants of everything a review reads.
    private async Task<string> SourceFingerprintAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();

        return await Db(scope).Database.SqlQuery<string>($"""
            SELECT concat_ws('|',
                (SELECT count(*) FROM transactions WHERE user_id = {userId}),
                (SELECT sum(amount) FROM transactions WHERE user_id = {userId}),
                (SELECT max(occurred_at_utc) FROM transactions WHERE user_id = {userId}),
                (SELECT count(*) FROM categories WHERE user_id = {userId}),
                (SELECT count(*) FROM workout_sessions WHERE user_id = {userId}),
                (SELECT count(*) FROM meal_entries WHERE user_id = {userId}),
                (SELECT max(updated_at_utc) FROM meal_entries WHERE user_id = {userId}),
                (SELECT count(*) FROM meal_nutrition_snapshots s JOIN meal_entries m ON m.id = s.meal_entry_id WHERE m.user_id = {userId}),
                (SELECT max(s.updated_at_utc) FROM meal_nutrition_snapshots s JOIN meal_entries m ON m.id = s.meal_entry_id WHERE m.user_id = {userId})) AS "Value"
            """).SingleAsync();
    }

    private async Task<DeviceRegistration> DeviceAsync(Guid userId)
    {
        var registration = DeviceRegistration.Register(userId, $"install-{Guid.NewGuid():N}", DevicePlatform.Android, $"token-{Guid.NewGuid():N}", true, Now.AddDays(-1));

        await using var scope = fixture.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IDeviceRegistrationRepository>().UpsertAsync(registration, default));

        return await Db(scope).DeviceRegistrations.AsNoTracking().SingleAsync(row => row.UserId == userId);
    }

    private static WeeklyReview Review(Guid userId, DateOnly weekEnd) =>
        WeeklyReview.Create(userId, weekEnd, Zone, Now.AddDays(weekEnd.DayNumber - WeekEnd.DayNumber), new WeeklyReviewSnapshot(
            new WeeklyFinanceSummary([
                new WeeklyCurrencySummary("EUR", 42.50m, 100m, 57.50m, [new WeeklyExpenseCategory("Food", 30.00m), new WeeklyExpenseCategory("Travel", 12.50m)]),
                new WeeklyCurrencySummary("USD", 5m, 0m, -5m, [new WeeklyExpenseCategory("Books", 5m)])]),
            new WeeklyGymSummary(1, 3600, 10, 12, [new WeeklyWorkout(weekEnd.AddDays(-5), "Upper", "Base", 3600, 10, 12)]),
            new WeeklyNutritionSummary(1, 2, 1, 0, 650.5m, 30m, 80m, 20m, [new WeeklyNutritionDay(weekEnd.AddDays(-6), 2, 1, 650.5m, 30m, 80m, 20m)])));

    private async Task<bool> AddAsync(WeeklyReview review)
    {
        await using var scope = fixture.CreateScope();
        return await Reviews(scope).TryAddAsync(review, default);
    }

    private async Task<WeeklyReview> AddReviewAsync(Guid userId, DateOnly weekEnd)
    {
        var review = Review(userId, weekEnd);
        Assert.True(await AddAsync(review));

        return review;
    }

    private static void AssertSameSnapshot(WeeklyReviewSnapshot expected, WeeklyReviewSnapshot actual)
    {
        Assert.Equal(expected.Finance.Currencies.Select(currency => currency with { ExpenseCategories = [] }), actual.Finance.Currencies.Select(currency => currency with { ExpenseCategories = [] }));
        Assert.Equal(expected.Finance.Currencies.SelectMany(currency => currency.ExpenseCategories), actual.Finance.Currencies.SelectMany(currency => currency.ExpenseCategories));
        Assert.Equal(expected.Gym with { Workouts = [] }, actual.Gym with { Workouts = [] });
        Assert.Equal(expected.Gym.Workouts, actual.Gym.Workouts);
        Assert.Equal(expected.Nutrition with { Days = [] }, actual.Nutrition with { Days = [] });
        Assert.Equal(expected.Nutrition.Days, actual.Nutrition.Days);
    }

    private async Task<User> NewUserAsync(string? timeZoneId = null)
    {
        var user = User.CreateFromExternalIdentity(null, null, Now.AddDays(-60));

        if (timeZoneId is not null)
        {
            user.SetTimeZone(timeZoneId);
        }

        await PostgresAssert.InsertAsync(fixture, user);

        return user;
    }

    private async Task Execute(string sql)
    {
        await using var scope = fixture.CreateScope();
        await Db(scope).Database.ExecuteSqlRawAsync(sql);
    }

    private static IWeeklyReviewRepository Reviews(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IWeeklyReviewRepository>();

    private static LifeOSDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();

    private static Task<List<string>> Columns(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database, string table) =>
        Strings(database,
            $"""
            SELECT attname || ' ' || format_type(atttypid, atttypmod) || CASE WHEN attnotnull THEN ' NOT NULL' ELSE ' NULL' END AS "Value"
            FROM pg_attribute WHERE attrelid = '{table}'::regclass AND attnum > 0 AND NOT attisdropped ORDER BY attnum
            """);

    private static async Task<List<string>> Constraints(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database, string table) =>
        (await Strings(database,
            $"""
            SELECT conname::text || CASE WHEN contype = 'f' THEN ' ' || confdeltype::text ELSE '' END AS "Value" FROM pg_constraint
            WHERE conrelid = '{table}'::regclass AND contype IN ('c', 'f')
            """)).Order(StringComparer.Ordinal).ToList();

    private static Task<List<string>> Strings(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database, string sql) =>
        database.SqlQueryRaw<string>(sql).ToListAsync();
}
