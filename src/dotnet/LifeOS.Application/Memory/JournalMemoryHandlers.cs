namespace LifeOS.Application.Memory;

// AI-004: the Journal Memory Layer use cases. The journal (journal_entries) is the source of truth;
// the memory index is derived, eventually consistent and rebuilt only by an explicit, bounded sync.
// Nothing here writes a journal entry, and no question, retrieved passage or answer is persisted.

public sealed record JournalMemoryStatus(JournalMemoryCounts Counts, JournalMemoryIndexIdentity Identity, string RetrievalVersion);

public sealed class GetJournalMemoryStatusHandler(IJournalMemoryStore store)
{
    public async Task<JournalMemoryStatus> HandleAsync(Guid userId, CancellationToken cancellationToken) =>
        new(await store.GetCountsAsync(userId, JournalMemoryPolicy.Identity, cancellationToken),
            JournalMemoryPolicy.Identity,
            JournalMemoryPolicy.RetrievalVersion);
}

public enum JournalMemorySyncOutcome
{
    // Every claimed job was handled (indexed, discarded as stale, or failed and left for a retry).
    Completed,

    // The embedding service was unavailable before anything was indexed; jobs stay pending.
    Unavailable,

    // Only invalid index answers, nothing indexed; the failed jobs wait for their lease to expire.
    InvalidOutput
}

// Failed: claims not indexed because the service failed (invalid answer, or not attempted after the
// service became unavailable). They stay queued. More: whether pending work remains.
public sealed record JournalMemorySyncResult(
    JournalMemorySyncOutcome Outcome,
    int Claimed,
    int Indexed,
    int StaleDiscarded,
    int Failed,
    int PendingRemaining)
{
    public bool More => PendingRemaining > 0;
}

// Indexes up to `limit` of the user's pending entries, one claim at a time:
// 1. claim the oldest job with a unique lease token (short statement; concurrent syncs skip it),
//    reading the entry's current source;
// 2. call the AI service with NO database transaction open;
// 3. validate the answer strictly;
// 4. complete in one short transaction that writes the chunks only if the lease and the revision
//    still hold (otherwise the result is discarded: the newer revision stays queued).
// Unavailable: the claim is released (retryable at once) and the run stops. Invalid output: the job
// keeps its lease, so it is retried after the lease expires while other entries progress.
public sealed class SyncJournalMemoryHandler(IJournalMemoryStore store, IJournalEmbeddingService embeddings, TimeProvider clock)
{
    // Throws ArgumentOutOfRangeException when limit is not 1–MaxSyncLimit.
    public async Task<JournalMemorySyncResult> HandleAsync(Guid userId, int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > JournalMemoryPolicy.MaxSyncLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, $"The limit must be between 1 and {JournalMemoryPolicy.MaxSyncLimit}.");
        }

        var leaseToken = Guid.NewGuid();
        int claimed = 0, indexed = 0, stale = 0, failed = 0;
        var unavailable = false;

        while (claimed < limit)
        {
            var now = clock.GetUtcNow();

            if (await store.TryClaimNextAsync(userId, leaseToken, now, now + JournalMemoryPolicy.LeaseDuration, cancellationToken) is not { } claim)
            {
                break;
            }

            claimed++;
            var result = await embeddings.IndexEntryAsync(claim.Title, claim.Content, cancellationToken);

            if (result.Failure == JournalMemoryAiFailure.Unavailable)
            {
                await store.ReleaseAsync(claim, cancellationToken);
                failed++;
                unavailable = true;
                break;
            }

            if (result.Entry is not { } entry || !JournalMemoryValidation.IsValidIndex(entry, claim.Content))
            {
                failed++;
                continue;
            }

            switch (await store.CompleteAsync(claim, entry, clock.GetUtcNow(), cancellationToken))
            {
                case JournalIndexCompletion.Indexed:
                    indexed++;
                    break;
                default:
                    stale++;
                    break;
            }
        }

        var counts = await store.GetCountsAsync(userId, JournalMemoryPolicy.Identity, cancellationToken);
        var progressed = indexed + stale > 0;
        var outcome = unavailable && !progressed ? JournalMemorySyncOutcome.Unavailable
            : failed > 0 && !progressed ? JournalMemorySyncOutcome.InvalidOutput
            : JournalMemorySyncOutcome.Completed;

        return new JournalMemorySyncResult(outcome, claimed, indexed, stale, failed, counts.PendingEntries);
    }
}

public enum JournalMemoryQueryStatus
{
    Ok,
    Invalid,
    Unavailable,
    InvalidOutput
}

// The retrieved evidence for one question. PendingEntries > 0 means the index is incomplete: some
// entries' latest text is not (or not yet) searchable.
public sealed record JournalMemorySearchResult(
    JournalMemoryQueryStatus Status,
    IReadOnlyList<JournalMemoryHit> Hits,
    int PendingEntries,
    string? Field = null,
    string? Message = null)
{
    public bool IndexIncomplete => PendingEntries > 0;

    public static JournalMemorySearchResult Invalid(string field, string message) =>
        new(JournalMemoryQueryStatus.Invalid, [], 0, field, message);

    public static JournalMemorySearchResult Failed(JournalMemoryQueryStatus status) => new(status, [], 0);
}

