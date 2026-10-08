using LifeOS.Application.Memory;

namespace LifeOS.UnitTests.Fakes;

// AI-004: scripted journal embedding service. By default every entry is one chunk (its whole content)
// with a valid vector under the pinned identity; Index/Query replace that per test. OnIndex runs while
// the "call" is in flight (e.g. to edit or delete the entry meanwhile). Records every call.
internal sealed class FakeJournalEmbeddingService : IJournalEmbeddingService
{
    public Func<string?, string, JournalIndexingResult> Index { get; set; } = (_, content) => JournalIndexingResult.Success(ValidIndex(content));

    public Func<string, JournalQueryEmbeddingResult> Query { get; set; } = _ => JournalQueryEmbeddingResult.Success(
        new JournalQueryEmbedding(JournalMemoryPolicy.EmbeddingProvider, JournalMemoryPolicy.EmbeddingModel, Vector(1)));

    public Func<Task>? OnIndex { get; set; }

    public List<(string? Title, string Content)> IndexCalls { get; } = [];

    public List<string> QueryCalls { get; } = [];

    public static float[] Vector(int seed) =>
        Enumerable.Range(0, JournalMemoryPolicy.EmbeddingDimensions).Select(index => ((seed * 31 + index * 7) % 97) / 128f - 0.375f).ToArray();

    public static JournalIndexedEntry ValidIndex(string content) =>
        new(JournalMemoryPolicy.Identity, [new JournalIndexedChunk(0, content, Vector(0))]);

    public async Task<JournalIndexingResult> IndexEntryAsync(string? title, string content, CancellationToken cancellationToken)
    {
        IndexCalls.Add((title, content));

        if (OnIndex is { } during)
        {
            await during();
        }

        return Index(title, content);
    }

    public Task<JournalQueryEmbeddingResult> EmbedQueryAsync(string question, CancellationToken cancellationToken)
    {
        QueryCalls.Add(question);
        return Task.FromResult(Query(question));
    }
}

// AI-004: scripted grounded-answer service; records every request (question and sources).
internal sealed class FakeJournalAnswerService : IJournalAnswerService
{
    public Func<IReadOnlyList<JournalAnswerSource>, JournalAnswerResult> Respond { get; set; } = sources => JournalAnswerResult.Success(
        new JournalGeneratedAnswer(JournalMemoryValidation.Answered, "You went to the sea.", [sources[0].Label], "groq", "openai/gpt-oss-20b", "journal-rag-answer-v1"));

    public List<(string Question, IReadOnlyList<JournalAnswerSource> Sources)> Calls { get; } = [];

    public Task<JournalAnswerResult> AnswerAsync(string question, IReadOnlyList<JournalAnswerSource> sources, CancellationToken cancellationToken)
    {
        Calls.Add((question, sources));
        return Task.FromResult(Respond(sources));
    }

    public static JournalAnswerResult Answer(string status, string text, params string[] citations) =>
        JournalAnswerResult.Success(new JournalGeneratedAnswer(status, text, citations, "groq", "openai/gpt-oss-20b", "journal-rag-answer-v1"));
}
