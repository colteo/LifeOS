using LifeOS.Application.Notifications;
using LifeOS.Domain.Notifications;
using LifeOS.Domain.Reminders;

namespace LifeOS.Application.Reminders;

// REM-001 §8: the user's own reminders (Custom). Every write that depends on "now" carries the device's
// UTC offset: localNow = UTC now + offset, within ±14 h. The automation later resolves occurrences in
// the user's IANA zone (normally the same wall clock: the app syncs its zone after sign-in).

public sealed record CreateReminderCommand(string Title, string? Note, ReminderScheduleInput Schedule, int UtcOffsetMinutes);

public sealed record UpdateReminderCommand(string Title, string? Note, ReminderScheduleInput Schedule, int UtcOffsetMinutes);

public sealed class CreateReminderHandler(IReminderRepository repository, INotificationPreferencesRepository preferences, TimeProvider clock)
{
    public async Task<ReminderResult<ReminderView>> HandleAsync(Guid userId, CreateReminderCommand command, CancellationToken cancellationToken)
    {
        Reminder reminder;

        try
        {
            var localNow = ReminderLimits.LocalNow(clock, command.UtcOffsetMinutes);
            reminder = Reminder.Create(userId, command.Title ?? "", command.Note, command.Schedule.ToSchedule(), localNow, clock.GetUtcNow());
        }
        catch (ArgumentException exception)
        {
            return ReminderResult<ReminderView>.Invalid(exception);
        }

        if (await repository.CountOpenAsync(userId, cancellationToken) >= ReminderLimits.MaxOpenReminders)
        {
            return LimitReached();
        }

        await repository.AddAsync(reminder, cancellationToken);

        return ReminderResult<ReminderView>.Ok(await ReminderReads.ViewAsync(preferences, reminder, cancellationToken));
    }

    internal static ReminderResult<ReminderView> LimitReached() =>
        ReminderResult<ReminderView>.Conflict(ReminderLimits.LimitReachedCode,
            $"You can have at most {ReminderLimits.MaxOpenReminders} reminders. Delete one first.");
}

// Title, note and schedule; the next occurrence is recomputed from now (REM-US5).
public sealed class UpdateReminderHandler(IReminderRepository repository, INotificationPreferencesRepository preferences, TimeProvider clock)
{
    public async Task<ReminderResult<ReminderView>> HandleAsync(Guid userId, Guid reminderId, UpdateReminderCommand command,
        CancellationToken cancellationToken)
    {
        DateTime localNow;
        ReminderSchedule schedule;

        try
        {
            localNow = ReminderLimits.LocalNow(clock, command.UtcOffsetMinutes);
            schedule = command.Schedule.ToSchedule();
        }
        catch (ArgumentException exception)
        {
            return ReminderResult<ReminderView>.Invalid(exception);
        }

        if (await repository.GetAsync(userId, reminderId, cancellationToken) is not { } reminder)
        {
            return ReminderResult<ReminderView>.NotFound();
        }

        // Editing a completed reminder opens it again: it counts towards the limit.
        if (reminder.Status == ReminderStatus.Completed
            && await repository.CountOpenAsync(userId, cancellationToken) >= ReminderLimits.MaxOpenReminders)
        {
            return CreateReminderHandler.LimitReached();
        }

        try
        {
            reminder.Update(command.Title ?? "", command.Note, schedule, localNow, clock.GetUtcNow());
        }
        catch (ArgumentException exception)
        {
            return ReminderResult<ReminderView>.Invalid(exception);
        }

        return await repository.SaveAsync(reminder, cancellationToken)
            ? ReminderResult<ReminderView>.Ok(await ReminderReads.ViewAsync(preferences, reminder, cancellationToken))
            : ReminderResult<ReminderView>.NotFound();
    }
}

// REM-US4: pause stops pushes (and drops a pending snooze); resume continues from the next occurrence
// after now, without catching up. Both are idempotent; a completed reminder is a 409.
public sealed class PauseReminderHandler(IReminderRepository repository, INotificationPreferencesRepository preferences, TimeProvider clock)
{
    public Task<ReminderResult<ReminderView>> PauseAsync(Guid userId, Guid reminderId, CancellationToken cancellationToken) =>
        ReminderChange.ApplyAsync(repository, preferences, userId, reminderId, reminder => reminder.Pause(clock.GetUtcNow()), cancellationToken);

    public Task<ReminderResult<ReminderView>> ResumeAsync(Guid userId, Guid reminderId, int utcOffsetMinutes, CancellationToken cancellationToken)
    {
        DateTime localNow;

        try
        {
            localNow = ReminderLimits.LocalNow(clock, utcOffsetMinutes);
        }
        catch (ArgumentException exception)
        {
            return Task.FromResult(ReminderResult<ReminderView>.Invalid(exception));
        }

        return ReminderChange.ApplyAsync(repository, preferences, userId, reminderId, reminder => reminder.Resume(localNow, clock.GetUtcNow()),
            cancellationToken);
    }
}

