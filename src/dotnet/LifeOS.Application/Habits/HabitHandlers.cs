using LifeOS.Application.Nutrition.Hydration;
using LifeOS.Domain.Habits;

namespace LifeOS.Application.Habits;

// HAB-001 §7: Habit Tracker v1 write use cases. Every write that depends on "today" carries the device's
// UTC offset; today = (UTC now + offset).Date, offset within ±14 h. Nothing here calls AI or stores
// derived statistics. The only other module read is Hydration, through IHydrationDayReader, for the
// water habit (B4): its days are derived on every read and no water amount is ever stored here.

public sealed record CreateHabitCommand(string Name, string? Description, HabitCompletionSource CompletionSource,
    HabitFrequencyInput Frequency, DateOnly? StartsOn, int UtcOffsetMinutes);

// Frequency null: unchanged. A frequency change applies from today.
public sealed record UpdateHabitCommand(string Name, string? Description, HabitFrequencyInput? Frequency, int UtcOffsetMinutes);

public sealed record SetHabitCheckInCommand(DateOnly Date, HabitCheckInStatus Status, string? Note, int UtcOffsetMinutes);

public static class HabitLimits
{
    // Abuse guard (HAB-001 §6.7).
    public const int MaxActiveHabits = 100;

    public const int MaxUtcOffsetMinutes = 14 * 60;

    public const string LimitReachedCode = "habit_limit_reached";

    // Throws ArgumentOutOfRangeException("utcOffsetMinutes") outside ±14 h.
    public static DateOnly LocalToday(TimeProvider clock, int utcOffsetMinutes)
    {
        if (utcOffsetMinutes is < -MaxUtcOffsetMinutes or > MaxUtcOffsetMinutes)
        {
            throw new ArgumentOutOfRangeException("utcOffsetMinutes", "UTC offset must be between -840 and 840 minutes.");
        }

        return DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromMinutes(utcOffsetMinutes)).DateTime);
    }
}

// A Hydration habit ("Reach my water target", HAB-US11) needs a current Hydration target (409
// hydration_target_required) and is the user's only active one (409 hydration_habit_exists).
public sealed class CreateHabitHandler(IHabitRepository repository, IHydrationDayReader hydration, TimeProvider clock)
{
    public async Task<HabitResult<HabitDetail>> HandleAsync(Guid userId, CreateHabitCommand command, CancellationToken cancellationToken)
    {
        Habit habit;
        DateOnly today;

        try
        {
            today = HabitLimits.LocalToday(clock, command.UtcOffsetMinutes);
            habit = Habit.Create(userId, command.Name ?? "", command.Description, command.CompletionSource,
                command.Frequency.ToFrequency(), command.StartsOn ?? today, today, clock.GetUtcNow());
        }
        catch (ArgumentException exception)
        {
            return HabitResult<HabitDetail>.Invalid(exception);
        }

        if (await repository.CountActiveAsync(userId, cancellationToken) >= HabitLimits.MaxActiveHabits)
        {
            return HabitResult<HabitDetail>.Conflict(HabitLimits.LimitReachedCode,
                $"You can have at most {HabitLimits.MaxActiveHabits} active habits. Archive one first.");
        }

        if (habit.CompletionSource == HabitCompletionSource.Hydration)
        {
            if (await repository.HasActiveHydrationHabitAsync(userId, null, cancellationToken))
            {
                return HydrationHabitExists<HabitDetail>();
            }

            if ((await hydration.GetDaysAsync(userId, today, today, cancellationToken)).Single().TargetMl is null)
            {
                return HabitResult<HabitDetail>.Conflict(HabitConflictException.HydrationTargetRequired,
                    "Set a daily water target first (Nutrition → Targets).");
            }
        }

        try
        {
            await repository.AddAsync(habit, cancellationToken);
        }
        catch (HabitConflictException exception)
        {
            return HabitResult<HabitDetail>.Conflict(exception);
        }

        return HabitResult<HabitDetail>.Ok(await HabitReads.DetailAsync(repository, hydration, habit, today, HabitReads.DefaultWindowDays,
            cancellationToken));
    }

