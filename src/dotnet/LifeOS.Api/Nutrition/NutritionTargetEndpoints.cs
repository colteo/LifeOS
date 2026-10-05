using System.Globalization;
using LifeOS.Api.Authentication;
using LifeOS.Application.Nutrition;
using LifeOS.Contracts.Nutrition;
using LifeOS.Domain.Nutrition;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Nutrition;

// NUT-003: nutrition target planning. Plans carry explicit dates, so no route needs a time zone.
// Overlapping periods are 409 with a readable message; nothing is moved, shortened or merged.
public static class NutritionTargetEndpoints
{
    public const string OverlapTitle = "Target periods overlap";

    public static IEndpointRouteBuilder MapNutritionTargetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var plans = endpoints.MapGroup("/api/nutrition/target-plans")
            .RequireAuthorization();

        plans.MapGet("/", GetPlansAsync).WithName("GetNutritionTargetPlans");
        plans.MapGet("/{id:guid}", GetPlanAsync).WithName("GetNutritionTargetPlan");
        plans.MapPost("/", CreatePlanAsync).WithName("CreateNutritionTargetPlan");
        plans.MapPut("/{id:guid}", UpdatePlanAsync).WithName("UpdateNutritionTargetPlan");
        plans.MapDelete("/{id:guid}", DeletePlanAsync).WithName("DeleteNutritionTargetPlan");

        var nutrition = endpoints.MapGroup("/api/nutrition")
            .RequireAuthorization();

        nutrition.MapGet("/targets/resolved", GetResolvedAsync).WithName("GetResolvedNutritionTarget");
        nutrition.MapPut("/target-overrides/{date}", SetOverrideAsync).WithName("SetNutritionTargetOverride");
        nutrition.MapDelete("/target-overrides/{date}", RemoveOverrideAsync).WithName("RemoveNutritionTargetOverride");

