using System.Diagnostics;
using LifeOS.Api.Authentication;
using LifeOS.Application.Memory;
using LifeOS.Contracts.Memory;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Memory;

// AI-004: the Journal Memory Layer over the authenticated user's own journal. Transport only. The owner
// comes only from the access token. Search never calls the answer model; ask calls it only after
// retrieval found evidence. Nothing (question, passages, answer) is persisted.
//
// Logs: one line per request with outcome, counts, versions and latency only; never the question,
// journal text, chunks, vectors, answers, excerpts or the user id.
public static class JournalMemoryEndpoints
{
    public const int ExcerptLength = 240;

    private const string LoggerName = "LifeOS.Api.Memory";
    private const string UnavailableMessage = "Journal memory is unavailable right now. Your journal is safe; try again later.";
    private const string FailedMessage = "Journal memory could not produce a valid result. Try again later.";

    public static IEndpointRouteBuilder MapJournalMemoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var memory = endpoints.MapGroup("/api/journal/memory")
            .RequireAuthorization();

        memory.MapGet("/status", GetStatusAsync).WithName("GetJournalMemoryStatus");
        memory.MapPost("/sync", SyncAsync).WithName("SyncJournalMemory");
        memory.MapGet("/search", SearchAsync).WithName("SearchJournalMemory");
        memory.MapPost("/ask", AskAsync).WithName("AskJournalMemory");

