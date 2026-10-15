using LifeOS.Domain.Nutrition.Hydration;

namespace LifeOS.Application.Nutrition.Hydration;

// HYD-001 §7: Hydration v1 use cases. Plain handlers with an explicit user id (ADR-006) and TimeProvider
// for "now". Every write that depends on "today" carries the device's UTC offset; today = (UTC now +
// offset).Date. Nothing here calls AI or reads another module, and no total is ever stored.

public sealed record AddHydrationIntakeCommand(int AmountMl, DateOnly Date, TimeOnly Time, int UtcOffsetMinutes);

// The intake stays on its diary day.
public sealed record UpdateHydrationIntakeCommand(int AmountMl, TimeOnly Time);

public enum HydrationResultStatus
{
    Ok,
    Invalid,
    NotFound
}

// The outcome of a hydration use case. Invalid carries the request field and a readable message.
public sealed record HydrationResult<T>(HydrationResultStatus Status, T? Value = default, string? Field = null, string? Message = null)
{
    public static HydrationResult<T> Ok(T value) => new(HydrationResultStatus.Ok, value);

    public static HydrationResult<T> NotFound() => new(HydrationResultStatus.NotFound);

    public static HydrationResult<T> Invalid(string field, string message) => new(HydrationResultStatus.Invalid, Field: field, Message: message);

    // Domain validation failures name the offending parameter; it is also the request field name.
    public static HydrationResult<T> Invalid(ArgumentException exception) =>
        Invalid(exception.ParamName ?? "request", HydrationLimits.Message(exception));
}

// An intake after a change, with its day's updated total (the client refreshes without another request).
public sealed record HydrationIntakeMutation(HydrationIntake Intake, HydrationDay Day);

// One diary day: its total and target, and its intakes newest first.
public sealed record HydrationDayDetails(HydrationDay Day, IReadOnlyList<HydrationIntake> Intakes);

// Days met / with a target, and the mean total over ALL days of the range (empty days count as 0),
// rounded half away from zero.
public sealed record HydrationRangeSummary(int DaysWithTarget, int DaysMet, int AverageTotalMl);

public sealed record HydrationRange(IReadOnlyList<HydrationDay> Days, HydrationRangeSummary Summary);

public static class HydrationLimits
{
    // The longest history one request may read.
    public const int MaxRangeDays = 92;

    public const string FutureDayMessage = "Water can't be logged for a future day.";

    // Throws ArgumentOutOfRangeException("utcOffsetMinutes") outside ±14 h.
    public static DateOnly LocalToday(TimeProvider clock, int utcOffsetMinutes)
    {
        if (utcOffsetMinutes is < -HydrationIntake.MaxUtcOffsetMinutes or > HydrationIntake.MaxUtcOffsetMinutes)
        {
            throw new ArgumentOutOfRangeException("utcOffsetMinutes", "UTC offset must be between -840 and 840 minutes.");
        }

        return DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromMinutes(utcOffsetMinutes)).DateTime);
    }

    public static bool IsCalendarDate(DateOnly date) => date != DateOnly.MinValue && date != DateOnly.MaxValue;

    public const string CalendarDateMessage = "Choose a calendar date between 0001-01-02 and 9999-12-30.";

    // ArgumentException appends " (Parameter '...')" to the message.
    internal static string Message(ArgumentException exception)
    {
        var index = exception.Message.IndexOf(" (Parameter '", StringComparison.Ordinal);

        return index < 0 ? exception.Message : exception.Message[..index];
    }
}

// Logs one drink on a diary day up to the caller's local today; past days are corrections and allowed.
public sealed class AddHydrationIntakeHandler(IHydrationRepository repository, HydrationDayReader days, TimeProvider clock)
{
    public async Task<HydrationResult<HydrationIntakeMutation>> HandleAsync(Guid userId, AddHydrationIntakeCommand command,
        CancellationToken cancellationToken)
    {
        HydrationIntake intake;

        try
        {
            var today = HydrationLimits.LocalToday(clock, command.UtcOffsetMinutes);

            if (command.Date > today)
            {
                return HydrationResult<HydrationIntakeMutation>.Invalid("date", HydrationLimits.FutureDayMessage);
            }

            intake = HydrationIntake.Create(userId, command.AmountMl, command.Date, command.Time, command.UtcOffsetMinutes, clock.GetUtcNow());
        }
        catch (ArgumentException exception)
        {
            return HydrationResult<HydrationIntakeMutation>.Invalid(exception);
        }

        await repository.AddAsync(intake, cancellationToken);

        return HydrationResult<HydrationIntakeMutation>.Ok(new(intake, await days.GetDayAsync(userId, intake.DiaryDate, cancellationToken)));
    }
}