// Shared by search and ask: the question's embedding, then the production hybrid retrieval.
public sealed class JournalMemoryRetrieval(IJournalMemoryStore store, IJournalEmbeddingService embeddings)
{
    public async Task<JournalMemorySearchResult> RetrieveAsync(Guid userId, string question, int limit, CancellationToken cancellationToken)
    {
        var embedded = await embeddings.EmbedQueryAsync(question, cancellationToken);

        if (embedded.Failure == JournalMemoryAiFailure.Unavailable)
        {
            return JournalMemorySearchResult.Failed(JournalMemoryQueryStatus.Unavailable);
        }

        if (embedded.Embedding is not { } embedding || !JournalMemoryValidation.IsValidQueryEmbedding(embedding))
        {
            return JournalMemorySearchResult.Failed(JournalMemoryQueryStatus.InvalidOutput);
        }

        var hits = await store.SearchAsync(userId, embedding.Embedding, question, JournalMemoryPolicy.Identity, limit, cancellationToken);
        var counts = await store.GetCountsAsync(userId, JournalMemoryPolicy.Identity, cancellationToken);

        return new JournalMemorySearchResult(JournalMemoryQueryStatus.Ok, hits, counts.PendingEntries);
    }

    // Trimmed question, or null with a readable message.
    public static string? Normalize(string? question, out string? message)
    {
        var text = question?.Trim();

        if (string.IsNullOrEmpty(text))
        {
            message = "Write a question.";
            return null;
        }

        if (text.Length > JournalMemoryPolicy.MaxQuestionLength)
        {
            message = $"The question must be at most {JournalMemoryPolicy.MaxQuestionLength} characters.";
            return null;
        }

        message = null;
        return text;
    }
}

// Retrieval only: never calls the answer model.
public sealed class SearchJournalMemoryHandler(JournalMemoryRetrieval retrieval)
{
    public async Task<JournalMemorySearchResult> HandleAsync(Guid userId, string? query, int limit, CancellationToken cancellationToken)
    {
        if (JournalMemoryRetrieval.Normalize(query, out var message) is not { } text)
        {
            return JournalMemorySearchResult.Invalid("q", message!);
        }

        if (limit is < 1 or > JournalMemoryPolicy.MaxSearchLimit)
        {
            return JournalMemorySearchResult.Invalid("limit", $"The limit must be between 1 and {JournalMemoryPolicy.MaxSearchLimit}.");
        }

        return await retrieval.RetrieveAsync(userId, text, limit, cancellationToken);
    }
}

public enum JournalMemoryAskStatus
{
    Answered,
    InsufficientEvidence,
    Invalid,
    Unavailable,
    InvalidOutput
}

// Citations are the retrieved chunks the answer cites, in citation order.
public sealed record JournalMemoryAskResult(
    JournalMemoryAskStatus Status,
    string Answer,
    IReadOnlyList<JournalMemoryHit> Citations,
    int SourceCount,
    int PendingEntries,
    JournalGeneratedAnswer? Generation = null,
    string? Field = null,
    string? Message = null)
{
    public bool IndexIncomplete => PendingEntries > 0;

    public static JournalMemoryAskResult Failed(JournalMemoryAskStatus status, int sourceCount = 0) =>
        new(status, "", [], sourceCount, 0);
}

// Retrieval first; the answer model sees only the retrieved passages (labelled S1..Sn, no ids) and is
// called only when there is at least one. With no evidence the answer is a deterministic
// insufficient_evidence. The answer is validated (status, text, citation membership) before use.
public sealed class AskJournalMemoryHandler(JournalMemoryRetrieval retrieval, IJournalAnswerService answers)
{
    public async Task<JournalMemoryAskResult> HandleAsync(Guid userId, string? question, CancellationToken cancellationToken)
    {
        if (JournalMemoryRetrieval.Normalize(question, out var message) is not { } text)
        {
            return JournalMemoryAskResult.Failed(JournalMemoryAskStatus.Invalid) with { Field = "question", Message = message };
        }

        var retrieved = await retrieval.RetrieveAsync(userId, text, JournalMemoryPolicy.AnswerContextChunks, cancellationToken);

        switch (retrieved.Status)
        {
            case JournalMemoryQueryStatus.Unavailable:
                return JournalMemoryAskResult.Failed(JournalMemoryAskStatus.Unavailable);
            case JournalMemoryQueryStatus.InvalidOutput:
                return JournalMemoryAskResult.Failed(JournalMemoryAskStatus.InvalidOutput);
        }

        var hits = retrieved.Hits;

        if (hits.Count == 0)
        {
            return new JournalMemoryAskResult(JournalMemoryAskStatus.InsufficientEvidence, "", [], 0, retrieved.PendingEntries);
        }

        var sources = hits
            .Select((hit, index) => new JournalAnswerSource($"S{index + 1}", hit.Title, hit.OccurredAtUtc, hit.ChunkText))
            .ToList();
        var byLabel = sources.Zip(hits).ToDictionary(pair => pair.First.Label, pair => pair.Second, StringComparer.Ordinal);

        var answered = await answers.AnswerAsync(text, sources, cancellationToken);

        if (answered.Failure == JournalMemoryAiFailure.Unavailable)
        {
            return JournalMemoryAskResult.Failed(JournalMemoryAskStatus.Unavailable, sources.Count);
        }

        if (answered.Answer is not { } answer || !JournalMemoryValidation.IsValidAnswer(answer, byLabel.Keys.ToHashSet(StringComparer.Ordinal)))
        {
            return JournalMemoryAskResult.Failed(JournalMemoryAskStatus.InvalidOutput, sources.Count);
        }

        return answer.Status == JournalMemoryValidation.Answered
            ? new JournalMemoryAskResult(JournalMemoryAskStatus.Answered, answer.Answer.Trim(),
                answer.Citations.Select(label => byLabel[label]).ToList(), sources.Count, retrieved.PendingEntries, answer)
            : new JournalMemoryAskResult(JournalMemoryAskStatus.InsufficientEvidence, "", [], sources.Count, retrieved.PendingEntries, answer);
    }
}