        return endpoints;
    }

    // All of the user's plans, by start date (past, current and upcoming).
    public static async Task<Ok<IReadOnlyList<NutritionTargetPlanResponse>>> GetPlansAsync(
        AuthenticatedUser user,
        GetNutritionTargetPlansHandler handler,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<NutritionTargetPlanResponse> plans = (await handler.HandleAsync(user.UserId, cancellationToken)).Select(ToResponse).ToList();

        return TypedResults.Ok(plans);
    }

    public static async Task<Results<Ok<NutritionTargetPlanResponse>, NotFound>> GetPlanAsync(
        Guid id,
        AuthenticatedUser user,
        GetNutritionTargetPlanHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(user.UserId, id, cancellationToken);

        return result.Plan is { } plan ? TypedResults.Ok(ToResponse(plan)) : TypedResults.NotFound();
    }

    public static async Task<Results<Created<NutritionTargetPlanResponse>, ValidationProblem, ProblemHttpResult>> CreatePlanAsync(
        NutritionTargetPlanRequest request,
        AuthenticatedUser user,
        SaveNutritionTargetPlanHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryParse(request, out var command, out var invalid))
        {
            return invalid!;
        }

        var result = await handler.CreateAsync(user.UserId, command!, cancellationToken);

        return result.Status switch
        {
            NutritionTargetStatus.Ok => TypedResults.Created($"/api/nutrition/target-plans/{result.Plan!.Id}", ToResponse(result.Plan)),
            NutritionTargetStatus.Overlap => Overlap(result.Message!),
            _ => NutritionEndpoints.Invalid(result.Field ?? "plan", result.Message!)
        };
    }

    public static async Task<Results<Ok<NutritionTargetPlanResponse>, ValidationProblem, ProblemHttpResult, NotFound>> UpdatePlanAsync(
        Guid id,
        NutritionTargetPlanRequest request,
        AuthenticatedUser user,
        SaveNutritionTargetPlanHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryParse(request, out var command, out var invalid))
        {
            return invalid!;
        }

        var result = await handler.UpdateAsync(user.UserId, id, command!, cancellationToken);

        return result.Status switch
        {
            NutritionTargetStatus.Ok => TypedResults.Ok(ToResponse(result.Plan!)),
            NutritionTargetStatus.NotFound => TypedResults.NotFound(),
            NutritionTargetStatus.Overlap => Overlap(result.Message!),
            _ => NutritionEndpoints.Invalid(result.Field ?? "plan", result.Message!)
        };
    }

    // The plan, its weekly rules and its daily overrides. Meals are untouched.
    public static async Task<Results<NoContent, NotFound>> DeletePlanAsync(
        Guid id,
        AuthenticatedUser user,
        DeleteNutritionTargetPlanHandler handler,
        CancellationToken cancellationToken) =>
        await handler.HandleAsync(user.UserId, id, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound();

    // ?date=yyyy-MM-dd: the resolved target for that diary date.
    public static async Task<Results<Ok<ResolvedNutritionTargetResponse>, ValidationProblem>> GetResolvedAsync(
        string? date,
        AuthenticatedUser user,
        GetResolvedNutritionTargetHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryParseDate(date, out var day))
        {
            return NutritionEndpoints.Invalid("date", "Supply the diary date as yyyy-MM-dd.");
        }

        return ToResponse(await handler.HandleAsync(user.UserId, day, cancellationToken));
    }

    // A custom target or no target for one date inside a plan. Outside every plan: 400.
    public static async Task<Results<Ok<ResolvedNutritionTargetResponse>, ValidationProblem>> SetOverrideAsync(
        string date,
        NutritionTargetOverrideDto request,
        AuthenticatedUser user,
        SetNutritionTargetOverrideHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryParseDate(date, out var day))
        {
            return NutritionEndpoints.Invalid("date", "Supply the date as yyyy-MM-dd.");
        }

        if (!Enum.TryParse<NutritionTargetOverrideMode>(request.Mode?.Trim(), ignoreCase: true, out var mode)
            || !Enum.IsDefined(mode) || int.TryParse(request.Mode, out _))
        {
            return NutritionEndpoints.Invalid("mode", "Mode must be Custom or NoTarget.");
        }

        return ToResponse(await handler.HandleAsync(user.UserId, day, mode, ToInput(request.Target), cancellationToken));
    }

    // Back to the weekday rule. Idempotent.
    public static async Task<Results<Ok<ResolvedNutritionTargetResponse>, ValidationProblem>> RemoveOverrideAsync(
        string date,
        AuthenticatedUser user,
        RemoveNutritionTargetOverrideHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryParseDate(date, out var day))
        {
            return NutritionEndpoints.Invalid("date", "Supply the date as yyyy-MM-dd.");
        }

        return ToResponse(await handler.HandleAsync(user.UserId, day, cancellationToken));
    }

    internal static NutritionTargetValuesDto? ToDto(NutritionTargetValues? target) =>
        target is null ? null : new(target.CaloriesKcal, target.ProteinGrams, target.CarbsGrams, target.FatGrams);

    private static ProblemHttpResult Overlap(string message) =>
        TypedResults.Problem(message, statusCode: StatusCodes.Status409Conflict, title: OverlapTitle);

    private static bool TryParse(NutritionTargetPlanRequest request, out NutritionTargetPlanCommand? command, out ValidationProblem? invalid)
    {
        command = null;
        invalid = null;

        if (request.StartsOn is not { } startsOn)
        {
            invalid = NutritionEndpoints.Invalid("startsOn", "Choose the first day of the period.");
            return false;
        }

        if (request.EndsOn is not { } endsOn)
        {
            invalid = NutritionEndpoints.Invalid("endsOn", "Choose the last day of the period.");
            return false;
        }

        var rules = new List<NutritionTargetDayRuleInput>();

        foreach (var rule in request.WeeklyRules ?? [])
        {
            if (!TryParseName<DayOfWeek>(rule.Weekday, out var weekday))
            {
                invalid = NutritionEndpoints.Invalid("weeklyRules", "Weekday must be Monday, Tuesday, Wednesday, Thursday, Friday, Saturday or Sunday.");
                return false;
            }

            if (!TryParseName<NutritionTargetDayMode>(rule.Mode, out var mode))
            {
                invalid = NutritionEndpoints.Invalid($"weeklyRules.{weekday}", "Mode must be Default, Custom or NoTarget.");
                return false;
            }

            rules.Add(new(weekday, mode, ToInput(rule.Target)));
        }

        command = new(startsOn, endsOn, ToInput(request.DefaultTarget), rules);

        return true;
    }

    // Names only (no numbers), ignoring case.
    private static bool TryParseName<TEnum>(string? value, out TEnum parsed) where TEnum : struct, Enum
    {
        parsed = default;

        return !string.IsNullOrWhiteSpace(value) && !int.TryParse(value, out _)
            && Enum.TryParse(value.Trim(), ignoreCase: true, out parsed) && Enum.IsDefined(parsed);
    }

    private static NutritionTargetInput? ToInput(NutritionTargetValuesDto? target) =>
        target is null ? null : new(target.CaloriesKcal, target.ProteinGrams, target.CarbsGrams, target.FatGrams);

    private static bool TryParseDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static NutritionTargetPlanResponse ToResponse(NutritionTargetPlan plan) => new(plan.Id, plan.StartsOn, plan.EndsOn,
        ToDto(plan.DefaultTarget),
        plan.Rules.Select(rule => new NutritionTargetDayRuleDto(rule.Weekday.ToString(), rule.Mode.ToString(), ToDto(rule.Target))).ToList());

    private static Results<Ok<ResolvedNutritionTargetResponse>, ValidationProblem> ToResponse(ResolvedNutritionTargetResult result) =>
        result.Status == NutritionTargetStatus.Ok
            ? TypedResults.Ok(new ResolvedNutritionTargetResponse(result.Resolved!.Date, ToDto(result.Resolved.Target),
                result.Resolved.CoveredByPlan,
                result.Resolved.Override is { } item ? new NutritionTargetOverrideDto(item.Mode.ToString(), ToDto(item.Target)) : null))
            : NutritionEndpoints.Invalid(result.Field ?? "date", result.Message!);
}
