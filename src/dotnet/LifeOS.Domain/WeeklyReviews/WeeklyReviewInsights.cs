namespace LifeOS.Domain.WeeklyReviews;

// AI-001: AI-generated insights about one saved weekly review. A probabilistic interpretation that
// lives NEXT TO the deterministic review, never inside it: the review's snapshot is the source of
// truth and is never changed by insights. At most one per review (generated on demand); absent until
// the user asks for it. Attributable: the output shape version and the provider, model and prompt
// version that produced it are stored with it.
public sealed class WeeklyReviewInsights
{
    // The output shape this code writes and reads. A later shape gets a new version.
    public const int CurrentOutputVersion = 1;

    public const int MaxSummaryLength = 400;
    public const int MaxStatementLength = 200;
    public const int MaxStatementsPerGroup = 3;
    public const int MaxIdentityLength = 100;

    private WeeklyReviewInsights(
        Guid reviewId,
        int outputVersion,
        WeeklyReviewInsightsContent content,
        AiGenerationIdentity generation,
        DateTimeOffset generatedAtUtc)
    {
        ReviewId = reviewId;
        OutputVersion = outputVersion;
        Content = content;
        Generation = generation;
        GeneratedAtUtc = generatedAtUtc;
    }

    public Guid ReviewId { get; }

    public int OutputVersion { get; }

    public WeeklyReviewInsightsContent Content { get; }

    public AiGenerationIdentity Generation { get; }

    public DateTimeOffset GeneratedAtUtc { get; }

    // Validates and normalizes (trims) an interpretation. Throws ArgumentException for anything
    // outside the contract: nothing partial or out of bounds is ever accepted.
    public static WeeklyReviewInsights Create(
        Guid reviewId,
        WeeklyReviewInsightsContent content,
        AiGenerationIdentity generation,
        DateTimeOffset generatedAtUtc) =>
        Restore(reviewId, CurrentOutputVersion, content, generation, generatedAtUtc);

    // Rebuilds stored insights (persistence only). Applies the same invariants as Create.
    public static WeeklyReviewInsights Restore(
        Guid reviewId,
        int outputVersion,
        WeeklyReviewInsightsContent content,
        AiGenerationIdentity generation,
        DateTimeOffset generatedAtUtc)
    {
        if (reviewId == Guid.Empty)
        {
            throw new ArgumentException("A review id is required.", nameof(reviewId));
        }

        if (outputVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(outputVersion), outputVersion, "The output version must be at least 1.");
        }

        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(generation);

        var normalized = new WeeklyReviewInsightsContent(
            Text(content.Summary, MaxSummaryLength, nameof(content.Summary)),
            Statements(content.Wins, nameof(content.Wins)),
            Statements(content.Attention, nameof(content.Attention)),
            Statements(content.Patterns, nameof(content.Patterns)),
            Statements(content.NextWeekFocus, nameof(content.NextWeekFocus)));

        var identity = new AiGenerationIdentity(
            Text(generation.Provider, MaxIdentityLength, nameof(generation.Provider)),
            Text(generation.Model, MaxIdentityLength, nameof(generation.Model)),
            Text(generation.PromptVersion, MaxIdentityLength, nameof(generation.PromptVersion)));

        return new WeeklyReviewInsights(reviewId, outputVersion, normalized, identity, generatedAtUtc.ToUniversalTime());
    }

    private static IReadOnlyList<string> Statements(IReadOnlyList<string>? statements, string field)
    {
        if (statements is null)
        {
            throw new ArgumentException($"{field} is required.", field);
        }

        if (statements.Count > MaxStatementsPerGroup)
        {
            throw new ArgumentException($"{field} has at most {MaxStatementsPerGroup} statements.", field);
        }

        return statements.Select(statement => Text(statement, MaxStatementLength, field)).ToList();
    }

    // Non-empty single-line plain text within the limit, trimmed.
    private static string Text(string? value, int maxLength, string field)
    {
        var text = value?.Trim();

        if (string.IsNullOrEmpty(text) || text.Length > maxLength || text.Any(char.IsControl))
        {
            throw new ArgumentException($"{field} must be one line of 1–{maxLength} characters.", field);
        }

        return text;
    }
}

// The structured interpretation (output version 1). Short plain sentences, each list 0–3 items.
public sealed record WeeklyReviewInsightsContent(
    string Summary,
    IReadOnlyList<string> Wins,
    IReadOnlyList<string> Attention,
    IReadOnlyList<string> Patterns,
    IReadOnlyList<string> NextWeekFocus);

// Who produced a probabilistic result: provider, model id and the versioned prompt (whose text is in
// source control, not stored per row).
public sealed record AiGenerationIdentity(string Provider, string Model, string PromptVersion);
