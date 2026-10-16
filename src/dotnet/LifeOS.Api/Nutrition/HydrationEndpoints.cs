using System.Globalization;
using LifeOS.Api.Authentication;
using LifeOS.Application.Nutrition.Hydration;
using LifeOS.Contracts.Nutrition.Hydration;
using LifeOS.Domain.Nutrition.Hydration;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Nutrition;

// HYD-001 §8: Hydration v1. Transport only. The owner comes only from the access token; another user's
// intake is a 404, exactly like a missing one. Validation failures are field-keyed 400s.
public static class HydrationEndpoints
{
    private const string Route = "/api/nutrition/hydration";

    public static IEndpointRouteBuilder MapHydrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var hydration = endpoints.MapGroup(Route)
            .RequireAuthorization();

        hydration.MapGet("/days/{date}", GetDayAsync).WithName("GetHydrationDay");
        hydration.MapGet("/days", GetDaysAsync).WithName("GetHydrationDays");
        hydration.MapPost("/intakes", AddIntakeAsync).WithName("AddHydrationIntake");
        hydration.MapPut("/intakes/{id:guid}", UpdateIntakeAsync).WithName("UpdateHydrationIntake");
        hydration.MapDelete("/intakes/{id:guid}", DeleteIntakeAsync).WithName("DeleteHydrationIntake");
        hydration.MapGet("/settings", GetSettingsAsync).WithName("GetHydrationSettings");
        hydration.MapPut("/target", SetTargetAsync).WithName("SetHydrationTarget");
        hydration.MapPut("/quick-add", SetQuickAddAsync).WithName("SetHydrationQuickAdd");

