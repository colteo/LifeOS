using LifeOS.Application.Automation;
using LifeOS.Application.Finance.Analytics.GetMonthlyAnalytics;
using LifeOS.Application.Gym.History;
using LifeOS.Application.Notifications;
using LifeOS.Application.WeeklyReviews;
using LifeOS.Domain.Automation;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.Domain.Gym.Exercises;
using LifeOS.Domain.Gym.Programs;
using LifeOS.Domain.Gym.Sessions;
using LifeOS.Domain.Notifications;
using LifeOS.Domain.Nutrition;
using LifeOS.Domain.WeeklyReviews;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.WeeklyReviews;

// AUTO-002: the weekly review automation through the real tick engine (AUTO-001) with in-memory
// stores, and the real Finance/Gym/Nutrition use cases over their in-memory repositories. PostgreSQL
// behaviour (unique index, transaction rollback, discovery SQL) is proven in LifeOS.IntegrationTests.
public class WeeklyReviewAutomationTests
{
    // Week Mon 2026-09-28 – Sun 2026-10-04, Europe/Rome (CEST, +02:00). Due Sunday 20:00 = 18:00Z.
    private static readonly DateOnly WeekEnd = new(2026, 10, 4);
    private static readonly DateTimeOffset Due = new(2026, 10, 4, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WeekStartUtc = new(2026, 9, 27, 22, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WeekEndUtc = new(2026, 10, 4, 22, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserA = TestUsers.A;
    private static readonly Guid UserB = TestUsers.B;

    private readonly ManualTimeProvider _clock = new(Due.AddMinutes(3));
    private readonly InMemoryAutomationExecutionStore _store = new();
    private readonly InMemoryWeeklyReviewRepository _reviews;
    private readonly InMemoryTransactionRepository _transactions = new();
    private readonly InMemoryCategoryRepository _categories = new();
    private readonly InMemoryWorkoutSessionRepository _sessions = new();
    private readonly InMemoryExerciseRepository _exercises = new();
    private readonly InMemoryMealEntryRepository _meals = new();
    private readonly InMemoryDeviceRegistrationRepository _devices = new();
    private readonly InMemoryNotificationDeliveryStore _deliveries;
    private readonly FakeUnitOfWork _unitOfWork = new();

    public WeeklyReviewAutomationTests()
    {
        _reviews = new InMemoryWeeklyReviewRepository(_store);
        _deliveries = new InMemoryNotificationDeliveryStore(_devices);
    }

    // ---- Scheduling ----

    [Theory]
    [InlineData("2026-10-04T17:59:59Z", false)] // Sunday 19:59:59 Rome
    [InlineData("2026-10-04T18:00:00Z", true)]  // Sunday 20:00 Rome
    [InlineData("2026-10-05T07:00:00Z", true)]  // Monday morning: late, inside the window
    [InlineData("2026-10-05T17:59:59Z", true)]  // last second of the 24 h window
    [InlineData("2026-10-05T18:00:00Z", false)] // expired
    [InlineData("2026-10-03T18:00:00Z", false)] // Saturday 20:00
    public async Task FindDue_IsSunday2000Rome_With24HoursLateness(string now, bool due)
    {
        _reviews.AddUser(UserA, "Europe/Rome");

        var found = await Handler().FindDueAsync(DateTimeOffset.Parse(now), 25, default);

        if (due)
        {
            Assert.Equal([new DueOccurrence(UserA, "2026-10-04", "Europe/Rome", Due)], found);
        }
        else
        {
            Assert.Empty(found);
        }
    }

    [Theory]
    // Fall back (Sunday 25 Oct 2026, 03:00 CEST → 02:00 CET): 20:00 is CET = 19:00Z.
    [InlineData("2026-10-25T19:00:00Z", "2026-10-25", "2026-10-25T19:00:00Z")]
    // Spring forward (Sunday 29 Mar 2026, 02:00 CET → 03:00 CEST): 20:00 is CEST = 18:00Z.
    [InlineData("2026-03-29T18:00:00Z", "2026-03-29", "2026-03-29T18:00:00Z")]
    // First Sunday after the fall back: CET.
    [InlineData("2026-11-01T19:30:00Z", "2026-11-01", "2026-11-01T19:00:00Z")]
    public async Task FindDue_OnDstSundays_UsesTheLocalWallClock(string now, string key, string scheduled)
    {
        _reviews.AddUser(UserA, "Europe/Rome");

        var found = Assert.Single(await Handler().FindDueAsync(DateTimeOffset.Parse(now), 25, default));

        Assert.Equal((key, DateTimeOffset.Parse(scheduled)), (found.OccurrenceKey, found.ScheduledForUtc));
    }

    [Fact]
    public async Task FindDue_FallBackSunday_IsNotDueAtTheSummerTimeInstant()
    {
        _reviews.AddUser(UserA, "Europe/Rome");

        Assert.Empty(await Handler().FindDueAsync(new DateTimeOffset(2026, 10, 25, 18, 30, 0, TimeSpan.Zero), 25, default));
    }

    [Fact]
    public async Task FindDue_EachZoneUsesItsOwnSunday2000()
    {
        _reviews.AddUser(UserA, "Europe/Rome");
        _reviews.AddUser(UserB, "America/New_York"); // Sunday 20:00 EDT = Monday 00:00Z

        var atRome = await Handler().FindDueAsync(Due, 25, default);
        var atNewYork = await Handler().FindDueAsync(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero), 25, default);

        Assert.Equal([UserA], atRome.Select(occurrence => occurrence.UserId));
        Assert.Equal(
            [(UserA, "2026-10-04"), (UserB, "2026-10-04")],
            atNewYork.Select(occurrence => (occurrence.UserId, occurrence.OccurrenceKey)).OrderBy(pair => pair.UserId));
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero), atNewYork.Single(occurrence => occurrence.UserId == UserB).ScheduledForUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("+02:00")]
    [InlineData("W. Europe Standard Time")]
    public async Task FindDue_WithoutAValidIanaZone_SchedulesNothing(string? zone)
    {
        _reviews.AddUser(UserA, zone);

        Assert.Empty(await Handler().FindDueAsync(Due, 25, default));
    }

    [Fact]
    public async Task FindDue_IsEnabledByDefault_AndSkipsDisabledUsers()
    {
        _reviews.AddUser(UserA);
        _reviews.AddUser(UserB);
        _reviews.Settings[UserB] = WeeklyReviewSettings.Create(UserB, false, Due);

        Assert.Equal([UserA], (await Handler().FindDueAsync(Due, 25, default)).Select(occurrence => occurrence.UserId));
        Assert.True(await _reviews.IsEnabledAsync(UserA, default));
    }

    [Fact]
    public async Task FindDue_RespectsTheLimit()
    {
        _reviews.AddUser(UserA);
        _reviews.AddUser(UserB);

        Assert.Single(await Handler().FindDueAsync(Due, 1, default));
        Assert.Empty(await Handler().FindDueAsync(Due, 0, default));
    }

    [Fact]
    public void OccurrenceKey_IsTheLocalWeekEndingSunday()
    {
        Assert.Equal("2026-10-04", WeeklyReviewAutomationHandler.OccurrenceKey(WeekEnd));
        Assert.True(WeeklyReviewAutomationHandler.TryParseOccurrenceKey("2026-10-04", out var parsed));
        Assert.Equal(WeekEnd, parsed);
        Assert.False(WeeklyReviewAutomationHandler.TryParseOccurrenceKey("2026-10-03", out _)); // a Saturday
        Assert.False(WeeklyReviewAutomationHandler.TryParseOccurrenceKey("04/10/2026", out _));
        Assert.Equal(("WeeklyReview", TimeSpan.FromHours(24)), (Handler().AutomationType, Handler().MaxLateness));
    }

    // ---- Period ----

    [Fact]
    public void Period_IsLocalMondayToSunday_AsAHalfOpenUtcRange()
    {
        var period = WeeklyReviewPeriod.For(WeekEnd, Rome);

        Assert.Equal((new DateOnly(2026, 9, 28), WeekEnd, WeekStartUtc, WeekEndUtc), (period.StartDate, period.EndDate, period.StartUtc, period.EndUtc));
        Assert.Throws<ArgumentException>(() => WeeklyReviewPeriod.For(new DateOnly(2026, 10, 3), Rome));
    }

    [Theory]
    [InlineData("2026-10-25", 169)] // fall back week
    [InlineData("2026-03-29", 167)] // spring forward week
    [InlineData("2026-10-04", 168)]
    public void Period_OfADstWeek_IsNot168Hours(string weekEnd, int hours)
    {
        var period = WeeklyReviewPeriod.For(DateOnly.Parse(weekEnd), Rome);

        Assert.Equal(TimeSpan.FromHours(hours), period.EndUtc - period.StartUtc);
    }

    // ---- Generation through the tick ----

    [Fact]
    public async Task Tick_SavesOneReview_AndEnqueuesWeeklyReviewReadyForEachActiveDevice()
    {
        _reviews.AddUser(UserA);
        await AddDeviceAsync(UserA, "installation-phone-0001", "token-phone");
        await AddDeviceAsync(UserA, "installation-tablet-001", "token-tablet");

        var result = await Tick().RunAsync();

        Assert.Equal(1, result.Executions);
        var review = Assert.Single(_reviews.Reviews);
        Assert.Equal((UserA, new DateOnly(2026, 9, 28), WeekEnd, "Europe/Rome", _clock.UtcNow, 1),
            (review.UserId, review.WeekStartDate, review.WeekEndDate, review.TimeZoneId, review.GeneratedAtUtc, review.DataVersion));

        var execution = _store.Single("2026-10-04");
        Assert.Equal((AutomationExecutionStatus.Succeeded, (Guid?)review.Id, "WeeklyReview"), (execution.Status, execution.ResultId, execution.AutomationType));

        Assert.Equal(2, _deliveries.Rows.Count);
        Assert.All(_deliveries.Rows, row =>
        {
            Assert.Equal(UserA, row.UserId);
            Assert.Equal(NotificationType.WeeklyReviewReady, row.Type);
            Assert.Equal($"automation:{execution.Id:D}", row.NotificationKey);
            Assert.Equal(("weekly_review", (Guid?)review.Id), (row.ResourceType, row.ResourceId));
            Assert.Equal(_clock.UtcNow.AddHours(24), row.ExpiresAtUtc);
        });
    }

    // PD-3 / W-8: fixed English copy; the data is only the type and the review's opaque id.
    [Fact]
    public async Task ThePush_HasFixedNonPersonalCopy_AndTargetsTheReview()
    {
        _reviews.AddUser(UserA);
        await AddDeviceAsync(UserA, "installation-phone-0001", "token-phone");
        var food = await CategoryAsync(UserA, "Secret restaurant", CategoryType.Expense);
        Expense(UserA, Guid.CreateVersion7(), food, 123.45m, WeekStartUtc.AddDays(1));
        Meal(UserA, new DateOnly(2026, 9, 29), "Private meal text");

        await Tick().RunAsync();

        var row = Assert.Single(_deliveries.Rows);
        var message = NotificationCatalog.MessageFor(new NotificationDeliveryWorkItem(
            row.Id, 1, row.UserId, row.DeviceRegistrationId, row.NotificationKey, row.Type, row.ResourceType, row.ResourceId, row.ExpiresAtUtc));
        var review = Assert.Single(_reviews.Reviews);

        Assert.Equal(("LifeOS", "Your weekly review is ready"), (message.Title, message.Body));
        Assert.Equal(new Dictionary<string, string> { ["type"] = "weekly_review", ["id"] = review.Id.ToString("D") }, message.Data);
        var visible = $"{message.Title} {message.Body} {string.Join(" ", message.Data.Values)}";
        Assert.DoesNotContain("123", visible);
        Assert.DoesNotContain("Secret", visible);
        Assert.DoesNotContain("Private", visible);
    }

    [Fact]
    public async Task Tick_WithoutActiveDevices_StillSavesTheReview()
    {
        _reviews.AddUser(UserA);

        await Tick().RunAsync();

        Assert.Single(_reviews.Reviews);
        Assert.Empty(_deliveries.Rows);
        Assert.Equal(AutomationExecutionStatus.Succeeded, _store.Single("2026-10-04").Status);
    }

    [Fact]
    public async Task LaterTicks_NeverDuplicateTheReviewOrTheNotification()
    {
        _reviews.AddUser(UserA);
        await AddDeviceAsync(UserA, "installation-phone-0001", "token-phone");

        await Tick().RunAsync();
        _clock.Advance(TimeSpan.FromMinutes(10));
        var second = await Tick().RunAsync();
        _clock.Advance(TimeSpan.FromHours(5));
        await Tick().RunAsync();

        Assert.Equal(0, second.Executions);
        Assert.Single(_reviews.Reviews);
        Assert.Single(_deliveries.Rows);
        Assert.Single(_store.Rows);
    }

    [Fact]
    public async Task ARetriedOccurrence_SavesOneReview_AndNotifiesOnce()
    {
        _reviews.AddUser(UserA);
        await AddDeviceAsync(UserA, "installation-phone-0001", "token-phone");
        _reviews.FailReads = true;

        await Tick().RunAsync();

        var failed = _store.Single("2026-10-04");
        Assert.Equal((AutomationExecutionStatus.FailedRetryable, AutomationExecutionPolicy.UnhandledCode), (failed.Status, failed.LastFailureCode));
        Assert.Empty(_reviews.Reviews);
        Assert.Empty(_deliveries.Rows);

        _reviews.FailReads = false;
        _clock.Advance(TimeSpan.FromMinutes(10));
        await Tick().RunAsync();
        _clock.Advance(TimeSpan.FromMinutes(30));
        await Tick().RunAsync();

        var succeeded = _store.Single("2026-10-04");
        Assert.Equal((AutomationExecutionStatus.Succeeded, 2), (succeeded.Status, succeeded.AttemptCount));
        Assert.Equal(succeeded.ResultId, Assert.Single(_reviews.Reviews).Id);
        Assert.Single(_deliveries.Rows);
    }

    [Fact]
    public async Task AnExistingReview_IsRecordedAsTheResult_WithoutANewNotification()
    {
        _reviews.AddUser(UserA);
        await AddDeviceAsync(UserA, "installation-phone-0001", "token-phone");
        var existing = WeeklyReview.Create(UserA, WeekEnd, "Europe/Rome", Due, EmptySnapshot);
        _reviews.Reviews.Add(existing);

        var result = await Handler().ExecuteAsync(Occurrence(), default);

        var success = Assert.IsType<AutomationResult.Success>(result);
        Assert.Equal(existing.Id, success.ResultId);
        Assert.Null(success.Notification);
        Assert.Null(success.SaveArtifact);
    }

    [Fact]
    public async Task ALateRunInsideTheWindow_GeneratesTheReview()
    {
        _reviews.AddUser(UserA);
        _clock.UtcNow = Due.AddHours(23);

        await Tick().RunAsync();

        Assert.Equal(WeekEnd, Assert.Single(_reviews.Reviews).WeekEndDate);
    }

    [Fact]
    public async Task AnExpiredOccurrence_IsNotGenerated()
    {
        _reviews.AddUser(UserA);
        _clock.UtcNow = Due.AddHours(24);

        var result = await Tick().RunAsync();

        Assert.Equal(0, result.Executions);
        Assert.Empty(_reviews.Reviews);
        Assert.Empty(_store.Rows);
    }

    [Fact]
    public async Task AUserDisabledAfterDiscovery_IsNotApplicable_WithoutReviewOrPush()
    {
        _reviews.AddUser(UserA);
        _reviews.Settings[UserA] = WeeklyReviewSettings.Create(UserA, false, Due);

        Assert.IsType<AutomationResult.Inapplicable>(await Handler().ExecuteAsync(Occurrence(), default));
    }

    [Theory]
    [InlineData("2026-10-03", "Europe/Rome", WeeklyReviewAutomationHandler.InvalidOccurrenceKeyCode)]
    [InlineData("not-a-date", "Europe/Rome", WeeklyReviewAutomationHandler.InvalidOccurrenceKeyCode)]
    [InlineData("2026-10-04", "Mars/Olympus_Mons", WeeklyReviewAutomationHandler.InvalidTimeZoneCode)]
    public async Task AnInvalidOccurrence_IsAPermanentFailure_WithAStableCode(string key, string zone, string code)
    {
        var result = await Handler().ExecuteAsync(Occurrence(key, zone), default);

        Assert.Equal(code, Assert.IsType<AutomationResult.Permanent>(result).Code);
        Assert.True(AutomationExecution.IsStableCode(AutomationExecutionPolicy.PermanentPrefix + code));
    }

    // ---- Snapshot content ----

    [Fact]
    public async Task Snapshot_SummarizesTheLocalWeek_FromTheModules()
    {
        _reviews.AddUser(UserA);
        var food = await CategoryAsync(UserA, "Food", CategoryType.Expense);
        var salary = await CategoryAsync(UserA, "Salary", CategoryType.Income);
        var account = Guid.CreateVersion7();
        Expense(UserA, account, food, 12.50m, WeekStartUtc);                      // Monday 00:00 local: in
        Expense(UserA, account, food, 7.50m, WeekEndUtc.AddSeconds(-1));          // Sunday 23:59:59 local: in
        Expense(UserA, account, food, 100m, WeekStartUtc.AddSeconds(-1));         // previous Sunday: out
        Expense(UserA, account, food, 100m, WeekEndUtc);                          // next Monday: out
        Income(UserA, account, salary, 1000m, WeekStartUtc.AddDays(2));
        _transactions.Transactions.Add(Transaction.CreateTransfer(UserA, account, Guid.CreateVersion7(), 50m, "EUR", WeekStartUtc.AddDays(1), null, WeekStartUtc));
        Expense(UserB, account, food, 999m, WeekStartUtc.AddDays(1));             // another user: out

        await CompletedWorkoutAsync(UserA, "Upper", WeekStartUtc.AddDays(1).AddHours(18), TimeSpan.FromMinutes(52), completeSets: 2, sets: 3);
        await CompletedWorkoutAsync(UserA, "Lower", WeekEndUtc.AddHours(-3), TimeSpan.FromMinutes(61), completeSets: 3, sets: 3);
        await CompletedWorkoutAsync(UserA, "Old", WeekStartUtc.AddMinutes(-1), TimeSpan.FromMinutes(30), completeSets: 3, sets: 3);

        var monday = new DateOnly(2026, 9, 28);
        var analyzed = Meal(UserA, monday, "Pasta");
        Analyze(analyzed, 600m, 20m, 90m, 15m);
        Meal(UserA, monday, "Apple");                                               // unanalyzed
        Analyze(Meal(UserA, monday.AddDays(2), "Rice"), 500.5m, 10m, 100m, 5m);
        Meal(UserA, monday.AddDays(7), "Next week");                                // out
        Meal(UserA, monday.AddDays(-1), "Last week");                               // out

        await Tick().RunAsync();

        var snapshot = Assert.Single(_reviews.Reviews).Snapshot;

        var eur = Assert.Single(snapshot.Finance.Currencies);
        Assert.Equal(("EUR", 20.00m, 1000m, 980.00m), (eur.Currency, eur.Expenses, eur.Income, eur.NetFlow));
        Assert.Equal([new WeeklyExpenseCategory("Food", 20.00m)], eur.ExpenseCategories);

        Assert.Equal((2, 52L * 60 + 61 * 60, 5, 6), (snapshot.Gym.CompletedWorkouts, snapshot.Gym.TotalDurationSeconds, snapshot.Gym.CompletedSets, snapshot.Gym.PrescribedSets));
        Assert.Equal(
            [(new DateOnly(2026, 9, 29), "Upper", 52L * 60), (new DateOnly(2026, 10, 4), "Lower", 61L * 60)],
            snapshot.Gym.Workouts.Select(workout => (workout.Date, workout.WorkoutName, workout.DurationSeconds)));

        var nutrition = snapshot.Nutrition;
        Assert.Equal((2, 3, 2, 1), (nutrition.DaysWithMeals, nutrition.MealCount, nutrition.AnalyzedMealCount, nutrition.FullyAnalyzedDays));
        Assert.Equal((1100.5m, 30m, 190m, 20m), (nutrition.AnalyzedCaloriesKcal, nutrition.AnalyzedProteinGrams, nutrition.AnalyzedCarbsGrams, nutrition.AnalyzedFatGrams));
        Assert.Equal(
            [new WeeklyNutritionDay(monday, 2, 1, 600m, 20m, 90m, 15m), new WeeklyNutritionDay(monday.AddDays(2), 1, 1, 500.5m, 10m, 100m, 5m)],
            nutrition.Days);
    }

    [Fact]
    public async Task Snapshot_OfAnEmptyWeek_HasNoInventedValues()
    {
        _reviews.AddUser(UserA);

        await Tick().RunAsync();

        var snapshot = Assert.Single(_reviews.Reviews).Snapshot;

        Assert.Empty(snapshot.Finance.Currencies);
        Assert.Equal((0, 0L, 0, 0), (snapshot.Gym.CompletedWorkouts, snapshot.Gym.TotalDurationSeconds, snapshot.Gym.CompletedSets, snapshot.Gym.PrescribedSets));
        Assert.Empty(snapshot.Gym.Workouts);
        Assert.Equal(EmptySnapshot.Nutrition with { Days = snapshot.Nutrition.Days }, snapshot.Nutrition);
        Assert.Empty(snapshot.Nutrition.Days);
    }

    // Sunday cut-off: the review holds what exists when it is generated (Sunday 20:00); activity
    // logged later on Sunday is never added to the saved review.
    [Fact]
    public async Task ActivityAddedAfterGeneration_DoesNotChangeTheSavedReview()
    {
        _reviews.AddUser(UserA);
        var food = await CategoryAsync(UserA, "Food", CategoryType.Expense);
        var account = Guid.CreateVersion7();
        Expense(UserA, account, food, 10m, Due.AddHours(-1));

        await Tick().RunAsync();
        var saved = Assert.Single(_reviews.Reviews);

        Expense(UserA, account, food, 25m, Due.AddHours(2));                         // Sunday 22:00 local
        _transactions.Transactions[0] = Transaction.CreateExpense(UserA, account, food, 99m, "EUR", Due.AddHours(-1), null, Due); // history edited
        Meal(UserA, WeekEnd, "Late dinner");
        _clock.Advance(TimeSpan.FromHours(3));
        await Tick().RunAsync();

        Assert.Same(saved, Assert.Single(_reviews.Reviews));
        Assert.Equal(10m, Assert.Single(saved.Snapshot.Finance.Currencies).Expenses);
        Assert.Equal(0, saved.Snapshot.Nutrition.MealCount);
    }

    [Fact]
    public async Task Generation_IsReadOnly_TowardsTheModules()
    {
        _reviews.AddUser(UserA);
        var unanalyzed = Meal(UserA, new DateOnly(2026, 9, 30), "Soup");
        await CompletedWorkoutAsync(UserA, "Upper", WeekStartUtc.AddDays(1), TimeSpan.FromMinutes(40), completeSets: 1, sets: 2);
        var transactions = _transactions.Transactions.Count;
        var sessions = _sessions.Sessions.Count;

        await Tick().RunAsync();

        Assert.Single(_reviews.Reviews);
        Assert.Empty(_meals.Snapshots);                      // never estimated, never lazily closed
        Assert.Single(_meals.Entries, entry => entry.Id == unanalyzed.Id);
        Assert.Equal((transactions, sessions), (_transactions.Transactions.Count, _sessions.Sessions.Count));
    }

    // The same data in any read order gives the same snapshot: every list has a fixed order.
    [Fact]
    public void Snapshot_IsDeterministic_WhateverTheReadOrder()
    {
        var early = new WorkoutHistoryItem(Guid.CreateVersion7(), "Base", "Upper", WeekStartUtc.AddHours(10), WeekStartUtc.AddHours(11), 3, 3, 1);
        var late = new WorkoutHistoryItem(Guid.CreateVersion7(), "Base", "Lower", WeekStartUtc.AddDays(3), WeekStartUtc.AddDays(3).AddHours(1), 2, 3, 1);
        var monday = Meal(UserA, new DateOnly(2026, 9, 28), "Pasta");
        var friday = Meal(UserA, new DateOnly(2026, 10, 2), "Rice");
        var meals = new[] { new LifeOS.Application.Nutrition.MealWithNutrition(friday, null), new LifeOS.Application.Nutrition.MealWithNutrition(monday, null) };
        var currencies = new[]
        {
            new LifeOS.Application.Finance.Analytics.CurrencyAnalytics("USD", 5m, 0m, []),
            new LifeOS.Application.Finance.Analytics.CurrencyAnalytics("EUR", 7m, 1m, [])
        };

        var first = WeeklyReviewSnapshotBuilder.Build(Rome, currencies, [late, early], meals);
        var second = WeeklyReviewSnapshotBuilder.Build(Rome, currencies.Reverse().ToList(), [early, late], meals.Reverse().ToList());

        Assert.Equal(["EUR", "USD"], first.Finance.Currencies.Select(currency => currency.Currency));
        Assert.Equal(first.Finance.Currencies.Select(currency => currency with { ExpenseCategories = [] }), second.Finance.Currencies.Select(currency => currency with { ExpenseCategories = [] }));
        Assert.Equal(["Upper", "Lower"], first.Gym.Workouts.Select(workout => workout.WorkoutName));
        Assert.Equal(first.Gym.Workouts, second.Gym.Workouts);
        Assert.Equal([new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 2)], first.Nutrition.Days.Select(day => day.Date));
        Assert.Equal(first.Nutrition.Days, second.Nutrition.Days);
    }

