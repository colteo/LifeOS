namespace LifeOS.Application.Memory;

// AI-004: LifeOS never trusts the AI service. Every index, query vector and answer is checked here
// before it can be stored or shown; a failed check is an invalid output, never a partial result.
public static class JournalMemoryValidation
{
    public const string Answered = "answered";
    public const string InsufficientEvidence = "insufficient_evidence";

    // The index of exactly this content under the pinned identity: 1..MaxChunksPerEntry chunks with
    // contiguous ordinals from 0, each a nonblank, bounded, exact substring of the content (citations
    // show the person's own words), each with a finite vector of the pinned dimensions.
    public static bool IsValidIndex(JournalIndexedEntry indexed, string content)
    {
        if (indexed.Identity != JournalMemoryPolicy.Identity
            || indexed.Chunks is not { Count: > 0 and <= JournalMemoryPolicy.MaxChunksPerEntry } chunks)
        {
            return false;
        }

        for (var ordinal = 0; ordinal < chunks.Count; ordinal++)
        {
            var chunk = chunks[ordinal];

            if (chunk is null
                || chunk.Ordinal != ordinal
                || string.IsNullOrWhiteSpace(chunk.Text)
                || chunk.Text.Length > JournalMemoryPolicy.MaxChunkLength
                || !content.Contains(chunk.Text, StringComparison.Ordinal)
                || !IsValidVector(chunk.Embedding))
            {
                return false;
            }
        }

        return true;
    }

    // A query vector is comparable with the index only under the same provider and model.
    public static bool IsValidQueryEmbedding(JournalQueryEmbedding embedding) =>
        embedding.Provider == JournalMemoryPolicy.EmbeddingProvider
        && embedding.Model == JournalMemoryPolicy.EmbeddingModel
        && IsValidVector(embedding.Embedding);

    public static bool IsValidVector(float[]? vector) =>
        vector is { Length: JournalMemoryPolicy.EmbeddingDimensions } && Array.TrueForAll(vector, float.IsFinite);

    // answered: nonblank bounded text and at least one citation; insufficient_evidence: no text and no
    // citations. Citations are distinct and every one names a supplied source label.
    public static bool IsValidAnswer(JournalGeneratedAnswer answer, IReadOnlySet<string> labels)
    {
        if (answer.Citations is null
            || answer.Citations.Distinct(StringComparer.Ordinal).Count() != answer.Citations.Count
            || !answer.Citations.All(citation => citation is not null && labels.Contains(citation))
            || string.IsNullOrWhiteSpace(answer.Provider)
            || string.IsNullOrWhiteSpace(answer.Model)
            || string.IsNullOrWhiteSpace(answer.PromptVersion))
        {
            return false;
        }

        return answer.Status switch
        {
            Answered => !string.IsNullOrWhiteSpace(answer.Answer)
                && answer.Answer.Trim().Length <= JournalMemoryPolicy.MaxAnswerLength
                && answer.Citations.Count > 0,
            InsufficientEvidence => string.IsNullOrWhiteSpace(answer.Answer) && answer.Citations.Count == 0,
            _ => false
        };
    }
}
