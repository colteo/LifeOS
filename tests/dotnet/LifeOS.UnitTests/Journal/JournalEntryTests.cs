using LifeOS.Domain.Journal;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Journal;

public class JournalEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 18, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Yesterday = new(2026, 10, 6, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_SetsEveryField_AndOccurredAtMayDifferFromCreatedAt()
    {
        var entry = JournalEntry.Create(TestUsers.A, Yesterday, "  Una giornata lunga ", "  Riflessioni\n\nsulla settimana.  ", Now);

        Assert.NotEqual(Guid.Empty, entry.Id);
        Assert.Equal(7, entry.Id.Version);
        Assert.Equal(TestUsers.A, entry.UserId);
        Assert.Equal(Yesterday, entry.OccurredAtUtc);
        Assert.Equal("Una giornata lunga", entry.Title);
        Assert.Equal("Riflessioni\n\nsulla settimana.", entry.Content);
        Assert.Equal((Now, Now), (entry.CreatedAtUtc, entry.UpdatedAtUtc));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Title_IsOptional_BlankMeansNone(string? title)
    {
        var entry = JournalEntry.Create(TestUsers.A, Yesterday, title, "Testo", Now);

        Assert.Null(entry.Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void Content_IsRequired(string content)
    {
        var exception = Assert.Throws<ArgumentException>(() => JournalEntry.Create(TestUsers.A, Yesterday, null, content, Now));

        Assert.Equal("content", exception.ParamName);
    }

    [Fact]
    public void Limits_AreInclusive_AndLongerTextIsRejected_NeverTruncated()
    {
        var entry = JournalEntry.Create(TestUsers.A, Yesterday,
            new string('t', JournalEntry.TitleMaxLength), new string('c', JournalEntry.ContentMaxLength), Now);
        Assert.Equal((JournalEntry.TitleMaxLength, JournalEntry.ContentMaxLength), (entry.Title!.Length, entry.Content.Length));

        Assert.Equal("title", Assert.Throws<ArgumentException>(() =>
            JournalEntry.Create(TestUsers.A, Yesterday, new string('t', JournalEntry.TitleMaxLength + 1), "Testo", Now)).ParamName);
        Assert.Equal("content", Assert.Throws<ArgumentException>(() =>
            JournalEntry.Create(TestUsers.A, Yesterday, null, new string('c', JournalEntry.ContentMaxLength + 1), Now)).ParamName);
    }

    [Fact]
    public void OccurredAt_IsStoredAsUtc_AtMicrosecondPrecision()
    {
        var local = new DateTimeOffset(2026, 10, 6, 23, 15, 0, TimeSpan.FromHours(2)).AddTicks(1234567);

        var entry = JournalEntry.Create(TestUsers.A, local, null, "Testo", Now.AddTicks(9));

        Assert.Equal(TimeSpan.Zero, entry.OccurredAtUtc.Offset);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 21, 15, 0, TimeSpan.Zero).AddTicks(1234560), entry.OccurredAtUtc);
        Assert.Equal(Now, entry.CreatedAtUtc);
    }

    [Fact]
    public void OccurredAt_OutsideTheSupportedRange_IsRejected()
    {
        Assert.Equal("occurredAtUtc", Assert.Throws<ArgumentOutOfRangeException>(() =>
            JournalEntry.Create(TestUsers.A, default, null, "Testo", Now)).ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => JournalEntry.Create(TestUsers.A, DateTimeOffset.MaxValue, null, "Testo", Now));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JournalEntry.Create(TestUsers.A, new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero), null, "Testo", Now));

        var earliest = JournalEntry.Create(TestUsers.A, new DateTimeOffset(1, 1, 2, 0, 0, 0, TimeSpan.Zero), null, "Testo", Now);
        Assert.Equal(1, earliest.OccurredAtUtc.Year);
    }

    [Fact]
    public void Create_RequiresAUser()
    {
        Assert.Equal("userId", Assert.Throws<ArgumentException>(() => JournalEntry.Create(Guid.Empty, Yesterday, null, "Testo", Now)).ParamName);
    }

    [Fact]
    public void Update_ReplacesOccurredAtTitleAndContent_AndStampsUpdatedAt()
    {
        var entry = JournalEntry.Create(TestUsers.A, Yesterday, "Titolo", "Prima", Now);
        var later = Now.AddHours(1);

        entry.Update(Yesterday.AddDays(-1), null, "Dopo", later);

        Assert.Equal((Yesterday.AddDays(-1), (string?)null, "Dopo"), (entry.OccurredAtUtc, entry.Title, entry.Content));
        Assert.Equal((Now, later), (entry.CreatedAtUtc, entry.UpdatedAtUtc));
        Assert.Equal(TestUsers.A, entry.UserId);
    }

    [Fact]
    public void InvalidUpdate_ChangesNothing()
    {
        var entry = JournalEntry.Create(TestUsers.A, Yesterday, "Titolo", "Prima", Now);

        Assert.Throws<ArgumentException>(() => entry.Update(Now, "Nuovo", " ", Now.AddHours(1)));

        Assert.Equal((Yesterday, "Titolo", "Prima", Now), (entry.OccurredAtUtc, entry.Title, entry.Content, entry.UpdatedAtUtc));
    }
}
