using System.Globalization;
using LifeOS.Api.Authentication;
using LifeOS.Application.Reminders;
using LifeOS.Contracts.Reminders;
using LifeOS.Domain.Reminders;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Reminders;

// REM-001 §9: the user's reminders (their own and, from R5, the habit and water nudges). Transport only. The owner comes only from the access token;
// another user's reminder is a 404, exactly like a missing one. Validation failures are field-keyed
// 400s; business conflicts are 409 with a stable "code".
public static class ReminderEndpoints
{
    public const string Path = "/api/reminders";
    public const string TimeFormat = "HH:mm";
    public const string LocalDateTimeFormat = "yyyy-MM-dd'T'HH:mm";

    public static IEndpointRouteBuilder MapReminderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var reminders = endpoints.MapGroup(Path)
            .RequireAuthorization();

        reminders.MapGet("/", GetAllAsync).WithName("GetReminders");
        reminders.MapGet("/{id:guid}", GetAsync).WithName("GetReminder");
        reminders.MapPost("/", CreateAsync).WithName("CreateReminder");
        reminders.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateReminder");
        reminders.MapPost("/{id:guid}/pause", PauseAsync).WithName("PauseReminder");
        reminders.MapPost("/{id:guid}/resume", ResumeAsync).WithName("ResumeReminder");
        reminders.MapPost("/{id:guid}/snooze", SnoozeAsync).WithName("SnoozeReminder");
        reminders.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteReminder");