        return endpoints;
    }

    public static async Task<Results<Ok<HydrationDayResponse>, ValidationProblem>> GetDayAsync(
        string date,
        AuthenticatedUser user,
        GetHydrationDayHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryParseDate(date, out var day))
        {
            return Invalid("date", "Supply the diary date as yyyy-MM-dd.");
        }

        var result = await handler.HandleAsync(user.UserId, day, cancellationToken);

        if (result.Status != HydrationResultStatus.Ok)
        {
            return Invalid(result.Field!, result.Message!);
        }

        return TypedResults.Ok(new HydrationDayResponse(ToResponse(result.Value!.Day), result.Value.Intakes.Select(ToResponse).ToList()));
    }

    // ?from=yyyy-MM-dd&to=yyyy-MM-dd (both required, at most 92 days).
    public static async Task<Results<Ok<HydrationRangeResponse>, ValidationProblem>> GetDaysAsync(
        string? from,
        string? to,
        AuthenticatedUser user,
        GetHydrationDaysHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryParseDate(from, out var first))
        {
            return Invalid("from", "Supply the first date as yyyy-MM-dd.");
        }

        if (!TryParseDate(to, out var last))
        {
            return Invalid("to", "Supply the last date as yyyy-MM-dd.");
        }

        var result = await handler.HandleAsync(user.UserId, first, last, cancellationToken);

        if (result.Status != HydrationResultStatus.Ok)
        {
            return Invalid(result.Field!, result.Message!);
        }

        var summary = result.Value!.Summary;

        return TypedResults.Ok(new HydrationRangeResponse(
            result.Value.Days.Select(ToResponse).ToList(),
            new HydrationRangeSummaryResponse(summary.DaysWithTarget, summary.DaysMet, summary.AverageTotalMl)));
    }

    public static async Task<Results<Created<HydrationIntakeMutationResponse>, ValidationProblem>> AddIntakeAsync(
        AddHydrationIntakeRequest request,
        AuthenticatedUser user,
        AddHydrationIntakeHandler handler,
        CancellationToken cancellationToken)
    {
        if (request.AmountMl is not { } amount)
        {
            return Invalid("amountMl", "Amount is required.");
        }

        if (request.Date is not { } date)
        {
            return Invalid("date", "Date is required.");
        }

        if (request.Time is not { } time)
        {
            return Invalid("time", "Time is required.");
        }

        if (request.UtcOffsetMinutes is not { } offset)
        {
            return Invalid("utcOffsetMinutes", "UTC offset is required.");
        }

        var result = await handler.HandleAsync(user.UserId, new AddHydrationIntakeCommand(amount, date, time, offset), cancellationToken);

        if (result.Status != HydrationResultStatus.Ok)
        {
            return Invalid(result.Field!, result.Message!);
        }

        var response = ToResponse(result.Value!);

        return TypedResults.Created($"{Route}/intakes/{response.Intake.Id}", response);
    }

    public static async Task<Results<Ok<HydrationIntakeMutationResponse>, ValidationProblem, NotFound>> UpdateIntakeAsync(
        Guid id,
        UpdateHydrationIntakeRequest request,
        AuthenticatedUser user,
        UpdateHydrationIntakeHandler handler,
        CancellationToken cancellationToken)
    {
        if (request.AmountMl is not { } amount)
        {
            return Invalid("amountMl", "Amount is required.");
        }

        if (request.Time is not { } time)
        {
            return Invalid("time", "Time is required.");
        }

        var result = await handler.HandleAsync(user.UserId, id, new UpdateHydrationIntakeCommand(amount, time), cancellationToken);

        return result.Status switch
        {
            HydrationResultStatus.NotFound => TypedResults.NotFound(),
            HydrationResultStatus.Invalid => Invalid(result.Field!, result.Message!),
            _ => TypedResults.Ok(ToResponse(result.Value!))
        };
    }

    // 200 with the updated day (not 204), so Undo refreshes the total without a second request.
    public static async Task<Results<Ok<HydrationDaySummaryResponse>, NotFound>> DeleteIntakeAsync(
        Guid id,
        AuthenticatedUser user,
        DeleteHydrationIntakeHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(user.UserId, id, cancellationToken);

        return result.Status == HydrationResultStatus.Ok
            ? TypedResults.Ok(ToResponse(result.Value!))
            : TypedResults.NotFound();
    }

    // ?date=yyyy-MM-dd (required): the target on that date and the quick-add amounts.
    public static async Task<Results<Ok<HydrationSettingsResponse>, ValidationProblem>> GetSettingsAsync(
        string? date,
        AuthenticatedUser user,
        GetHydrationSettingsHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryParseDate(date, out var day))
        {
            return Invalid("date", "Supply the date as yyyy-MM-dd.");
        }

        return Settings(await handler.HandleAsync(user.UserId, day, cancellationToken));
    }

    public static async Task<Results<Ok<HydrationSettingsResponse>, ValidationProblem>> SetTargetAsync(
        SetHydrationTargetRequest request,
        AuthenticatedUser user,
        SetHydrationTargetHandler handler,
        CancellationToken cancellationToken)
    {
        if (request.UtcOffsetMinutes is not { } offset)
        {
            return Invalid("utcOffsetMinutes", "UTC offset is required.");
        }

        return Settings(await handler.HandleAsync(user.UserId, request.TargetMl, offset, cancellationToken));
    }

    public static async Task<Results<Ok<HydrationSettingsResponse>, ValidationProblem>> SetQuickAddAsync(
        SetHydrationQuickAddRequest request,
        AuthenticatedUser user,
        SetHydrationQuickAddHandler handler,
        CancellationToken cancellationToken)
    {
        if (request.AmountsMl is not { } amounts)
        {
            return Invalid("amountsMl", "Quick-add amounts are required.");
        }

        if (request.UtcOffsetMinutes is not { } offset)
        {
            return Invalid("utcOffsetMinutes", "UTC offset is required.");
        }

        return Settings(await handler.HandleAsync(user.UserId, amounts, offset, cancellationToken));
    }

    private static Results<Ok<HydrationSettingsResponse>, ValidationProblem> Settings(HydrationResult<HydrationSettingsView> result) =>
        result.Status == HydrationResultStatus.Ok
            ? TypedResults.Ok(new HydrationSettingsResponse(result.Value!.Date, result.Value.TargetMl, result.Value.TargetEffectiveFrom,
                result.Value.QuickAddMl))
            : Invalid(result.Field!, result.Message!);

    private static bool TryParseDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    internal static HydrationDaySummaryResponse ToResponse(HydrationDay day) =>
        new(day.Date, day.TotalMl, day.TargetMl, day.IsTargetMet, day.ProgressPercent);

    private static HydrationIntakeResponse ToResponse(HydrationIntake intake) => new(intake.Id, intake.AmountMl, intake.DiaryDate,
        intake.DiaryTime, intake.OccurredAtUtc, intake.CreatedAtUtc, intake.UpdatedAtUtc);

    private static HydrationIntakeMutationResponse ToResponse(HydrationIntakeMutation mutation) =>
        new(ToResponse(mutation.Intake), ToResponse(mutation.Day));

    internal static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
