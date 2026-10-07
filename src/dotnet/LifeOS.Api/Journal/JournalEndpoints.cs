using System.Globalization;
using LifeOS.Api.Authentication;
using LifeOS.Application.Journal;
using LifeOS.Contracts.Journal;
using LifeOS.Domain.Journal;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Journal;

// JRN-001: the personal journal. Transport only. The owner comes only from the access token; another
// user's entry is a 404, exactly like a missing one.
public static class JournalEndpoints
{
    public static IEndpointRouteBuilder MapJournalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var journal = endpoints.MapGroup("/api/journal")
            .RequireAuthorization();

        journal.MapGet("/", GetPageAsync).WithName("GetJournalEntries");
        journal.MapGet("/{id:guid}", GetAsync).WithName("GetJournalEntry");
        journal.MapPost("/", CreateAsync).WithName("CreateJournalEntry");
        journal.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateJournalEntry");
        journal.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteJournalEntry");

        return endpoints;
    }

    // limit: 1–50 (default 20). cursor: the previous page's NextCursor.
    public static async Task<Results<Ok<JournalEntryPageResponse>, ValidationProblem>> GetPageAsync(
        AuthenticatedUser user,
        GetJournalEntriesHandler handler,
        CancellationToken cancellationToken,
        int? limit = null,
        string? cursor = null)
    {
        JournalCursor? after = null;

        if (cursor is not null && !TryParseCursor(cursor, out after))
        {
            return Invalid("cursor", "The cursor is not valid. Start again from the first page.");
        }

        JournalPage page;

        try
        {
            page = await handler.HandleAsync(user.UserId, after, limit ?? GetJournalEntriesHandler.DefaultPageSize, cancellationToken);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Invalid("limit", $"The page size must be between 1 and {GetJournalEntriesHandler.MaxPageSize}.");
        }

        return TypedResults.Ok(new JournalEntryPageResponse(
            page.Items.Select(ToResponse).ToList(),
            page.Next is { } next ? FormatCursor(next) : null));
    }

    public static async Task<Results<Ok<JournalEntryResponse>, ProblemHttpResult>> GetAsync(
        Guid id,
        AuthenticatedUser user,
        GetJournalEntryHandler handler,
        CancellationToken cancellationToken) =>
        await handler.HandleAsync(user.UserId, id, cancellationToken) is { } entry
            ? TypedResults.Ok(ToResponse(entry))
            : NotFound();

    public static async Task<Results<Created<JournalEntryResponse>, ValidationProblem>> CreateAsync(
        CreateJournalEntryRequest request,
        AuthenticatedUser user,
        CreateJournalEntryHandler handler,
        CancellationToken cancellationToken)
    {
        if (request.OccurredAtUtc is not { } occurredAtUtc)
        {
            return Invalid("occurredAtUtc", "The time the entry is about is required.");
        }

        var result = await handler.HandleAsync(user.UserId,
            new CreateJournalEntryCommand(occurredAtUtc, request.Title, request.Content ?? ""), cancellationToken);

        if (result.Status != JournalResultStatus.Ok)
        {
            return Invalid(result);
        }

        var response = ToResponse(result.Entry!);

        return TypedResults.Created($"/api/journal/{response.Id}", response);
    }

    public static async Task<Results<Ok<JournalEntryResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id,
        UpdateJournalEntryRequest request,
        AuthenticatedUser user,
        UpdateJournalEntryHandler handler,
        CancellationToken cancellationToken)
    {
        if (request.OccurredAtUtc is not { } occurredAtUtc)
        {
            return Invalid("occurredAtUtc", "The time the entry is about is required.");
        }

        var result = await handler.HandleAsync(user.UserId, id,
            new UpdateJournalEntryCommand(occurredAtUtc, request.Title, request.Content ?? ""), cancellationToken);

        return result.Status switch
        {
            JournalResultStatus.NotFound => NotFound(),
            JournalResultStatus.Invalid => Invalid(result),
            _ => TypedResults.Ok(ToResponse(result.Entry!))
        };
    }

    public static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id,
        AuthenticatedUser user,
        DeleteJournalEntryHandler handler,
        CancellationToken cancellationToken) =>
        await handler.HandleAsync(user.UserId, id, cancellationToken) == JournalResultStatus.Ok
            ? TypedResults.NoContent()
            : NotFound();

    internal static JournalEntryResponse ToResponse(JournalEntry entry) =>
        new(entry.Id, entry.OccurredAtUtc, entry.Title, entry.Content, entry.CreatedAtUtc, entry.UpdatedAtUtc);

    // ---- Cursor ----

    // Opaque to clients: "<OccurredAt UtcTicks>_<CreatedAt UtcTicks>_<id>" of the previous page's last item.
    internal static string FormatCursor(JournalCursor cursor) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{cursor.OccurredAtUtc.UtcTicks}_{cursor.CreatedAtUtc.UtcTicks}_{cursor.Id:N}");

    internal static bool TryParseCursor(string text, out JournalCursor? cursor)
    {
        cursor = null;
        var parts = text.Split('_');

        if (parts.Length != 3
            || !TryParseTicks(parts[0], out var occurredAt)
            || !TryParseTicks(parts[1], out var createdAt)
            || !Guid.TryParseExact(parts[2], "N", out var id))
        {
            return false;
        }

        cursor = new JournalCursor(occurredAt, createdAt, id);
        return true;
    }

    private static bool TryParseTicks(string text, out DateTimeOffset value)
    {
        value = default;

        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks < DateTimeOffset.MinValue.UtcTicks
            || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return false;
        }

        value = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }

    private static ProblemHttpResult NotFound() =>
        TypedResults.Problem(
            title: "Journal entry not found.",
            detail: "This journal entry does not exist.",
            statusCode: StatusCodes.Status404NotFound);

    private static ValidationProblem Invalid(JournalResult result) => Invalid(result.Field ?? "request", result.Message!);

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
