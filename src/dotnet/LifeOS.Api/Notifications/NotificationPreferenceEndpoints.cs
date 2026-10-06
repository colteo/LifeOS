using System.Globalization;
using LifeOS.Api.Authentication;
using LifeOS.Application.Notifications;
using LifeOS.Contracts.Notifications;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Notifications;

// AUTO-003A: the signed-in user's reminder preferences and quiet hours. Transport only; the owner
// comes only from the access token, so no request can read or change another user's preferences.
public static class NotificationPreferenceEndpoints
{
    public const string Path = "/api/notification-preferences";
    public const string TimeFormat = "HH:mm";

    public static IEndpointRouteBuilder MapNotificationPreferenceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var preferences = endpoints.MapGroup(Path).RequireAuthorization();

        preferences.MapGet("/", GetAsync).WithName("GetNotificationPreferences");
        preferences.MapPut("/", SetAsync).WithName("SetNotificationPreferences");

        return endpoints;
    }

    public static async Task<Ok<NotificationPreferencesResponse>> GetAsync(
        AuthenticatedUser user,
        GetNotificationPreferencesHandler handler,
        CancellationToken cancellationToken)
    {
        var preferences = await handler.HandleAsync(user.UserId, cancellationToken);

        return TypedResults.Ok(new NotificationPreferencesResponse(
            preferences.RecurringTransactionReminders,
            preferences.PlannedExpenseReminders,
            FormatTime(preferences.QuietHoursStart),
            FormatTime(preferences.QuietHoursEnd)));
    }

    public static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> SetAsync(
        SetNotificationPreferencesRequest request,
        AuthenticatedUser user,
        SetNotificationPreferencesHandler handler,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.RecurringTransactionReminders is null)
        {
            errors["recurringTransactionReminders"] = ["Recurring transaction reminders is required."];
        }

        if (request.PlannedExpenseReminders is null)
        {
            errors["plannedExpenseReminders"] = ["Planned expense reminders is required."];
        }

        if (!TryParseTime(request.QuietHoursStart, out var start))
        {
            errors["quietHoursStart"] = ["Quiet hours start must be a time such as 22:00."];
        }

        if (!TryParseTime(request.QuietHoursEnd, out var end))
        {
            errors["quietHoursEnd"] = ["Quiet hours end must be a time such as 08:00."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var result = await handler.HandleAsync(
            user.UserId, request.RecurringTransactionReminders!.Value, request.PlannedExpenseReminders!.Value, start, end, cancellationToken);

        return result switch
        {
            SetNotificationPreferencesResult.Saved => TypedResults.NoContent(),
            SetNotificationPreferencesResult.InvalidQuietHours => TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["quietHoursEnd"] = ["Quiet hours must start and end at different times."]
            }),
            _ => TypedResults.Problem(title: "User not found.", statusCode: StatusCodes.Status404NotFound)
        };
    }

    internal static string FormatTime(TimeOnly time) => time.ToString(TimeFormat, CultureInfo.InvariantCulture);

    // Exactly "HH:mm", 00:00–23:59.
    internal static bool TryParseTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
}
