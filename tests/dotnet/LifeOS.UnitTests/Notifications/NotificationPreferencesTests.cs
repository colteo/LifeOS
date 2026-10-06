using LifeOS.Application.Notifications;
using LifeOS.Domain.Notifications;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Notifications;

// AUTO-003A: per-type reminder preferences and quiet hours. Defaults need no row; the handlers validate
// quiet hours and save per user. PostgreSQL persistence is in FinanceReminderPersistenceTests.
public class NotificationPreferencesTests
{
    private static readonly Guid UserA = Guid.Parse("0192f0c3-0000-7000-8000-0000000000a1");
    private static readonly Guid UserB = Guid.Parse("0192f0c3-0000-7000-8000-0000000000b1");
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryNotificationPreferencesRepository _repository = new();

    [Fact]
    public void Defaults_EnableBothReminders_WithQuietHours22To08()
    {
        var preferences = NotificationPreferences.Default(UserA);

        Assert.Equal((UserA, true, true, new TimeOnly(22, 0), new TimeOnly(8, 0)),
            (preferences.UserId, preferences.RecurringTransactionRemindersEnabled, preferences.PlannedExpenseRemindersEnabled,
                preferences.QuietHoursStart, preferences.QuietHoursEnd));
        Assert.Equal(QuietHours.Default, preferences.QuietHours);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void IsEnabled_FollowsEachReminderFlag_AndNeverMutesOtherTypes(bool recurring, bool planned)
    {
        var preferences = NotificationPreferences.Create(UserA, recurring, planned, QuietHours.Default, Now);

        Assert.Equal(recurring, preferences.IsEnabled(NotificationType.RecurringTransactionReminder));
        Assert.Equal(planned, preferences.IsEnabled(NotificationType.PlannedExpenseReminder));
        Assert.True(preferences.IsEnabled(NotificationType.WeeklyReviewReady));
        Assert.True(preferences.IsEnabled(NotificationType.Test));
    }

    [Fact]
    public async Task Get_WithoutSavedPreferences_IsTheDefaults()
    {
        var preferences = await new GetNotificationPreferencesHandler(_repository).HandleAsync(UserA, default);

        Assert.Equal(new NotificationPreferencesView(true, true, new TimeOnly(22, 0), new TimeOnly(8, 0)), preferences);
    }

    [Fact]
    public async Task Set_ThenGet_RoundTrips_EachTypeIndependently()
    {
        Assert.Equal(SetNotificationPreferencesResult.Saved, await Set(UserA, recurring: false, planned: true, new TimeOnly(23, 30), new TimeOnly(7, 15)));

        Assert.Equal(new NotificationPreferencesView(false, true, new TimeOnly(23, 30), new TimeOnly(7, 15)),
            await new GetNotificationPreferencesHandler(_repository).HandleAsync(UserA, default));
        Assert.Equal(Now, _repository.Saved[UserA].UpdatedAtUtc);

        Assert.Equal(SetNotificationPreferencesResult.Saved, await Set(UserA, recurring: true, planned: false, new TimeOnly(23, 30), new TimeOnly(7, 15)));
        Assert.Equal(new NotificationPreferencesView(true, false, new TimeOnly(23, 30), new TimeOnly(7, 15)),
            await new GetNotificationPreferencesHandler(_repository).HandleAsync(UserA, default));
    }

    [Fact]
    public async Task Set_IsPerUser()
    {
        await Set(UserA, recurring: false, planned: false, new TimeOnly(20, 0), new TimeOnly(9, 0));

        Assert.Equal(new NotificationPreferencesView(true, true, new TimeOnly(22, 0), new TimeOnly(8, 0)),
            await new GetNotificationPreferencesHandler(_repository).HandleAsync(UserB, default));
    }

    [Fact]
    public async Task Set_RejectsEqualQuietHours_AndSavesNothing()
    {
        Assert.Equal(SetNotificationPreferencesResult.InvalidQuietHours, await Set(UserA, true, true, new TimeOnly(22, 0), new TimeOnly(22, 0)));
        Assert.Equal(SetNotificationPreferencesResult.InvalidQuietHours, await Set(UserA, true, true, new TimeOnly(22, 0, 15), new TimeOnly(8, 0)));
        Assert.Empty(_repository.Saved);
    }

    [Fact]
    public async Task Set_ForAMissingUser_IsNotFound()
    {
        _repository.ExistingUsers = [UserA];

        Assert.Equal(SetNotificationPreferencesResult.NotFound, await Set(UserB, true, true, new TimeOnly(22, 0), new TimeOnly(8, 0)));
    }

    private Task<SetNotificationPreferencesResult> Set(Guid userId, bool recurring, bool planned, TimeOnly start, TimeOnly end) =>
        new SetNotificationPreferencesHandler(_repository, new FixedTimeProvider(Now)).HandleAsync(userId, recurring, planned, start, end, default);
}
