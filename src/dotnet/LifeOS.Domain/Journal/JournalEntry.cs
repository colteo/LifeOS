namespace LifeOS.Domain.Journal;

// One private entry of the user's personal journal (JRN-001): free text, optionally titled.
//
// Time model: OccurredAtUtc is the instant the entry is about (the day of the event or thought); it is
// chosen by the user and may differ from CreatedAtUtc. The timeline is ordered by OccurredAtUtc, then
// CreatedAtUtc, then Id, all descending.
//
// Every timestamp is UTC truncated to whole microseconds, the precision PostgreSQL stores, so an entry
// returned by a write compares equal to the same entry read back.
public sealed class JournalEntry
{
    public const int TitleMaxLength = 200;
    public const int ContentMaxLength = 20_000;

    // Npgsql represents the DateTimeOffset endpoints as PostgreSQL infinities, not instants; the first
    // and last day of the range are excluded so no offset can reach them.
    public static readonly DateTime MinOccurredAtUtc = new(1, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime MaxOccurredAtUtc = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    private JournalEntry() { }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    // Null when the entry has no title.
    public string? Title { get; private set; }

    public string Content { get; private set; } = "";

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static JournalEntry Create(Guid userId, DateTimeOffset occurredAtUtc, string? title, string content, DateTimeOffset now)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A valid user id is required.", nameof(userId));
        }

        var entry = new JournalEntry
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            CreatedAtUtc = ToStoredPrecision(now)
        };

        entry.Apply(occurredAtUtc, title, content, now);

        return entry;
    }

    // Replaces the editable fields; the owner and creation time never change.
    public void Update(DateTimeOffset occurredAtUtc, string? title, string content, DateTimeOffset now) =>
        Apply(occurredAtUtc, title, content, now);

    // UTC, truncated to whole microseconds.
    public static DateTimeOffset ToStoredPrecision(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();

        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    private void Apply(DateTimeOffset occurredAtUtc, string? title, string content, DateTimeOffset now)
    {
        var occurred = NormalizeOccurredAt(occurredAtUtc);
        var normalizedTitle = NormalizeTitle(title);
        var normalizedContent = NormalizeContent(content);

        OccurredAtUtc = occurred;
        Title = normalizedTitle;
        Content = normalizedContent;
        UpdatedAtUtc = ToStoredPrecision(now);
    }

    private static DateTimeOffset NormalizeOccurredAt(DateTimeOffset occurredAtUtc)
    {
        var utc = ToStoredPrecision(occurredAtUtc);

        if (utc.UtcDateTime < MinOccurredAtUtc || utc.UtcDateTime >= MaxOccurredAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(occurredAtUtc),
                "Choose a time between 0001-01-02 and 9999-12-30 (UTC).");
        }

        return utc;
    }

    // Blank is "no title". Outer whitespace is trimmed; longer titles are rejected, never cut.
    private static string? NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var text = title.Trim();

        if (text.Length > TitleMaxLength)
        {
            throw new ArgumentException($"Title must be at most {TitleMaxLength} characters.", nameof(title));
        }

        return text;
    }

    // Outer whitespace is trimmed; internal text and line breaks are kept. Longer content is rejected,
    // never cut.
    private static string NormalizeContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Write something in the entry.", nameof(content));
        }

        var text = content.Trim();

        if (text.Length > ContentMaxLength)
        {
            throw new ArgumentException($"Content must be at most {ContentMaxLength} characters.", nameof(content));
        }

        return text;
    }
}