        return endpoints;
    }

    public static async Task<Ok<RemindersResponse>> GetAllAsync(
        AuthenticatedUser user,
        GetRemindersHandler handler,
        CancellationToken cancellationToken)
    {
        var overview = await handler.HandleAsync(user.UserId, cancellationToken);

        return TypedResults.Ok(new RemindersResponse(
            overview.TimeZoneKnown,
            overview.PushAvailable,
            overview.Upcoming.Select(ToResponse).ToList(),
            overview.Paused.Select(ToResponse).ToList(),
            overview.Completed.Select(ToResponse).ToList()));
    }

    public static async Task<Results<Ok<ReminderResponse>, ValidationProblem, ProblemHttpResult>> GetAsync(
        Guid id,
        AuthenticatedUser user,
        GetReminderHandler handler,
        CancellationToken cancellationToken) =>
        View(await handler.HandleAsync(user.UserId, id, cancellationToken));

    public static async Task<Results<Created<ReminderResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateReminderRequest request,
        AuthenticatedUser user,
        CreateReminderHandler handler,
        CancellationToken cancellationToken)
    {
        var kind = ReminderKind.Custom;

        if (request.Kind is not null && !TryParseName(request.Kind, out kind))
        {
            return Invalid("kind", "Choose a personal reminder, a habit or the water target.");
        }

        if (request.UtcOffsetMinutes is not { } offset)
        {
            return Invalid("utcOffsetMinutes", "The device's UTC offset is required.");
        }

        if (!TryParseSchedule(request.Schedule, out var schedule, out var field, out var message))
        {
            return Invalid(field!, message!);
        }

        var result = await handler.HandleAsync(user.UserId,
            new CreateReminderCommand(request.Title, request.Note, schedule!, offset, kind, request.HabitId), cancellationToken);

        return result.Status switch
        {
            ReminderResultStatus.Invalid => Invalid(result.Field!, result.Message!),
            ReminderResultStatus.Conflict => Conflict(result),
            // The only thing a create can miss is the habit (another user's habit included).
            ReminderResultStatus.NotFound => TypedResults.Problem(title: "Habit not found.", detail: "This habit does not exist.",
                statusCode: StatusCodes.Status404NotFound),
            _ => TypedResults.Created($"{Path}/{result.Value!.Reminder.Id}", ToResponse(result.Value!))
        };
    }

    public static async Task<Results<Ok<ReminderResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id,
        UpdateReminderRequest request,
        AuthenticatedUser user,
        UpdateReminderHandler handler,
        CancellationToken cancellationToken)
    {
        if (request.UtcOffsetMinutes is not { } offset)
        {
            return Invalid("utcOffsetMinutes", "The device's UTC offset is required.");
        }

        if (!TryParseSchedule(request.Schedule, out var schedule, out var field, out var message))
        {
            return Invalid(field!, message!);
        }

        return View(await handler.HandleAsync(user.UserId, id, new UpdateReminderCommand(request.Title ?? "", request.Note, schedule!, offset),
            cancellationToken));
    }

    public static async Task<Results<Ok<ReminderResponse>, ValidationProblem, ProblemHttpResult>> PauseAsync(
        Guid id,
        AuthenticatedUser user,
        PauseReminderHandler handler,
        CancellationToken cancellationToken) =>
        View(await handler.PauseAsync(user.UserId, id, cancellationToken));

    public static async Task<Results<Ok<ReminderResponse>, ValidationProblem, ProblemHttpResult>> ResumeAsync(
        Guid id,
        ResumeReminderRequest request,
        AuthenticatedUser user,
        PauseReminderHandler handler,
        CancellationToken cancellationToken) =>
        request.UtcOffsetMinutes is { } offset
            ? View(await handler.ResumeAsync(user.UserId, id, offset, cancellationToken))
            : Invalid("utcOffsetMinutes", "The device's UTC offset is required.");

    public static async Task<Results<Ok<ReminderResponse>, ValidationProblem, ProblemHttpResult>> SnoozeAsync(
        Guid id,
        SnoozeReminderRequest request,
        AuthenticatedUser user,
        SnoozeReminderHandler handler,
        CancellationToken cancellationToken) =>
        request.Minutes is { } minutes
            ? View(await handler.HandleAsync(user.UserId, id, minutes, cancellationToken))
            : Invalid("minutes", "Snooze for 15 minutes, 1 hour or 3 hours.");

    public static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id,
        AuthenticatedUser user,
        DeleteReminderHandler handler,
        CancellationToken cancellationToken) =>
        await handler.HandleAsync(user.UserId, id, cancellationToken) == ReminderResultStatus.Ok
            ? TypedResults.NoContent()
            : NotFound();

    // ---- Mapping ----

    internal static ReminderResponse ToResponse(ReminderView view)
    {
        var reminder = view.Reminder;
        var schedule = reminder.Schedule;

        return new ReminderResponse(
            reminder.Id,
            reminder.Kind.ToString(),
            view.Label,
            reminder.Title,
            reminder.Note,
            new ReminderScheduleResponse(
                schedule.Kind.ToString(),
                schedule.Date,
                schedule.Kind == ReminderScheduleKind.Weekly ? schedule.Weekdays.Select(day => day.ToString()).ToList() : null,
                schedule.Time.ToString(TimeFormat, CultureInfo.InvariantCulture)),
            reminder.Status.ToString(),
            reminder.NextDueLocal?.ToString(LocalDateTimeFormat, CultureInfo.InvariantCulture),
            reminder.SnoozeUntilUtc,
            reminder.LastTriggeredAtUtc,
            reminder.LastOutcome?.ToString(),
            view.InsideQuietHours,
            reminder.HabitId);
    }

    // ---- Parsing ----

    private static bool TryParseSchedule(ReminderScheduleRequest? request, out ReminderScheduleInput? schedule, out string? field, out string? message)
    {
        schedule = null;
        field = null;
        message = null;

        if (request is null)
        {
            (field, message) = ("schedule", "Choose when to be reminded.");
            return false;
        }

        if (!TryParseName<ReminderScheduleKind>(request.Kind, out var kind))
        {
            (field, message) = ("schedule.kind", "Choose once, every day or some days.");
            return false;
        }

        if (!TimeOnly.TryParseExact(request.Time, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            (field, message) = ("schedule.time", "Choose a time such as 08:30.");
            return false;
        }

        var weekdays = new List<DayOfWeek>();

        foreach (var text in request.Weekdays ?? [])
        {
            if (!TryParseName<DayOfWeek>(text, out var day))
            {
                (field, message) = ("schedule.weekdays", "Use weekday names, Monday to Sunday.");
                return false;
            }

            weekdays.Add(day);
        }

        // Fields that do not belong to the kind are ignored, like an unused form section.
        schedule = new ReminderScheduleInput(kind, kind == ReminderScheduleKind.Once ? request.Date : null, weekdays, time);
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

    private static Results<Ok<ReminderResponse>, ValidationProblem, ProblemHttpResult> View(ReminderResult<ReminderView> result) =>
        result.Status switch
        {
            ReminderResultStatus.NotFound => NotFound(),
            ReminderResultStatus.Invalid => Invalid(result.Field!, result.Message!),
            ReminderResultStatus.Conflict => Conflict(result),
            _ => TypedResults.Ok(ToResponse(result.Value!))
        };

    private static ProblemHttpResult Conflict<T>(ReminderResult<T> result) =>
        TypedResults.Problem(
            title: "The reminder can't be changed this way.",
            detail: result.Message,
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?> { ["code"] = result.Code });

    private static ProblemHttpResult NotFound() =>
        TypedResults.Problem(
            title: "Reminder not found.",
            detail: "This reminder does not exist.",
            statusCode: StatusCodes.Status404NotFound);

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
