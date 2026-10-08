namespace LifeOS.Application.Memory;

// AI-004: the versioned identity and bounds of the Journal Memory Layer.
//
// The index identity (chunking version, embedding provider/model/dimensions) is pinned here: an index
// answer from the AI service with any other identity is rejected, so vectors from different models or
// chunkers are never mixed. Changing it is a deliberate re-index (a new identity), never a config flip.
//
// The retrieval policy (candidate depths, RRF constant) lives in ONE place, the PostgreSQL function
// named by RetrievalFunction, created by the AI-004 migration; RetrievalVersion names it.
public static class JournalMemoryPolicy
{
    public const string ChunkingVersion = "journal-chunking-v1";
    public const string EmbeddingProvider = "openai";
    public const string EmbeddingModel = "text-embedding-3-small";
    public const int EmbeddingDimensions = 1536;

    public const string RetrievalVersion = "journal-retrieval-v1";
    public const string RetrievalFunction = "search_journal_memory_v1";

    // The service's chunker (journal-chunking-v1) produces at most 48 chunks of at most 1200 code points
    // for the largest JRN-001 entry; 1200 code points are at most 2400 UTF-16 units.
    public const int MaxChunksPerEntry = 48;
    public const int MaxChunkLength = 2400;

    public const int MaxQuestionLength = 500;
    public const int MaxAnswerLength = 1200;

    public const int DefaultSyncLimit = 3;
    public const int MaxSyncLimit = 10;

    public const int DefaultSearchLimit = 8;
    public const int MaxSearchLimit = 20;

    // At most this many retrieved chunks are sent to the answer model (S1..S8).
    public const int AnswerContextChunks = 8;

    // A claimed job is invisible to other indexing runs until its lease expires. It must outlast one
    // AI-service call (NutritionAi:TimeoutSeconds, at most 300 s): a crashed run's job becomes
    // claimable again afterwards.
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(6);

    public static JournalMemoryIndexIdentity Identity { get; } =
        new(ChunkingVersion, EmbeddingProvider, EmbeddingModel, EmbeddingDimensions);
}

public sealed record JournalMemoryIndexIdentity(string ChunkingVersion, string EmbeddingProvider, string EmbeddingModel, int EmbeddingDimensions);
