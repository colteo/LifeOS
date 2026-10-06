using LifeOS.Domain.WeeklyReviews;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.WeeklyReviews;

// AUTO-002: the WeeklyReview invariants (one local Monday–Sunday week, versioned snapshot) and the
// default of the module-owned setting.
public class WeeklyReviewDomainTests
{
    private static readonly DateTimeOffset Generated = new(2026, 10, 4, 20, 0, 3, TimeSpan.FromHours(2));

    private static readonly WeeklyReviewSnapshot Snapshot = new(
        new WeeklyFinanceSummary([]),
        new WeeklyGymSummary(0, 0, 0, 0, []),
        new WeeklyNutritionSummary(0, 0, 0, 0, 0m, 0m, 0m, 0m, []));

    [Fact]
    public void Create_CoversMondayToTheWeekEndingSunday_AtTheCurrentDataVersion()
    {
        var review = WeeklyReview.Create(TestUsers.A, new DateOnly(2026, 10, 4), "Europe/Rome", Generated, Snapshot);

        Assert.NotEqual(Guid.Empty, review.Id);
        Assert.Equal(7, review.Id.Version);
        Assert.Equal((new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 4)), (review.WeekStartDate, review.WeekEndDate));
        Assert.Equal((WeeklyReview.CurrentDataVersion, 1), (review.DataVersion, WeeklyReview.CurrentDataVersion));
        Assert.Equal(TimeSpan.Zero, review.GeneratedAtUtc.Offset);
        Assert.Equal(Generated, review.GeneratedAtUtc);
        Assert.Same(Snapshot, review.Snapshot);
    }

    [Theory]
    [InlineData("2026-10-03")] // Saturday
    [InlineData("2026-10-05")] // Monday
    public void Create_RejectsAWeekNotEndingOnSunday(string weekEnd)
    {
        Assert.Throws<ArgumentException>(() => WeeklyReview.Create(TestUsers.A, DateOnly.Parse(weekEnd), "Europe/Rome", Generated, Snapshot));
    }

    [Fact]
    public void Restore_AppliesTheSameInvariants()
    {
        var sunday = new DateOnly(2026, 10, 4);

        Assert.Throws<ArgumentException>(() => WeeklyReview.Restore(Guid.CreateVersion7(), TestUsers.A, sunday.AddDays(-5), sunday, "Europe/Rome", Generated, 1, Snapshot));
        Assert.Throws<ArgumentOutOfRangeException>(() => WeeklyReview.Restore(Guid.CreateVersion7(), TestUsers.A, sunday.AddDays(-6), sunday, "Europe/Rome", Generated, 0, Snapshot));
        Assert.Throws<ArgumentException>(() => WeeklyReview.Restore(Guid.Empty, TestUsers.A, sunday.AddDays(-6), sunday, "Europe/Rome", Generated, 1, Snapshot));
        Assert.Throws<ArgumentNullException>(() => WeeklyReview.Restore(Guid.CreateVersion7(), TestUsers.A, sunday.AddDays(-6), sunday, "Europe/Rome", Generated, 1, null!));
        Assert.Equal(2, WeeklyReview.Restore(Guid.CreateVersion7(), TestUsers.A, sunday.AddDays(-6), sunday, "Europe/Rome", Generated, 2, Snapshot).DataVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void Create_RequiresATimeZone(string? zone)
    {
        Assert.ThrowsAny<ArgumentException>(() => WeeklyReview.Create(TestUsers.A, new DateOnly(2026, 10, 4), zone!, Generated, Snapshot));
        Assert.Throws<ArgumentException>(() => WeeklyReview.Create(TestUsers.A, new DateOnly(2026, 10, 4), new string('a', 65), Generated, Snapshot));
    }

    [Fact]
    public void Create_RequiresAUser() =>
        Assert.Throws<ArgumentException>(() => WeeklyReview.Create(Guid.Empty, new DateOnly(2026, 10, 4), "Europe/Rome", Generated, Snapshot));

    [Fact]
    public void Settings_AreEnabledByDefault_AndOwned()
    {
        Assert.True(WeeklyReviewSettings.DefaultEnabled);
        Assert.False(WeeklyReviewSettings.Create(TestUsers.A, false, Generated).Enabled);
        Assert.Throws<ArgumentException>(() => WeeklyReviewSettings.Create(Guid.Empty, true, Generated));
    }
}
