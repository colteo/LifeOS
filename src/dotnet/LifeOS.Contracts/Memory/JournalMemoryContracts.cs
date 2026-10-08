namespace LifeOS.Contracts.Memory;

// AI-004: the Journal Memory Layer API. Every route is the authenticated user's own journal memory;
// no request carries a user id. The index is derived and eventually consistent: IndexIncomplete /
// PendingEntries say when some entries' latest text is not searchable yet.

// The pinned identity of the index and of the retrieval policy.
public sealed record JournalMemoryIndexResponse(
    string ChunkingVersion,
    string EmbeddingProvider,
    string EmbeddingModel,
    int EmbeddingDimensions,
    string RetrievalVersion);

public sealed record JournalMemoryStatusResponse(
    int IndexedEntries,
    int PendingEntries,
    bool IndexIncomplete,
    JournalMemoryIndexResponse Index);

// Limit: 1–10 entries to index in this request (default 3).
public sealed record SyncJournalMemoryRequest(int? Limit);

// Failed: claimed entries that were not indexed (they stay pending). More: whether pending work remains.
public sealed record JournalMemorySyncResponse(
    int Claimed,
    int Indexed,
    int StaleDiscarded,
    int Failed,
    int PendingRemaining,
    bool More);

// One retrieved chunk: Text is the exact journal text of the chunk. Rank is the final 1-based rank;
// VectorRank/LexicalRank are the component ranks (null: not a candidate of that list); Score is the
// fused Reciprocal Rank Fusion score.
public sealed record JournalMemorySearchResultResponse(
    Guid EntryId,
    DateTimeOffset OccurredAtUtc,
    string? Title,
    int ChunkOrdinal,
    string Text,
    int Rank,
    int? VectorRank,
    int? LexicalRank,
    double Score);

public sealed record JournalMemorySearchResponse(
    IReadOnlyList<JournalMemorySearchResultResponse> Results,
    bool IndexIncomplete,
    int PendingEntries,
    string RetrievalVersion);

public sealed record AskJournalMemoryRequest(string? Question);

// A journal source the answer relies on. Excerpt is the start of the cited chunk's own text.
public sealed record JournalMemoryCitationResponse(
    Guid EntryId,
    DateTimeOffset OccurredAtUtc,
    string? Title,
    int ChunkOrdinal,
    string Excerpt);

// Status: "answered" | "insufficient_evidence". Answer is empty for insufficient_evidence.
public sealed record JournalMemoryAnswerResponse(
    string Status,
    string Answer,
    IReadOnlyList<JournalMemoryCitationResponse> Citations,
    bool IndexIncomplete,
    int PendingEntries,
    string RetrievalVersion);
