using LifeOS.Api.Authentication;
using LifeOS.Application.Habits;
using LifeOS.Contracts.Habits;
using LifeOS.Domain.Habits;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Habits;

// HAB-001 §8: Habit Tracker v1. Transport only. The owner comes only from the access token; another
// user's habit is a 404, exactly like a missing one. Validation failures are field-keyed 400s; business
// conflicts are 409 with a stable "code".
public static class HabitEndpoints
{
    public static IEndpointRouteBuilder MapHabitEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var habits = endpoints.MapGroup("/api/habits")
            .RequireAuthorization();

        habits.MapGet("/", GetForDayAsync).WithName("GetHabitsForDay");
        habits.MapGet("/archived", GetArchivedAsync).WithName("GetArchivedHabits");
        habits.MapGet("/{id:guid}", GetAsync).WithName("GetHabit");
        habits.MapGet("/{id:guid}/history", GetHistoryAsync).WithName("GetHabitHistory");
        habits.MapPost("/", CreateAsync).WithName("CreateHabit");
        habits.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateHabit");
        habits.MapPost("/{id:guid}/archive", ArchiveAsync).WithName("ArchiveHabit");
        habits.MapPost("/{id:guid}/restore", RestoreAsync).WithName("RestoreHabit");
        habits.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteHabit");
        habits.MapPut("/{id:guid}/check-ins/{date}", SetCheckInAsync).WithName("SetHabitCheckIn");
        habits.MapDelete("/{id:guid}/check-ins/{date}", RemoveCheckInAsync).WithName("RemoveHabitCheckIn");