        return endpoints;
    }

    public static async Task<Ok<JournalMemoryStatusResponse>> GetStatusAsync(
        AuthenticatedUser user,
        GetJournalMemoryStatusHandler handler,
        CancellationToken cancellationToken)
    {
        var status = await handler.HandleAsync(user.UserId, cancellationToken);

        return TypedResults.Ok(new JournalMemoryStatusResponse(
            status.Counts.IndexedEntries,
            status.Counts.PendingEntries,
            status.Counts.PendingEntries > 0,
            IndexResponse()));
    }

    // Indexes a bounded number of the user's pending entries (limit 1–10, default 3).
    public static async Task<Results<Ok<JournalMemorySyncResponse>, ValidationProblem, ProblemHttpResult>> SyncAsync(
        SyncJournalMemoryRequest? request,
        AuthenticatedUser user,
        SyncJournalMemoryHandler handler,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        JournalMemorySyncResult result;

        try
        {
            result = await handler.HandleAsync(user.UserId, request?.Limit ?? JournalMemoryPolicy.DefaultSyncLimit, cancellationToken);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Invalid("limit", $"The limit must be between 1 and {JournalMemoryPolicy.MaxSyncLimit}.");
        }

        loggerFactory.CreateLogger(LoggerName).LogInformation(
            "Journal memory sync {Outcome}: claimed {Claimed}, indexed {Indexed}, stale {StaleDiscarded}, failed {Failed}, pending {Pending}; "
            + "chunking {ChunkingVersion}, embedding {EmbeddingProvider} {EmbeddingModel}; {ElapsedMs} ms.",
            result.Outcome, result.Claimed, result.Indexed, result.StaleDiscarded, result.Failed, result.PendingRemaining,
            JournalMemoryPolicy.ChunkingVersion, JournalMemoryPolicy.EmbeddingProvider, JournalMemoryPolicy.EmbeddingModel, Elapsed(started));

        return result.Outcome switch
        {
            JournalMemorySyncOutcome.Unavailable => Unavailable(),
            JournalMemorySyncOutcome.InvalidOutput => Failed(),
            _ => TypedResults.Ok(new JournalMemorySyncResponse(
                result.Claimed, result.Indexed, result.StaleDiscarded, result.Failed, result.PendingRemaining, result.More))
        };
    }

    // Retrieval only (limit 1–20, default 8): the evidence, best first. Never calls the answer model.
    public static async Task<Results<Ok<JournalMemorySearchResponse>, ValidationProblem, ProblemHttpResult>> SearchAsync(
        AuthenticatedUser user,
        SearchJournalMemoryHandler handler,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken,
        string? q = null,
        int? limit = null)
    {
        var started = Stopwatch.GetTimestamp();
        var result = await handler.HandleAsync(user.UserId, q, limit ?? JournalMemoryPolicy.DefaultSearchLimit, cancellationToken);

        if (result.Status == JournalMemoryQueryStatus.Invalid)
        {
            return Invalid(result.Field!, result.Message!);
        }

        loggerFactory.CreateLogger(LoggerName).LogInformation(
            "Journal memory search {Outcome}: {ResultCount} results, pending {Pending}; retrieval {RetrievalVersion}; {ElapsedMs} ms.",
            result.Status, result.Hits.Count, result.PendingEntries, JournalMemoryPolicy.RetrievalVersion, Elapsed(started));

        return result.Status switch
        {
            JournalMemoryQueryStatus.Unavailable => Unavailable(),
            JournalMemoryQueryStatus.InvalidOutput => Failed(),
            _ => TypedResults.Ok(new JournalMemorySearchResponse(
                result.Hits.Select(hit => new JournalMemorySearchResultResponse(
                    hit.EntryId, hit.OccurredAtUtc, hit.Title, hit.ChunkOrdinal, hit.ChunkText, hit.Rank, hit.VectorRank, hit.LexicalRank, hit.Score)).ToList(),
                result.IndexIncomplete,
                result.PendingEntries,
                JournalMemoryPolicy.RetrievalVersion))
        };
    }

    // A grounded answer over the retrieved passages, with citations mapped back to journal entries.
    public static async Task<Results<Ok<JournalMemoryAnswerResponse>, ValidationProblem, ProblemHttpResult>> AskAsync(
        AskJournalMemoryRequest request,
        AuthenticatedUser user,
        AskJournalMemoryHandler handler,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var result = await handler.HandleAsync(user.UserId, request.Question, cancellationToken);

        if (result.Status == JournalMemoryAskStatus.Invalid)
        {
            return Invalid(result.Field!, result.Message!);
        }

        loggerFactory.CreateLogger(LoggerName).LogInformation(
            "Journal memory ask {Outcome}: {SourceCount} sources, {CitationCount} citations, pending {Pending}; retrieval {RetrievalVersion}, "
            + "answer {AnswerProvider} {AnswerModel} prompt {PromptVersion}; {ElapsedMs} ms.",
            result.Status, result.SourceCount, result.Citations.Count, result.PendingEntries, JournalMemoryPolicy.RetrievalVersion,
            result.Generation?.Provider ?? "-", result.Generation?.Model ?? "-", result.Generation?.PromptVersion ?? "-", Elapsed(started));

        return result.Status switch
        {
            JournalMemoryAskStatus.Unavailable => Unavailable(),
            JournalMemoryAskStatus.InvalidOutput => Failed(),
            _ => TypedResults.Ok(new JournalMemoryAnswerResponse(
                result.Status == JournalMemoryAskStatus.Answered ? JournalMemoryValidation.Answered : JournalMemoryValidation.InsufficientEvidence,
                result.Answer,
                result.Citations.Select(hit => new JournalMemoryCitationResponse(
                    hit.EntryId, hit.OccurredAtUtc, hit.Title, hit.ChunkOrdinal, Excerpt(hit.ChunkText))).ToList(),
                result.IndexIncomplete,
                result.PendingEntries,
                JournalMemoryPolicy.RetrievalVersion))
        };
    }

    // The start of the chunk's own text, cut at a word boundary when longer than ExcerptLength.
    public static string Excerpt(string text)
    {
        if (text.Length <= ExcerptLength)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', ExcerptLength - 1);

        if (cut < ExcerptLength / 2)
        {
            cut = ExcerptLength - 1;
        }

        // Never split a surrogate pair.
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut].TrimEnd() + "…";
    }

    private static JournalMemoryIndexResponse IndexResponse() => new(
        JournalMemoryPolicy.ChunkingVersion,
        JournalMemoryPolicy.EmbeddingProvider,
        JournalMemoryPolicy.EmbeddingModel,
        JournalMemoryPolicy.EmbeddingDimensions,
        JournalMemoryPolicy.RetrievalVersion);

    private static ProblemHttpResult Unavailable() => TypedResults.Problem(
        title: "Journal memory unavailable", detail: UnavailableMessage, statusCode: StatusCodes.Status503ServiceUnavailable);

    private static ProblemHttpResult Failed() => TypedResults.Problem(
        title: "Journal memory failed", detail: FailedMessage, statusCode: StatusCodes.Status502BadGateway);

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static long Elapsed(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
