using System.Globalization;
using LifeOS.Api.Authentication;
using LifeOS.Application.Gym.History;
using LifeOS.Contracts.Gym.History;
using LifeOS.Contracts.Gym.Sessions;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Gym;

// Workout history: completed workouts, read-only, from their execution snapshots. The group has no
// mutating route. 404 covers a missing session, another user's, and one still in progress.
public static class WorkoutHistoryEndpoints
{
    public static IEndpointRouteBuilder MapWorkoutHistoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Owned by the authenticated user; the user id comes only from the access token.
        var history = endpoints.MapGroup("/api/gym/history")
            .RequireAuthorization();

        history.MapGet("/", GetPageAsync).WithName("GetWorkoutHistory");
        history.MapGet("/{sessionId:guid}", GetAsync).WithName("GetWorkoutHistoryDetail");

        var sessions = endpoints.MapGroup("/api/gym/sessions")
            .RequireAuthorization();

        sessions.MapGet("/{sessionId:guid}/previous-performance", GetPreviousPerformanceAsync)
            .WithName("GetPreviousPerformance");
        sessions.MapGet("/{sessionId:guid}/exercises/{exerciseId:guid}/history", GetExerciseHistoryAsync)
            .WithName("GetExerciseHistory");

        return endpoints;
    }

    // limit: 1–50 (default 20). cursor: the previous page's NextCursor.
    public static async Task<Results<Ok<WorkoutHistoryPageResponse>, ValidationProblem>> GetPageAsync(
        AuthenticatedUser user,
        GetWorkoutHistoryHandler handler,
        CancellationToken cancellationToken,
        int? limit = null,
        string? cursor = null)
    {
        WorkoutHistoryCursor? after = null;

        if (cursor is not null && !TryParseCursor(cursor, out after))
        {
            return Invalid("cursor", "The cursor is not valid. Start again from the first page.");
        }

        WorkoutHistoryPage page;

        try
        {
            page = await handler.HandleAsync(user.UserId, after, limit ?? GetWorkoutHistoryHandler.DefaultPageSize, cancellationToken);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Invalid("limit", $"The page size must be between 1 and {GetWorkoutHistoryHandler.MaxPageSize}.");
        }

        return TypedResults.Ok(new WorkoutHistoryPageResponse(
            page.Items
                .Select(item => new WorkoutHistoryItemResponse(
                    item.Id,
                    item.ProgramName,
                    item.WorkoutName,
                    item.StartedAtUtc,
                    item.CompletedAtUtc,
                    item.CompletedSetCount,
                    item.PrescribedSetCount,
                    item.ExerciseCount))
                .ToList(),
            page.Next is { } next ? FormatCursor(next) : null));
    }

    // A completed workout, as the session contract.
    public static async Task<Results<Ok<WorkoutSessionResponse>, ProblemHttpResult>> GetAsync(
        Guid sessionId,
        AuthenticatedUser user,
        GetWorkoutHistoryDetailHandler handler,
        CancellationToken cancellationToken) =>
        await handler.HandleAsync(user.UserId, sessionId, cancellationToken) is { } session
            ? TypedResults.Ok(WorkoutSessionEndpoints.ToResponse(session))
            : TypedResults.Problem(
                title: "Workout not found.",
                detail: "This completed workout does not exist.",
                statusCode: StatusCodes.Status404NotFound);

    public static async Task<Results<Ok<PreviousPerformanceResponse>, ProblemHttpResult>> GetPreviousPerformanceAsync(
        Guid sessionId,
        AuthenticatedUser user,
        GetPreviousPerformanceHandler handler,
        CancellationToken cancellationToken)
    {
        if (await handler.HandleAsync(user.UserId, sessionId, cancellationToken) is not { } previous)
        {
            return TypedResults.Problem(
                title: "Workout session not found.",
                detail: "This workout session does not exist.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return TypedResults.Ok(new PreviousPerformanceResponse(
            previous.SessionId,
            previous.Exercises
                .Select(exercise => new PreviousExercisePerformanceResponse(
                    exercise.ExerciseId,
                    exercise.SessionId,
                    exercise.WorkoutName,
                    exercise.CompletedAtUtc,
                    exercise.Sets
                        .Select(set => new PreviousSetResponse(set.BlockPosition, set.Position, set.ActualReps, set.WeightKg))
                        .ToList()))
                .ToList()));
    }

    // The earlier completed workouts of one exercise of the session. limit: 1–20 (default 5). cursor:
    // the previous page's NextCursor. 404 also covers an exercise the session does not contain.
    public static async Task<Results<Ok<ExerciseHistoryPageResponse>, ValidationProblem, ProblemHttpResult>> GetExerciseHistoryAsync(
        Guid sessionId,
        Guid exerciseId,
        AuthenticatedUser user,
        GetExerciseHistoryHandler handler,
        CancellationToken cancellationToken,
        int? limit = null,
        string? cursor = null)
    {
        WorkoutHistoryCursor? after = null;

        if (cursor is not null && !TryParseCursor(cursor, out after))
        {
            return Invalid("cursor", "The cursor is not valid. Start again from the first page.");
        }

        ExerciseHistoryPage? page;

        try
        {
            page = await handler.HandleAsync(user.UserId, sessionId, exerciseId, after, limit ?? GetExerciseHistoryHandler.DefaultPageSize, cancellationToken);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Invalid("limit", $"The page size must be between 1 and {GetExerciseHistoryHandler.MaxPageSize}.");
        }

        if (page is null)
        {
            return TypedResults.Problem(
                title: "Exercise not found.",
                detail: "This workout session or exercise does not exist.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return TypedResults.Ok(new ExerciseHistoryPageResponse(
            page.ExerciseId,
            page.Items
                .Select(item => new ExerciseHistoryEntryResponse(
                    item.SessionId,
                    item.ProgramName,
                    item.WorkoutName,
                    item.CompletedAtUtc,
                    item.Sets
                        .Select(set => new PreviousSetResponse(set.BlockPosition, set.Position, set.ActualReps, set.WeightKg))
                        .ToList()))
                .ToList(),
            page.Next is { } next ? FormatCursor(next) : null));
    }

    // ---- Cursor ----

    // Opaque to clients: "<UtcTicks>_<id>" of the last item of the previous page.
    internal static string FormatCursor(WorkoutHistoryCursor cursor) =>
        $"{cursor.CompletedAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture)}_{cursor.Id:N}";

    internal static bool TryParseCursor(string text, out WorkoutHistoryCursor? cursor)
    {
        cursor = null;
        var parts = text.Split('_');

        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks < DateTimeOffset.MinValue.UtcTicks
            || ticks > DateTimeOffset.MaxValue.UtcTicks
            || !Guid.TryParseExact(parts[1], "N", out var id))
        {
            return false;
        }

        cursor = new WorkoutHistoryCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
        return true;
    }

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
