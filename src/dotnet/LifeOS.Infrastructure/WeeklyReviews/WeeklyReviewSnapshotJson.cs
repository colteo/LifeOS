using System.Text.Json;
using LifeOS.Domain.WeeklyReviews;

namespace LifeOS.Infrastructure.WeeklyReviews;

// AUTO-002 D-3: the weekly review snapshot as a versioned JSON document (weekly_reviews.snapshot,
// jsonb). Fixed options: camelCase names in declaration order, ISO dates, exact decimals, no
// indentation. The stored data_version selects the reader; an unknown version is an error, never a
// guess. v1 rows are never rewritten: a later shape gets its own version and reader.
internal static class WeeklyReviewSnapshotJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false
    };

    public static string Serialize(int dataVersion, WeeklyReviewSnapshot snapshot) => dataVersion switch
    {
        1 => JsonSerializer.Serialize(snapshot, Options),
        _ => throw new NotSupportedException($"Weekly review data version {dataVersion} cannot be written.")
    };

    public static WeeklyReviewSnapshot Deserialize(int dataVersion, string document) => dataVersion switch
    {
        1 => JsonSerializer.Deserialize<WeeklyReviewSnapshot>(document, Options)
            ?? throw new InvalidOperationException("A weekly review snapshot is empty."),
        _ => throw new NotSupportedException($"Weekly review data version {dataVersion} is not supported.")
    };
}
