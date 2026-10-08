namespace LifeOS.Application.Memory;

// AI-004: the AI capabilities of the Journal Memory Layer, implemented in Infrastructure by clients of
// the Python AI service. No provider, framework or transport is visible here. Inputs are journal text
// only: never a user id, entry id or any other LifeOS identifier. Expected failures are results,
// never exceptions. Everything returned is untrusted until JournalMemoryValidation accepts it.

public enum JournalMemoryAiFailure
{
    // Not configured, unreachable, timed out or rate-limited: try again later.
    Unavailable,

    // The service answered with something unusable (rejected, malformed, wrong size or identity).
    InvalidOutput
}

// Chunking and embeddings: the index of one entry, and the vector of one question.
public interface IJournalEmbeddingService
{
    Task<JournalIndexingResult> IndexEntryAsync(string? title, string content, CancellationToken cancellationToken);

    Task<JournalQueryEmbeddingResult> EmbedQueryAsync(string question, CancellationToken cancellationToken);
}

// Ordinal and exact source text of one chunk, with its embedding.
public sealed record JournalIndexedChunk(int Ordinal, string Text, float[] Embedding);

public sealed record JournalIndexedEntry(JournalMemoryIndexIdentity Identity, IReadOnlyList<JournalIndexedChunk> Chunks);

public sealed record JournalIndexingResult(JournalIndexedEntry? Entry, JournalMemoryAiFailure? Failure)
{
    public static JournalIndexingResult Success(JournalIndexedEntry entry) => new(entry, null);

    public static JournalIndexingResult Failed(JournalMemoryAiFailure failure) => new(null, failure);
}

public sealed record JournalQueryEmbedding(string Provider, string Model, float[] Embedding);

public sealed record JournalQueryEmbeddingResult(JournalQueryEmbedding? Embedding, JournalMemoryAiFailure? Failure)
{
    public static JournalQueryEmbeddingResult Success(JournalQueryEmbedding embedding) => new(embedding, null);

    public static JournalQueryEmbeddingResult Failed(JournalMemoryAiFailure failure) => new(null, failure);
}

// Grounded answers over retrieved passages. Labels are request-local and opaque (S1, S2, ...).
public interface IJournalAnswerService
{
    Task<JournalAnswerResult> AnswerAsync(string question, IReadOnlyList<JournalAnswerSource> sources, CancellationToken cancellationToken);
}

public sealed record JournalAnswerSource(string Label, string? Title, DateTimeOffset OccurredAtUtc, string Text);

// Status is the service's text ("answered" | "insufficient_evidence"), validated by Application.
public sealed record JournalGeneratedAnswer(string Status, string Answer, IReadOnlyList<string> Citations, string Provider, string Model, string PromptVersion);

public sealed record JournalAnswerResult(JournalGeneratedAnswer? Answer, JournalMemoryAiFailure? Failure)
{
    public static JournalAnswerResult Success(JournalGeneratedAnswer answer) => new(answer, null);

    public static JournalAnswerResult Failed(JournalMemoryAiFailure failure) => new(null, failure);
}
