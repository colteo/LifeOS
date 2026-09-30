using LifeOS.Domain.Users;

namespace LifeOS.UnitTests.Users;

public class UserTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 9, 30, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void CreateFromExternalIdentity_WithValidInput_ReturnsUser()
    {
        var user = User.CreateFromExternalIdentity("Test Person", "person@example.com", CreatedAtUtc);

        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.Equal(7, user.Id.Version);
        Assert.Equal("Test Person", user.DisplayName);
        Assert.Equal("person@example.com", user.Email);
        Assert.Equal(CreatedAtUtc, user.CreatedAtUtc);
    }

    [Fact]
    public void CreateFromExternalIdentity_StartsWithIncompleteOnboarding()
    {
        var user = User.CreateFromExternalIdentity("Test Person", "person@example.com", CreatedAtUtc);

        Assert.Equal(OnboardingStatus.PendingFinanceProfile, user.OnboardingStatus);
        Assert.Null(user.DefaultCurrency);
        Assert.Null(user.StarterCategoriesInitializedAtUtc);
        Assert.Null(user.OnboardingCompletedAtUtc);
    }

    [Fact]
    public void CreateFromExternalIdentity_GeneratesDistinctIds()
    {
        var first = User.CreateFromExternalIdentity(null, null, CreatedAtUtc);
        var second = User.CreateFromExternalIdentity(null, null, CreatedAtUtc);

        Assert.NotEqual(first.Id, second.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateFromExternalIdentity_WithoutDisplayNameOrEmail_StoresNull(string? value)
    {
        var user = User.CreateFromExternalIdentity(value, value, CreatedAtUtc);

        Assert.Null(user.DisplayName);
        Assert.Null(user.Email);
    }

    [Fact]
    public void CreateFromExternalIdentity_TrimsDisplayNameAndEmail()
    {
        var user = User.CreateFromExternalIdentity("  Test Person ", " person@example.com  ", CreatedAtUtc);

        Assert.Equal("Test Person", user.DisplayName);
        Assert.Equal("person@example.com", user.Email);
    }

    [Fact]
    public void CreateFromExternalIdentity_NormalizesCreationTimeToUtc()
    {
        var local = new DateTimeOffset(2026, 9, 30, 12, 30, 0, TimeSpan.FromHours(2));

        var user = User.CreateFromExternalIdentity(null, null, local);

        Assert.Equal(TimeSpan.Zero, user.CreatedAtUtc.Offset);
        Assert.Equal(local.UtcDateTime, user.CreatedAtUtc.UtcDateTime);
    }
}
