using System.Globalization;
using LifeOS.Api.Authentication;
using LifeOS.Application.Nutrition;
using LifeOS.Contracts.Nutrition;
using LifeOS.Domain.Nutrition;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Nutrition;

// NUT-003: manual daily targets. Set and remove always apply from the user's local today, derived on the
// server from its clock and the device's UTC offset; no route accepts an effective date.
public static class NutritionTargetEndpoints
{
    public static IEndpointRouteBuilder MapNutritionTargetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var targets = endpoints.MapGroup("/api/nutrition/targets")
            .RequireAuthorization();

        targets.MapGet("/", GetTargetAsync).WithName("GetNutritionTarget");
        targets.MapGet("/current", GetCurrentTargetAsync).WithName("GetCurrentNutritionTarget");
        targets.MapPut("/", SetTargetAsync).WithName("SetNutritionTarget");
        targets.MapDelete("/current", RemoveTargetAsync).WithName("RemoveNutritionTarget");

        return endpoints;
    }

    // ?date=yyyy-MM-dd: the target that applied on that diary day.
    public static async Task<Results<Ok<NutritionTargetStateResponse>, ValidationProblem>> GetTargetAsync(
        string? date,
        AuthenticatedUser user,
        GetNutritionTargetHandler handler,
        CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return NutritionEndpoints.Invalid("date", "Supply the diary date as yyyy-MM-dd.");
        }

        return ToResponse(await handler.HandleAsync(user.UserId, day, cancellationToken));
    }

    // ?utcOffsetMinutes=: the target that applies on the user's local today.
    public static async Task<Results<Ok<NutritionTargetStateResponse>, ValidationProblem>> GetCurrentTargetAsync(
        int? utcOffsetMinutes,
        AuthenticatedUser user,
        GetCurrentNutritionTargetHandler handler,
        CancellationToken cancellationToken)
    {
        if (utcOffsetMinutes is not { } offset)
        {
            return OffsetRequired();
        }

        return ToResponse(await handler.HandleAsync(user.UserId, offset, cancellationToken));
    }

    // Sets (or, the same day, replaces) the targets from today on.
    public static async Task<Results<Ok<NutritionTargetStateResponse>, ValidationProblem>> SetTargetAsync(
        SetNutritionTargetRequest request,
        AuthenticatedUser user,
        SetNutritionTargetHandler handler,
        CancellationToken cancellationToken)
    {
        if (request.UtcOffsetMinutes is not { } offset)
        {
            return OffsetRequired();
        }

        return ToResponse(await handler.HandleAsync(user.UserId,
            new SetNutritionTargetCommand(request.CaloriesKcal, request.ProteinGrams, request.CarbsGrams, request.FatGrams, offset),
            cancellationToken));
    }

    // Remove targets: none from today on; earlier days keep theirs.
    public static async Task<Results<Ok<NutritionTargetStateResponse>, ValidationProblem>> RemoveTargetAsync(
        int? utcOffsetMinutes,
        AuthenticatedUser user,
        RemoveNutritionTargetHandler handler,
        CancellationToken cancellationToken)
    {
        if (utcOffsetMinutes is not { } offset)
        {
            return OffsetRequired();
        }

        return ToResponse(await handler.HandleAsync(user.UserId, offset, cancellationToken));
    }

    private static ValidationProblem OffsetRequired() => NutritionEndpoints.Invalid("utcOffsetMinutes", "UTC offset is required.");

    private static Results<Ok<NutritionTargetStateResponse>, ValidationProblem> ToResponse(NutritionTargetResult result) =>
        result.Status == NutritionStatus.Ok
            ? TypedResults.Ok(new NutritionTargetStateResponse(result.Date, result.Target is { } target ? ToResponse(target) : null))
            : NutritionEndpoints.Invalid(result.Field ?? "target", result.Message!);

    private static NutritionTargetResponse ToResponse(EffectiveNutritionTarget target) => new(target.EffectiveFrom,
        target.Values.CaloriesKcal, target.Values.ProteinGrams, target.Values.CarbsGrams, target.Values.FatGrams, target.Source.ToString());

    internal static DailyNutritionTargetResponse? ToSummaryTarget(NutritionTargetValues? target) =>
        target is null ? null : new(target.CaloriesKcal, target.ProteinGrams, target.CarbsGrams, target.FatGrams);
}