    internal static HabitResult<T> HydrationHabitExists<T>() =>
        HabitResult<T>.Conflict(HabitConflictException.HydrationHabitExists, "You already have a water habit. Archive it first.");
}

// Name and description; optionally the frequency, from today (409 habit_archived while archived).
public sealed class UpdateHabitHandler(IHabitRepository repository, IHydrationDayReader hydration, TimeProvider clock)
{
    public async Task<HabitResult<HabitDetail>> HandleAsync(Guid userId, Guid habitId, UpdateHabitCommand command, CancellationToken cancellationToken)
    {
        DateOnly today;

        try
        {
            today = HabitLimits.LocalToday(clock, command.UtcOffsetMinutes);
        }
        catch (ArgumentException exception)
        {
            return HabitResult<HabitDetail>.Invalid(exception);
        }

        if (await repository.GetAsync(userId, habitId, cancellationToken) is not { } habit)
        {
            return HabitResult<HabitDetail>.NotFound();
        }

        try
        {
            var frequency = command.Frequency?.ToFrequency();
            var now = clock.GetUtcNow();

            habit.UpdateDetails(command.Name ?? "", command.Description, now);

            if (frequency is not null)
            {
                habit.ChangeFrequency(frequency, today, now);
            }
        }
        catch (ArgumentException exception)
        {
            return HabitResult<HabitDetail>.Invalid(exception);
        }
        catch (HabitConflictException exception)
        {
            return HabitResult<HabitDetail>.Conflict(exception);
        }

        return await repository.SaveAsync(habit, cancellationToken)
            ? HabitResult<HabitDetail>.Ok(await HabitReads.DetailAsync(repository, hydration, habit, today, HabitReads.DefaultWindowDays,
                cancellationToken))
            : HabitResult<HabitDetail>.NotFound();
    }
}

// Archive and restore are versioned from today and idempotent. Restoring a Hydration habit while another
// one is active is a 409 hydration_habit_exists.
public sealed class ArchiveHabitHandler(IHabitRepository repository, IHydrationDayReader hydration, TimeProvider clock)
{
    public Task<HabitResult<HabitDetail>> ArchiveAsync(Guid userId, Guid habitId, int utcOffsetMinutes, CancellationToken cancellationToken) =>
        ChangeAsync(userId, habitId, utcOffsetMinutes, restoring: false, (habit, today, now) => habit.Archive(today, now), cancellationToken);

    // Back to the habit's last frequency.
    public Task<HabitResult<HabitDetail>> RestoreAsync(Guid userId, Guid habitId, int utcOffsetMinutes, CancellationToken cancellationToken) =>
        ChangeAsync(userId, habitId, utcOffsetMinutes, restoring: true, (habit, today, now) => habit.Restore(today, null, now), cancellationToken);

    private async Task<HabitResult<HabitDetail>> ChangeAsync(Guid userId, Guid habitId, int utcOffsetMinutes, bool restoring,
        Action<Habit, DateOnly, DateTimeOffset> change, CancellationToken cancellationToken)
    {
        DateOnly today;

        try
        {
            today = HabitLimits.LocalToday(clock, utcOffsetMinutes);
        }
        catch (ArgumentException exception)
        {
            return HabitResult<HabitDetail>.Invalid(exception);
        }

        if (await repository.GetAsync(userId, habitId, cancellationToken) is not { } habit)
        {
            return HabitResult<HabitDetail>.NotFound();
        }

        if (restoring && habit is { Status: HabitStatus.Archived, CompletionSource: HabitCompletionSource.Hydration }
            && await repository.HasActiveHydrationHabitAsync(userId, habit.Id, cancellationToken))
        {
            return CreateHabitHandler.HydrationHabitExists<HabitDetail>();
        }

        change(habit, today, clock.GetUtcNow());

        bool saved;

        try
        {
            saved = await repository.SaveAsync(habit, cancellationToken);
        }
        catch (HabitConflictException exception)
        {
            return HabitResult<HabitDetail>.Conflict(exception);
        }

        return saved
            ? HabitResult<HabitDetail>.Ok(await HabitReads.DetailAsync(repository, hydration, habit, today, HabitReads.DefaultWindowDays,
                cancellationToken))
            : HabitResult<HabitDetail>.NotFound();
    }
}