        return endpoints;
    }

    // date: the viewed local date (default today), not after today.
    public static async Task<Results<Ok<HabitsForDayResponse>, ValidationProblem>> GetForDayAsync(
        AuthenticatedUser user,
        GetHabitsForDayHandler handler,
        TimeProvider clock,
        CancellationToken cancellationToken,
        DateOnly? date = null,
        int utcOffsetMinutes = 0)
    {
        DateOnly day;

        try
        {
            day = date ?? HabitLimits.LocalToday(clock, utcOffsetMinutes);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Invalid("utcOffsetMinutes", "UTC offset must be between -840 and 840 minutes.");
        }

        var result = await handler.HandleAsync(user.UserId, day, utcOffsetMinutes, cancellationToken);

        if (result.Status != HabitResultStatus.Ok)
        {
            return Invalid(result.Field!, result.Message!);
        }

        var value = result.Value!;

        return TypedResults.Ok(new HabitsForDayResponse(
            value.Date,
            value.Today,
            value.Items.Select(item => new HabitListItemResponse(
                item.Habit.Id,
                item.Habit.Name,
                item.Habit.CompletionSource.ToString(),
                item.Status.ToString(),
                item.Note,
                item.Group.ToString(),
                item.CurrentStreak,
                item.StreakUnit.ToString(),
                ToResponse(item.Habit.VersionOn(value.Date)!),
                ToResponse(item.Week))).ToList(),
            value.Upcoming.Select(habit => new UpcomingHabitResponse(habit.Id, habit.Name, habit.StartsOn)).ToList(),
            value.Upcoming.Count,
            value.ArchivedCount));
    }

    public static async Task<Ok<ArchivedHabitsResponse>> GetArchivedAsync(
        AuthenticatedUser user,
        GetArchivedHabitsHandler handler,
        CancellationToken cancellationToken)
    {
        var habits = await handler.HandleAsync(user.UserId, cancellationToken);

        return TypedResults.Ok(new ArchivedHabitsResponse(habits
            .Select(habit => new ArchivedHabitResponse(habit.Id, habit.Name, habit.Description, habit.ArchivedAtUtc!.Value,
                LastActive(habit)))
            .ToList()));
    }

    // window: adherence over the last 7, 30 (default) or 90 days.
    public static async Task<Results<Ok<HabitDetailResponse>, ValidationProblem, ProblemHttpResult>> GetAsync(
        Guid id,
        AuthenticatedUser user,
        GetHabitDetailHandler handler,
        CancellationToken cancellationToken,
        int utcOffsetMinutes = 0,
        int window = 30) =>
        Detail(await handler.HandleAsync(user.UserId, id, utcOffsetMinutes, window, cancellationToken));

    public static async Task<Results<Ok<HabitHistoryResponse>, ValidationProblem, ProblemHttpResult>> GetHistoryAsync(
        Guid id,
        AuthenticatedUser user,
        GetHabitHistoryHandler handler,
        CancellationToken cancellationToken,
        DateOnly? from = null,
        DateOnly? to = null,
        int utcOffsetMinutes = 0)
    {
        if (from is not { } first)
        {
            return Invalid("from", "The first date is required.");
        }

        if (to is not { } last)
        {
            return Invalid("to", "The last date is required.");
        }

        var result = await handler.HandleAsync(user.UserId, id, first, last, utcOffsetMinutes, cancellationToken);

        return result.Status switch
        {
            HabitResultStatus.NotFound => NotFound(),
            HabitResultStatus.Invalid => Invalid(result.Field!, result.Message!),
            _ => TypedResults.Ok(new HabitHistoryResponse(id, first, last, result.Value!.Days.Select(ToResponse).ToList()))
        };
    }

    public static async Task<Results<Created<HabitDetailResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateHabitRequest request,
        AuthenticatedUser user,
        CreateHabitHandler handler,
        CancellationToken cancellationToken)
    {
        if (request.UtcOffsetMinutes is not { } offset)
        {
            return Invalid("utcOffsetMinutes", "The device's UTC offset is required.");
        }

        if (!TryParseSource(request.CompletionSource, out var source))
        {
            return Invalid("completionSource", "Choose a manual habit.");
        }

        if (!TryParseFrequency(request.Frequency, out var frequency, out var field, out var message))
        {
            return Invalid(field!, message!);
        }

        var result = await handler.HandleAsync(user.UserId,
            new CreateHabitCommand(request.Name ?? "", request.Description, source, frequency!, request.StartsOn, offset), cancellationToken);

        return result.Status switch
        {
            HabitResultStatus.Invalid => Invalid(result.Field!, result.Message!),
            HabitResultStatus.Conflict => Conflict(result),
            _ => TypedResults.Created($"/api/habits/{result.Value!.Habit.Id}", ToResponse(result.Value!))
        };
    }

    public static async Task<Results<Ok<HabitDetailResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id,
        UpdateHabitRequest request,
        AuthenticatedUser user,
        UpdateHabitHandler handler,
        CancellationToken cancellationToken)
    {
        if (request.UtcOffsetMinutes is not { } offset)
        {
            return Invalid("utcOffsetMinutes", "The device's UTC offset is required.");
        }

        HabitFrequencyInput? frequency = null;

        if (request.Frequency is not null && !TryParseFrequency(request.Frequency, out frequency, out var field, out var message))
        {
            return Invalid(field!, message!);
        }

        return Detail(await handler.HandleAsync(user.UserId, id,
            new UpdateHabitCommand(request.Name ?? "", request.Description, frequency, offset), cancellationToken));
    }

    public static async Task<Results<Ok<HabitDetailResponse>, ValidationProblem, ProblemHttpResult>> ArchiveAsync(
        Guid id,
        HabitTodayRequest request,
        AuthenticatedUser user,
        ArchiveHabitHandler handler,
        CancellationToken cancellationToken) =>
        request.UtcOffsetMinutes is { } offset
            ? Detail(await handler.ArchiveAsync(user.UserId, id, offset, cancellationToken))
            : Invalid("utcOffsetMinutes", "The device's UTC offset is required.");

    public static async Task<Results<Ok<HabitDetailResponse>, ValidationProblem, ProblemHttpResult>> RestoreAsync(
        Guid id,
        HabitTodayRequest request,
        AuthenticatedUser user,
        ArchiveHabitHandler handler,
        CancellationToken cancellationToken) =>
        request.UtcOffsetMinutes is { } offset
            ? Detail(await handler.RestoreAsync(user.UserId, id, offset, cancellationToken))
            : Invalid("utcOffsetMinutes", "The device's UTC offset is required.");

    public static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id,
        AuthenticatedUser user,
        DeleteHabitHandler handler,
        CancellationToken cancellationToken) =>
        await handler.HandleAsync(user.UserId, id, cancellationToken) == HabitResultStatus.Ok
            ? TypedResults.NoContent()
            : NotFound();

    // Idempotent upsert of Done or Excused for the date.
    public static async Task<Results<Ok<HabitDayResponse>, ValidationProblem, ProblemHttpResult>> SetCheckInAsync(
        Guid id,
        DateOnly date,
        SetHabitCheckInRequest request,
        AuthenticatedUser user,
        SetHabitCheckInHandler handler,
        CancellationToken cancellationToken)
    {
        if (request.UtcOffsetMinutes is not { } offset)
        {
            return Invalid("utcOffsetMinutes", "The device's UTC offset is required.");
        }

        if (!TryParseName<HabitCheckInStatus>(request.Status, out var status))
        {
            return Invalid("status", "Choose Done or Excused.");
        }

        return Day(await handler.HandleAsync(user.UserId, id, new SetHabitCheckInCommand(date, status, request.Note, offset), cancellationToken));
    }

    // Undo: idempotent.
    public static async Task<Results<Ok<HabitDayResponse>, ValidationProblem, ProblemHttpResult>> RemoveCheckInAsync(
        Guid id,
        DateOnly date,
        AuthenticatedUser user,
        RemoveHabitCheckInHandler handler,
        CancellationToken cancellationToken,
        int? utcOffsetMinutes = null) =>
        utcOffsetMinutes is { } offset
            ? Day(await handler.HandleAsync(user.UserId, id, date, offset, cancellationToken))
            : Invalid("utcOffsetMinutes", "The device's UTC offset is required.");

    // ---- Mapping ----

    internal static HabitDetailResponse ToResponse(HabitDetail detail)
    {
        var habit = detail.Habit;

        return new HabitDetailResponse(
            habit.Id,
            habit.Name,
            habit.Description,
            habit.CompletionSource.ToString(),
            habit.Status.ToString(),
            habit.StartsOn,
            ToResponse(habit.Schedule[^1]),
            LastActive(habit),
            habit.CreatedAtUtc,
            habit.ArchivedAtUtc,
            ToResponse(detail.Progress),
            detail.RecentDays.Select(ToResponse).ToList());
    }

    internal static HabitProgressResponse ToResponse(HabitProgress progress) =>
        new(progress.AsOf,
            progress.TodayStatus.ToString(),
            progress.CurrentStreak,
            progress.BestStreak,
            progress.StreakUnit.ToString(),
            new HabitAdherenceResponse(progress.Adherence.Met, progress.Adherence.Expected, progress.Adherence.Percent, progress.Adherence.WindowDays),
            ToResponse(progress.ThisWeek));

    private static HabitWeekResponse? ToResponse(HabitWeekProgress? week) =>
        week is null ? null : new HabitWeekResponse(week.WeekStart, week.Done, week.Target);

    private static HabitDayItemResponse ToResponse(HabitDayEntry day) => new(day.Date, day.Status.ToString(), day.Note);

    private static HabitFrequencyResponse ToResponse(HabitScheduleVersion version)
    {
        var frequency = version.Frequency;

        return new HabitFrequencyResponse(
            frequency.Kind.ToString(),
            frequency.Weekdays?.Days.Select(day => day.ToString()).ToList(),
            frequency.TimesPerWeek,
            version.EffectiveFrom);
    }

    private static HabitFrequencyResponse? LastActive(Habit habit) =>
        habit.Schedule.LastOrDefault(version => version.Kind != HabitFrequencyKind.Inactive) is { } version ? ToResponse(version) : null;

    // ---- Parsing ----

    // Missing means Manual (the only source v1 creates).
    private static bool TryParseSource(string? text, out HabitCompletionSource source)
    {
        source = HabitCompletionSource.Manual;

        return text is null || TryParseName(text, out source);
    }

    private static bool TryParseFrequency(HabitFrequencyRequest? request, out HabitFrequencyInput? frequency, out string? field, out string? message)
    {
        frequency = null;
        field = null;
        message = null;

        if (request is null)
        {
            (field, message) = ("frequency", "Choose how often the habit is expected.");
            return false;
        }

        if (!TryParseName<HabitFrequencyKind>(request.Kind, out var kind) || kind == HabitFrequencyKind.Inactive)
        {
            (field, message) = ("frequency.kind", "Choose every day, specific days or times per week.");
            return false;
        }

        var weekdays = new List<DayOfWeek>();

        foreach (var text in request.Weekdays ?? [])
        {
            if (!TryParseName<DayOfWeek>(text, out var day))
            {
                (field, message) = ("frequency.weekdays", "Use weekday names, Monday to Sunday.");
                return false;
            }

            weekdays.Add(day);
        }

        frequency = new HabitFrequencyInput(kind, weekdays, request.TimesPerWeek);
        return true;
    }

    // Only the enum's names (case-insensitive), never numbers.
    private static bool TryParseName<T>(string? text, out T value) where T : struct, Enum
    {
        value = default;

        return text is not null
            && Enum.GetNames<T>().Any(name => string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
            && Enum.TryParse(text, ignoreCase: true, out value);
    }

    // ---- Results ----

    private static Results<Ok<HabitDetailResponse>, ValidationProblem, ProblemHttpResult> Detail(HabitResult<HabitDetail> result) =>
        result.Status switch
        {
            HabitResultStatus.NotFound => NotFound(),
            HabitResultStatus.Invalid => Invalid(result.Field!, result.Message!),
            HabitResultStatus.Conflict => Conflict(result),
            _ => TypedResults.Ok(ToResponse(result.Value!))
        };

    private static Results<Ok<HabitDayResponse>, ValidationProblem, ProblemHttpResult> Day(HabitResult<HabitDayResult> result) =>
        result.Status switch
        {
            HabitResultStatus.NotFound => NotFound(),
            HabitResultStatus.Invalid => Invalid(result.Field!, result.Message!),
            HabitResultStatus.Conflict => Conflict(result),
            _ => TypedResults.Ok(new HabitDayResponse(result.Value!.Habit.Id, result.Value.Day.Date, result.Value.Day.Status.ToString(),
                result.Value.Day.Note, ToResponse(result.Value.Progress)))
        };

    private static ProblemHttpResult Conflict<T>(HabitResult<T> result) =>
        TypedResults.Problem(
            title: "The habit can't be changed this way.",
            detail: result.Message,
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?> { ["code"] = result.Code });

    private static ProblemHttpResult NotFound() =>
        TypedResults.Problem(
            title: "Habit not found.",
            detail: "This habit does not exist.",
            statusCode: StatusCodes.Status404NotFound);

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
