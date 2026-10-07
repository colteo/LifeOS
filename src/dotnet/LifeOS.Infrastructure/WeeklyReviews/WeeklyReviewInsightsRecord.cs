using System.Text.Json;
using LifeOS.Domain.WeeklyReviews;

namespace LifeOS.Infrastructure.WeeklyReviews;

// The weekly_review_insights row (AI-001): one per review, separate from the immutable weekly_reviews
// row. Insert-only. The content is a versioned jsonb document read according to output_version, like
// the review snapshot (AUTO-002 D-3/D-4).
internal sealed class WeeklyReviewInsightsRecord
{
    public Guid ReviewId { get; set; }

    public int OutputVersion { get; set; }

    // jsonb document; see WeeklyReviewInsightsJson.
    public string Content { get; set; } = "";

    public string Provider { get; set; } = "";

    public string Model { get; set; } = "";

    public string PromptVersion { get; set; } = "";

    public DateTimeOffset GeneratedAtUtc { get; set; }

    public static WeeklyReviewInsightsRecord From(WeeklyReviewInsights insights) => new()
    {
        ReviewId = insights.ReviewId,
        OutputVersion = insights.OutputVersion,
        Content = WeeklyReviewInsightsJson.Serialize(insights.OutputVersion, insights.Content),
        Provider = insights.Generation.Provider,
        Model = insights.Generation.Model,
        PromptVersion = insights.Generation.PromptVersion,
        GeneratedAtUtc = insights.GeneratedAtUtc
    };

    public WeeklyReviewInsights ToDomain() => WeeklyReviewInsights.Restore(
        ReviewId, OutputVersion, WeeklyReviewInsightsJson.Deserialize(OutputVersion, Content),
        new AiGenerationIdentity(Provider, Model, PromptVersion), GeneratedAtUtc);
}

// Fixed options: camelCase names in declaration order, no indentation. The stored output_version
// selects the reader; an unknown version is an error, never a guess.
internal static class WeeklyReviewInsightsJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false
    };

    public static string Serialize(int outputVersion, WeeklyReviewInsightsContent content) => outputVersion switch
    {
        1 => JsonSerializer.Serialize(content, Options),
        _ => throw new NotSupportedException($"Weekly review insights output version {outputVersion} cannot be written.")
    };

    public static WeeklyReviewInsightsContent Deserialize(int outputVersion, string document) => outputVersion switch
    {
        1 => JsonSerializer.Deserialize<WeeklyReviewInsightsContent>(document, Options)
            ?? throw new InvalidOperationException("Weekly review insights are empty."),
        _ => throw new NotSupportedException($"Weekly review insights output version {outputVersion} is not supported.")
    };
}