// Amount and time of the caller's own intake; it never moves to another day (P-8).
public sealed class UpdateHydrationIntakeHandler(IHydrationRepository repository, HydrationDayReader days, TimeProvider clock)
{
    public async Task<HydrationResult<HydrationIntakeMutation>> HandleAsync(Guid userId, Guid intakeId, UpdateHydrationIntakeCommand command,
        CancellationToken cancellationToken)
    {
        if (await repository.GetAsync(userId, intakeId, cancellationToken) is not { } intake)
        {
            return HydrationResult<HydrationIntakeMutation>.NotFound();
        }

        try
        {
            intake.Update(command.AmountMl, command.Time, clock.GetUtcNow());
        }
        catch (ArgumentException exception)
        {
            return HydrationResult<HydrationIntakeMutation>.Invalid(exception);
        }

        if (!await repository.SaveAsync(intake, cancellationToken))
        {
            return HydrationResult<HydrationIntakeMutation>.NotFound();
        }

        return HydrationResult<HydrationIntakeMutation>.Ok(new(intake, await days.GetDayAsync(userId, intake.DiaryDate, cancellationToken)));
    }
}

// A real delete (also Undo, P-7); returns the intake's day as it is afterwards.
public sealed class DeleteHydrationIntakeHandler(IHydrationRepository repository, HydrationDayReader days)
{
    public async Task<HydrationResult<HydrationDay>> HandleAsync(Guid userId, Guid intakeId, CancellationToken cancellationToken)
    {
        if (await repository.GetAsync(userId, intakeId, cancellationToken) is not { } intake
            || !await repository.DeleteAsync(userId, intakeId, cancellationToken))
        {
            return HydrationResult<HydrationDay>.NotFound();
        }

        return HydrationResult<HydrationDay>.Ok(await days.GetDayAsync(userId, intake.DiaryDate, cancellationToken));
    }
}

// One diary day with its intakes, newest first. The order is the diary's rule, so it is applied here as
// well as by the repository's index-backed query.
public sealed class GetHydrationDayHandler(IHydrationRepository repository, HydrationDayReader days)
{
    public async Task<HydrationResult<HydrationDayDetails>> HandleAsync(Guid userId, DateOnly date, CancellationToken cancellationToken)
    {
        if (!HydrationLimits.IsCalendarDate(date))
        {
            return HydrationResult<HydrationDayDetails>.Invalid("date", HydrationLimits.CalendarDateMessage);
        }

        var intakes = await repository.GetForDateAsync(userId, date, cancellationToken);
        var day = await days.GetDayAsync(userId, date, cancellationToken);

        return HydrationResult<HydrationDayDetails>.Ok(new(day, intakes
            .OrderByDescending(intake => intake.DiaryTime)
            .ThenByDescending(intake => intake.Id)
            .ToList()));
    }
}

// Every date of [from, to] (at most 92), zeros for empty days, with the range summary.
public sealed class GetHydrationDaysHandler(IHydrationDayReader days)
{
    public async Task<HydrationResult<HydrationRange>> HandleAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        if (!HydrationLimits.IsCalendarDate(from))
        {
            return HydrationResult<HydrationRange>.Invalid("from", HydrationLimits.CalendarDateMessage);
        }

        if (!HydrationLimits.IsCalendarDate(to))
        {
            return HydrationResult<HydrationRange>.Invalid("to", HydrationLimits.CalendarDateMessage);
        }

        if (to < from)
        {
            return HydrationResult<HydrationRange>.Invalid("to", "The last date must not be before the first.");
        }

        if (to.DayNumber - from.DayNumber + 1 > HydrationLimits.MaxRangeDays)
        {
            return HydrationResult<HydrationRange>.Invalid("to", $"Choose at most {HydrationLimits.MaxRangeDays} days.");
        }