    // ---- Helpers ----

    private static TimeZoneInfo Rome => TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");

    private static readonly WeeklyReviewSnapshot EmptySnapshot = new(
        new WeeklyFinanceSummary([]),
        new WeeklyGymSummary(0, 0, 0, 0, []),
        new WeeklyNutritionSummary(0, 0, 0, 0, 0m, 0m, 0m, 0m, []));

    private WeeklyReviewAutomationHandler Handler() =>
        new(_reviews,
            new WeeklyReviewSnapshotBuilder(new GetMonthlyAnalyticsHandler(_transactions, _categories), new GetWorkoutHistoryHandler(_sessions), _meals),
            _clock);

    private RunAutomationTick Tick() =>
        new([Handler()], _store, new NotificationDispatcher(_deliveries, _devices, _unitOfWork, _clock), _deliveries, _unitOfWork, new AutomationTickGuard(), _clock);

    private AutomationOccurrence Occurrence(string key = "2026-10-04", string zone = "Europe/Rome") =>
        new(Guid.CreateVersion7(), UserA, WeeklyReviewAutomationHandler.Type, key, zone, Due, Due.AddHours(24), 1);

    private Task AddDeviceAsync(Guid userId, string installationId, string token) =>
        _devices.UpsertAsync(DeviceRegistration.Register(userId, installationId, DevicePlatform.Android, token, true, Due.AddDays(-1)), default);