// REM-US7 / D-11: fire again in 15, 60 or 180 minutes (409 reminder_paused while paused).
public sealed class SnoozeReminderHandler(IReminderRepository repository, INotificationPreferencesRepository preferences, TimeProvider clock)
{
    public Task<ReminderResult<ReminderView>> HandleAsync(Guid userId, Guid reminderId, int minutes, CancellationToken cancellationToken)
    {
        if (!Reminder.SnoozeMinutes.Contains(minutes))
        {
            return Task.FromResult(ReminderResult<ReminderView>.Invalid("minutes", "Snooze for 15 minutes, 1 hour or 3 hours."));
        }

        return ReminderChange.ApplyAsync(repository, preferences, userId, reminderId, reminder => reminder.Snooze(minutes, clock.GetUtcNow()),
            cancellationToken);
    }
}

// REM-US6: a hard delete (the app asks for confirmation first). A delivery already enqueued may still
// arrive; its tap opens "Reminder not found".
public sealed class DeleteReminderHandler(IReminderRepository repository)
{
    public async Task<ReminderResultStatus> HandleAsync(Guid userId, Guid reminderId, CancellationToken cancellationToken) =>
        await repository.DeleteAsync(userId, reminderId, cancellationToken) ? ReminderResultStatus.Ok : ReminderResultStatus.NotFound;
}

public sealed class GetReminderHandler(IReminderRepository repository, INotificationPreferencesRepository preferences)
{
    public async Task<ReminderResult<ReminderView>> HandleAsync(Guid userId, Guid reminderId, CancellationToken cancellationToken) =>
        await repository.GetAsync(userId, reminderId, cancellationToken) is { } reminder
            ? ReminderResult<ReminderView>.Ok(await ReminderReads.ViewAsync(preferences, reminder, cancellationToken))
            : ReminderResult<ReminderView>.NotFound();
}

// The Reminders screen. PushAvailable is whether a push sender is registered (FCM configured).
public sealed class GetRemindersHandler(
    IReminderRepository repository,
    INotificationPreferencesRepository preferences,
    TimeProvider clock,
    IPushNotificationSender? sender = null)
{
    public async Task<RemindersOverview> HandleAsync(Guid userId, CancellationToken cancellationToken)
    {
        var context = await preferences.GetQuietHoursContextAsync(userId, cancellationToken);
        var quietHours = context?.QuietHours ?? QuietHours.Default;
        var reminders = await repository.GetAllAsync(userId, clock.GetUtcNow() - ReminderLimits.CompletedListed, cancellationToken);

        List<ReminderView> Views(ReminderStatus status) =>
            reminders.Where(reminder => reminder.Status == status).Select(reminder => new ReminderView(reminder, quietHours.Contains(reminder.LocalTime))).ToList();

        return new RemindersOverview(
            context?.TimeZoneId is not null,
            sender is not null,
            Views(ReminderStatus.Active).OrderBy(view => view.Reminder.NextDueLocal).ThenBy(view => view.Reminder.Id).ToList(),
            Views(ReminderStatus.Paused).OrderBy(view => view.Reminder.CreatedAtUtc).ThenBy(view => view.Reminder.Id).ToList(),
            Views(ReminderStatus.Completed).OrderByDescending(view => view.Reminder.UpdatedAtUtc).ThenBy(view => view.Reminder.Id).ToList());
    }
}

internal static class ReminderReads
{
    public static async Task<ReminderView> ViewAsync(INotificationPreferencesRepository preferences, Reminder reminder, CancellationToken cancellationToken)
    {
        var quietHours = (await preferences.GetQuietHoursContextAsync(reminder.UserId, cancellationToken))?.QuietHours
            ?? QuietHours.Default;

        return new ReminderView(reminder, quietHours.Contains(reminder.LocalTime));
    }
}

internal static class ReminderChange
{
    public static async Task<ReminderResult<ReminderView>> ApplyAsync(IReminderRepository repository, INotificationPreferencesRepository preferences,
        Guid userId, Guid reminderId, Action<Reminder> change, CancellationToken cancellationToken)
    {
        if (await repository.GetAsync(userId, reminderId, cancellationToken) is not { } reminder)
        {
            return ReminderResult<ReminderView>.NotFound();
        }

        try
        {
            change(reminder);
        }
        catch (ArgumentException exception)
        {
            return ReminderResult<ReminderView>.Invalid(exception);
        }
        catch (ReminderConflictException exception)
        {
            return ReminderResult<ReminderView>.Conflict(exception);
        }

        return await repository.SaveAsync(reminder, cancellationToken)
            ? ReminderResult<ReminderView>.Ok(await ReminderReads.ViewAsync(preferences, reminder, cancellationToken))
            : ReminderResult<ReminderView>.NotFound();
    }
}