        var range = await days.GetDaysAsync(userId, from, to, cancellationToken);

        return HydrationResult<HydrationRange>.Ok(new(range, Summarize(range)));
    }

    public static HydrationRangeSummary Summarize(IReadOnlyList<HydrationDay> days)
    {
        var total = days.Sum(day => (long)day.TotalMl);
        var average = days.Count == 0 ? 0 : (int)Math.Round((decimal)total / days.Count, MidpointRounding.AwayFromZero);

        return new(days.Count(day => day.TargetMl is not null), days.Count(day => day.IsTargetMet), average);
    }
}

// ---- H2: daily target and quick-add amounts ----

// The target on Date (null: none), the version it comes from, and the three quick-add amounts.
public sealed record HydrationSettingsView(DateOnly Date, int? TargetMl, DateOnly? TargetEffectiveFrom, IReadOnlyList<int> QuickAddMl);

internal static class HydrationSettingsReads
{
    public static async Task<HydrationSettingsView> ViewAsync(IHydrationRepository repository, Guid userId, DateOnly date,
        CancellationToken cancellationToken)
    {
        var version = new HydrationTargetSchedule(await repository.GetTargetVersionsAsync(userId, date, cancellationToken)).VersionOn(date);
        var settings = await repository.GetSettingsAsync(userId, cancellationToken);

        return new(date, version?.TargetMl, version?.EffectiveFrom, settings.QuickAddMl);
    }
}

public sealed class GetHydrationSettingsHandler(IHydrationRepository repository)
{
    public async Task<HydrationResult<HydrationSettingsView>> HandleAsync(Guid userId, DateOnly date, CancellationToken cancellationToken) =>
        HydrationLimits.IsCalendarDate(date)
            ? HydrationResult<HydrationSettingsView>.Ok(await HydrationSettingsReads.ViewAsync(repository, userId, date, cancellationToken))
            : HydrationResult<HydrationSettingsView>.Invalid("date", HydrationLimits.CalendarDateMessage);
}

// Sets, changes or removes (null) the daily target from the caller's local today (P-4): the client can
// choose neither a past date (no retroactive rewrite) nor a future one. Past days keep their target.
public sealed class SetHydrationTargetHandler(IHydrationRepository repository, TimeProvider clock)
{
    public async Task<HydrationResult<HydrationSettingsView>> HandleAsync(Guid userId, int? targetMl, int utcOffsetMinutes,
        CancellationToken cancellationToken)
    {
        DateOnly today;
        HydrationTargetVersion version;

        try
        {
            today = HydrationLimits.LocalToday(clock, utcOffsetMinutes);
            version = HydrationTargetVersion.Create(userId, today, targetMl, clock.GetUtcNow());
        }
        catch (ArgumentException exception)
        {
            return HydrationResult<HydrationSettingsView>.Invalid(exception);
        }

        await repository.UpsertTargetVersionAsync(version, cancellationToken);

        return HydrationResult<HydrationSettingsView>.Ok(await HydrationSettingsReads.ViewAsync(repository, userId, today, cancellationToken));
    }
}

// Replaces the three quick-add amounts; the result shows the target on the caller's local today.
public sealed class SetHydrationQuickAddHandler(IHydrationRepository repository, TimeProvider clock)
{
    public async Task<HydrationResult<HydrationSettingsView>> HandleAsync(Guid userId, IReadOnlyList<int> amountsMl, int utcOffsetMinutes,
        CancellationToken cancellationToken)
    {
        DateOnly today;
        HydrationSettings settings;

        try
        {
            today = HydrationLimits.LocalToday(clock, utcOffsetMinutes);
            settings = HydrationSettings.Create(userId, amountsMl, clock.GetUtcNow());
        }
        catch (ArgumentException exception)
        {
            return HydrationResult<HydrationSettingsView>.Invalid(exception);
        }

        await repository.UpsertSettingsAsync(settings, cancellationToken);

        return HydrationResult<HydrationSettingsView>.Ok(await HydrationSettingsReads.ViewAsync(repository, userId, today, cancellationToken));
    }
}