    private async Task<Guid> CategoryAsync(Guid userId, string name, CategoryType type)
    {
        var category = Category.Create(userId, name, type, null, Due.AddDays(-30));
        await _categories.TryAddAsync(category, default);

        return category.Id;
    }

    private void Expense(Guid userId, Guid account, Guid category, decimal amount, DateTimeOffset occurredAtUtc) =>
        _transactions.Transactions.Add(Transaction.CreateExpense(userId, account, category, amount, "EUR", occurredAtUtc, null, occurredAtUtc));

    private void Income(Guid userId, Guid account, Guid category, decimal amount, DateTimeOffset occurredAtUtc) =>
        _transactions.Transactions.Add(Transaction.CreateIncome(userId, account, category, amount, "EUR", occurredAtUtc, null, occurredAtUtc));

    private MealEntry Meal(Guid userId, DateOnly date, string description)
    {
        var meal = MealEntry.Create(userId, description, MealType.Lunch, date, new TimeOnly(12, 30), 120, Due.AddDays(-7));
        _meals.Entries.Add(meal);

        return meal;
    }

    private void Analyze(MealEntry meal, decimal kcal, decimal protein, decimal carbs, decimal fat) =>
        _meals.Snapshots.Add(MealNutritionSnapshot.Create(meal.Id, NutritionValues.Create(kcal, protein, carbs, fat), NutritionSource.AiConfirmed, Due.AddDays(-7)));

    private async Task CompletedWorkoutAsync(Guid userId, string name, DateTimeOffset completedAtUtc, TimeSpan duration, int completeSets, int sets)
    {
        var exercise = _exercises.Add(userId, $"{name} exercise");
        var program = WorkoutProgram.Create(userId, "Base", completedAtUtc.AddDays(-30));
        var workout = program.AddWorkout(name);
        workout.AddBlock(WorkoutBlockKind.Single, 90, [new ExercisePrescription(exercise, null, Enumerable.Repeat(new RepRange(8, 8), sets).ToList())]);

        var startedAtUtc = completedAtUtc - duration;
        var session = WorkoutSession.Start(program, workout, startedAtUtc);

        foreach (var set in session.ExecutionOrder.Take(completeSets))
        {
            session.RecordSet(set.Id, 8, 50m, startedAtUtc.AddMinutes(1));
        }

        session.Finish(completedAtUtc);
        await _sessions.TryAddAsync(session, default);
    }
}