// Destructive: the habit, its schedule and its check-ins (the App asks for confirmation first).
public sealed class DeleteHabitHandler(IHabitRepository repository)
{
    public async Task<HabitResultStatus> HandleAsync(Guid userId, Guid habitId, CancellationToken cancellationToken) =>
        await repository.DeleteAsync(userId, habitId, cancellationToken) ? HabitResultStatus.Ok : HabitResultStatus.NotFound;
}

// Done or Excused for a local date: an idempotent upsert (HAB-001 §5.1 rules 7-11).
// A Hydration habit accepts Excused only (409 habit_completion_derived for Done).
public sealed class SetHabitCheckInHandler(IHabitRepository repository, IHydrationDayReader hydration, TimeProvider clock)
{
    public async Task<HabitResult<HabitDayResult>> HandleAsync(Guid userId, Guid habitId, SetHabitCheckInCommand command,
        CancellationToken cancellationToken)
    {
        DateOnly today;

        try
        {
            today = HabitLimits.LocalToday(clock, command.UtcOffsetMinutes);
        }
        catch (ArgumentException exception)
        {
            return HabitResult<HabitDayResult>.Invalid(exception);
        }

        if (await repository.GetAsync(userId, habitId, cancellationToken) is not { } habit)
        {
            return HabitResult<HabitDayResult>.NotFound();
        }

        HabitCheckIn checkIn;

        try
        {
            checkIn = habit.CheckIn(command.Date, command.Status, command.Note, today, clock.GetUtcNow());
        }
        catch (ArgumentException exception)
        {
            return HabitResult<HabitDayResult>.Invalid(exception);
        }
        catch (HabitConflictException exception)
        {
            return HabitResult<HabitDayResult>.Conflict(exception);
        }

        return await repository.UpsertCheckInAsync(checkIn, cancellationToken)
            ? HabitResult<HabitDayResult>.Ok(await HabitReads.DayAsync(repository, hydration, habit, command.Date, today, cancellationToken))
            : HabitResult<HabitDayResult>.NotFound();
    }
}

// Undo: removes the stored status; the day returns to Pending (today) or Missed. Idempotent.
public sealed class RemoveHabitCheckInHandler(IHabitRepository repository, IHydrationDayReader hydration, TimeProvider clock)
{
    public async Task<HabitResult<HabitDayResult>> HandleAsync(Guid userId, Guid habitId, DateOnly date, int utcOffsetMinutes,
        CancellationToken cancellationToken)
    {
        DateOnly today;

        try
        {
            today = HabitLimits.LocalToday(clock, utcOffsetMinutes);

            if (date > today)
            {
                throw new ArgumentOutOfRangeException(nameof(date), "You can't change a future date.");
            }
        }
        catch (ArgumentException exception)
        {
            return HabitResult<HabitDayResult>.Invalid(exception);
        }

        if (await repository.GetAsync(userId, habitId, cancellationToken) is not { } habit)
        {
            return HabitResult<HabitDayResult>.NotFound();
        }

        await repository.RemoveCheckInAsync(userId, habitId, date, cancellationToken);

        return HabitResult<HabitDayResult>.Ok(await HabitReads.DayAsync(repository, hydration, habit, date, today, cancellationToken));
    }
}
